using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

using Opportunity.Application.Audit;
using Opportunity.Application.Identity;

namespace Opportunity.Security.Authentication;

/// <summary>
/// User-initiated logout (ADR-015 D4.2): revokes the server session, best-effort revokes the refresh token at the IdP
/// (RFC 7009) and returns the IdP end-session URL for the SPA to navigate to. No ID token leaves the server, so the
/// URL carries <c>client_id</c> instead of <c>id_token_hint</c>.
/// </summary>
internal sealed partial class SignOutService(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<OpportunityAuthenticationOptions> settings,
    SessionTokenProtector protector,
    ILogger<SignOutService> logger)
{
    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidcOptions = oidcOptions;
    private readonly IOptions<OpportunityAuthenticationOptions> _settings = settings;
    private readonly ILogger<SignOutService> _logger = logger;

    private SessionTokenProtector Protector { get; } = protector;

    /// <summary>Ends the current session and returns the IdP end-session URL, if the IdP advertises one.</summary>
    public async Task<string?> SignOutAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? endSessionUrl = null;
        if (context.GetUserSession() is { } session)
        {
            var oidc = _oidcOptions.Get(AuthenticationSchemes.Oidc);
            try
            {
                var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(context.RequestAborted).ConfigureAwait(false);
                await RevokeRefreshTokenAsync(oidc, configuration, Protector.Unprotect(session.ProtectedTokens).RefreshToken, context.RequestAborted)
                    .ConfigureAwait(false);
                endSessionUrl = EndSessionUrl(oidc, configuration);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                LogIdpLogoutFailed(_logger, exception.GetType().Name);
            }
        }

        await context.SignOutAsync(AuthenticationSchemes.Session).ConfigureAwait(false);
        return endSessionUrl;
    }

    private static async Task RevokeRefreshTokenAsync(
        OpenIdConnectOptions oidc, OpenIdConnectConfiguration configuration, string? refreshToken, CancellationToken cancellationToken)
    {
        var revocationEndpoint = configuration.RevocationEndpoint;
        if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(revocationEndpoint))
        {
            return;
        }

        var form = new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = oidc.ClientId!,
        };
        if (!string.IsNullOrEmpty(oidc.ClientSecret))
        {
            form["client_secret"] = oidc.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, revocationEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await oidc.Backchannel.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private string? EndSessionUrl(OpenIdConnectOptions oidc, OpenIdConnectConfiguration configuration)
    {
        if (string.IsNullOrEmpty(configuration.EndSessionEndpoint))
        {
            return null;
        }

        return QueryHelpers.AddQueryString(configuration.EndSessionEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = oidc.ClientId,
            ["post_logout_redirect_uri"] = _settings.Value.PublicOriginString + "/",
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Logout: the IdP could not be reached ({ErrorType}); the local session is ended anyway.")]
    private static partial void LogIdpLogoutFailed(ILogger logger, string errorType);
}

/// <summary>
/// OpenID Connect Back-Channel Logout 1.0 receiver (ADR-015 D3.5): validates the IdP-signed <c>logout_token</c> and
/// revokes the matching sessions immediately, by <c>sid</c> when present, otherwise by <c>sub</c>.
/// </summary>
internal sealed partial class BackChannelLogoutService(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    ISessionStore sessions,
    AuthenticationAudit audit,
    TimeProvider time,
    ILogger<BackChannelLogoutService> logger)
{
    public const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidcOptions = oidcOptions;
    private readonly ISessionStore _sessions = sessions;
    private readonly AuthenticationAudit _audit = audit;
    private readonly TimeProvider _time = time;
    private readonly ILogger<BackChannelLogoutService> _logger = logger;

    /// <summary>Returns false when the token is invalid (the endpoint answers 400).</summary>
    public async Task<bool> HandleAsync(HttpContext context, string? logoutToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(logoutToken))
        {
            return false;
        }

        var oidc = _oidcOptions.Get(AuthenticationSchemes.Oidc);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(context.RequestAborted).ConfigureAwait(false);

        var parameters = oidc.TokenValidationParameters.Clone();
        parameters.ValidIssuer = configuration.Issuer;
        parameters.ValidAudience = oidc.ClientId;
        parameters.IssuerSigningKeys = configuration.SigningKeys;
        parameters.RequireExpirationTime = false;

        var result = await oidc.TokenHandler.ValidateTokenAsync(logoutToken, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            LogRejected(_logger, result.Exception?.GetType().Name ?? "invalid");
            return false;
        }

        var claims = result.ClaimsIdentity;
        var sid = claims.FindFirst("sid")?.Value;
        var sub = claims.FindFirst(OpportunityClaimTypes.Subject)?.Value;
        if (!HasLogoutEvent(claims) || claims.HasClaim(c => c.Type == "nonce") || claims.FindFirst("iat") is null
            || (string.IsNullOrEmpty(sid) && string.IsNullOrEmpty(sub)))
        {
            LogRejected(_logger, "claims");
            return false;
        }

        var issuer = configuration.Issuer;
        var now = _time.GetUtcNow();
        var revoked = !string.IsNullOrEmpty(sid)
            ? await _sessions.RevokeByIdpSessionAsync(issuer, sid, SessionEndReason.BackChannelLogout, now, CancellationToken.None).ConfigureAwait(false)
            : await _sessions.RevokeBySubjectAsync(issuer, sub!, SessionEndReason.BackChannelLogout, now, CancellationToken.None).ConfigureAwait(false);

        foreach (var session in revoked)
        {
            await _audit.SessionEventAsync(
                context, AuditTaxonomy.Auth.SessionRevoked, session, reasonCode: nameof(SessionEndReason.BackChannelLogout)).ConfigureAwait(false);
        }

        LogRevoked(_logger, revoked.Count);
        return true;
    }

    private static bool HasLogoutEvent(ClaimsIdentity claims)
    {
        var events = claims.FindFirst("events")?.Value;
        if (string.IsNullOrEmpty(events))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(events);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(LogoutEvent, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Back-channel logout token rejected ({Reason}).")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Back-channel logout revoked {Count} session(s).")]
    private static partial void LogRevoked(ILogger logger, int count);
}
