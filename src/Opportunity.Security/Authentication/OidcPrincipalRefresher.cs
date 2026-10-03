using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

using Opportunity.Application.Identity;

namespace Opportunity.Security.Authentication;

internal abstract record PrincipalRefreshResult
{
    public sealed record Refreshed(IReadOnlyList<string> Groups, SessionTokenSet Tokens) : PrincipalRefreshResult;

    public sealed record Failed(string Reason) : PrincipalRefreshResult;
}

/// <summary>
/// Re-validates the user with the IdP (ADR-015 D3.5): a refresh-token grant (validating any new ID token), or the
/// userinfo endpoint when the provider issued no refresh token. A rejected grant, e.g. because the user was disabled
/// or their IdP session ended, fails the refresh and the caller ends the session.
/// </summary>
internal sealed partial class OidcPrincipalRefresher(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<OpportunityAuthenticationOptions> options,
    TimeProvider time,
    ILogger<OidcPrincipalRefresher> logger)
{
    public async Task<PrincipalRefreshResult> RefreshAsync(UserSession session, SessionTokenSet tokens, CancellationToken cancellationToken)
    {
        var oidc = oidcOptions.Get(AuthenticationSchemes.Oidc);
        try
        {
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(tokens.RefreshToken))
            {
                return await RefreshWithGrantAsync(oidc, configuration, session, tokens, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(tokens.AccessToken) && tokens.AccessTokenExpiresAt > time.GetUtcNow())
            {
                var groups = await ReadUserInfoGroupsAsync(oidc, configuration, session, tokens.AccessToken, cancellationToken).ConfigureAwait(false);
                return groups is null ? Fail("userinfo-rejected") : new PrincipalRefreshResult.Refreshed(groups, tokens);
            }

            return Fail("no-refresh-token");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or SecurityTokenException
            or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogRefreshError(logger, exception.GetType().Name);
            return Fail("idp-unavailable");
        }
    }

    private async Task<PrincipalRefreshResult> RefreshWithGrantAsync(
        OpenIdConnectOptions oidc,
        OpenIdConnectConfiguration configuration,
        UserSession session,
        SessionTokenSet tokens,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken!,
            ["client_id"] = oidc.ClientId!,
        };
        if (!string.IsNullOrEmpty(oidc.ClientSecret))
        {
            form["client_secret"] = oidc.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await oidc.Backchannel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogGrantRejected(logger, (int)response.StatusCode);
            return Fail("refresh-rejected");
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = body.RootElement;
        var accessToken = GetString(root, "access_token");
        var refreshed = new SessionTokenSet(
            accessToken,
            GetString(root, "refresh_token") ?? tokens.RefreshToken,
            GetString(root, "id_token") ?? tokens.IdToken,
            root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds)
                ? time.GetUtcNow().AddSeconds(seconds)
                : null);

        IReadOnlyList<string>? groups;
        if (GetString(root, "id_token") is { } idToken)
        {
            var identity = await ValidateIdTokenAsync(oidc, configuration, idToken).ConfigureAwait(false);
            groups = identity is not null && identity.FindFirst(OpportunityClaimTypes.Subject)?.Value == session.Subject
                ? ClaimMapping.Groups(identity.Claims, options.Value.Oidc.GroupsClaim)
                : null;
        }
        else if (accessToken is not null)
        {
            groups = await ReadUserInfoGroupsAsync(oidc, configuration, session, accessToken, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            groups = null;
        }

        return groups is null ? Fail("refresh-invalid") : new PrincipalRefreshResult.Refreshed(groups, refreshed);
    }

    private async Task<ClaimsIdentity?> ValidateIdTokenAsync(OpenIdConnectOptions oidc, OpenIdConnectConfiguration configuration, string idToken)
    {
        var parameters = oidc.TokenValidationParameters.Clone();
        parameters.ValidIssuer = configuration.Issuer;
        parameters.ValidAudience = oidc.ClientId;
        parameters.IssuerSigningKeys = configuration.SigningKeys;

        var result = await oidc.TokenHandler.ValidateTokenAsync(idToken, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            LogIdTokenInvalid(logger, result.Exception?.GetType().Name ?? "unknown");
            return null;
        }

        return result.ClaimsIdentity;
    }

    private async Task<IReadOnlyList<string>?> ReadUserInfoGroupsAsync(
        OpenIdConnectOptions oidc, OpenIdConnectConfiguration configuration, UserSession session, string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(configuration.UserInfoEndpoint))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, configuration.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await oidc.Backchannel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogGrantRejected(logger, (int)response.StatusCode);
            return null;
        }

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = body.RootElement;
        if (GetString(root, "sub") != session.Subject)
        {
            return null;
        }

        return ClaimMapping.Groups(root, options.Value.Oidc.GroupsClaim);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static PrincipalRefreshResult.Failed Fail(string reason) => new(reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Principal refresh: the IdP rejected the request with HTTP {StatusCode}.")]
    private static partial void LogGrantRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Principal refresh: the refreshed ID token is invalid ({ErrorType}).")]
    private static partial void LogIdTokenInvalid(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Principal refresh failed ({ErrorType}); the session ends.")]
    private static partial void LogRefreshError(ILogger logger, string errorType);
}
