using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Opportunity.Application.Audit;
using Opportunity.Application.Identity;
using Opportunity.Contracts.Api;

namespace Opportunity.Security.Authentication;

public sealed class SessionAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>Access to the session behind the current request (set by the session handler).</summary>
public static class SessionHttpContextExtensions
{
    internal const string ItemKey = "Opportunity.Security.UserSession";

    public static UserSession? GetUserSession(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) ? value as UserSession : null;
    }
}

/// <summary>
/// The BFF session scheme (ADR-015 D4.1, D4.2). The browser holds a 256-bit random key in <c>__Host-opp-session</c>;
/// the server keeps the session (principal snapshot and encrypted IdP tokens) in PostgreSQL under the key's SHA-256.
/// Every request enforces the idle and absolute timeouts and, every <c>PrincipalRefreshInterval</c>, re-validates the
/// user with the IdP. Sign-in is called by the OIDC handler after a successful callback and always issues a new key.
/// Challenges and denials are RFC 9457 problems, never redirects: only <c>/bff/login</c> redirects to the IdP.
/// </summary>
internal sealed partial class SessionAuthenticationHandler(
    IOptionsMonitor<SessionAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    SessionTokenProtector tokenProtector,
    OidcPrincipalRefresher refresher,
    AuthenticationAudit audit,
    IOptions<OpportunityAuthenticationOptions> settings,
    TimeProvider time,
    IProblemDetailsService problems)
    : SignInAuthenticationHandler<SessionAuthenticationOptions>(options, loggerFactory, encoder)
{
    public const string StepUpItem = "opp.step-up";
    private const int KeyBytes = 32;
    private const string EndedItem = "Opportunity.Security.SessionEnded";

    // Concurrent requests of one session share a single IdP refresh (refresh tokens may be single-use).
    private static readonly ConcurrentDictionary<Guid, Lazy<Task<PrincipalRefreshResult>>> Refreshes = new();

    private SessionTimeouts Timeouts => settings.Value.Session.ToTimeouts();

    // Resolved on use: requests without a session cookie never touch the store (or need a database).
    private ISessionStore Sessions => Context.RequestServices.GetRequiredService<ISessionStore>();

    private IUserDirectory Users => Context.RequestServices.GetRequiredService<IUserDirectory>();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryReadKeyHash(Request, out var keyHash))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await Sessions.FindActiveAsync(keyHash, CancellationToken.None).ConfigureAwait(false);
        if (session is null)
        {
            return End("The session does not exist or has ended.");
        }

        var now = time.GetUtcNow();
        var status = SessionLifetime.Evaluate(session, now, Timeouts);
        if (status != SessionStatus.Active)
        {
            var reason = SessionLifetime.EndReason(status);
            if (await Sessions.RevokeAsync(session.SessionId, reason, now, CancellationToken.None).ConfigureAwait(false))
            {
                await audit.SessionEventAsync(Context, AuditTaxonomy.Auth.SessionExpired, session, reasonCode: reason.ToString()).ConfigureAwait(false);
            }

            return End("The session has expired.");
        }

        if (SessionLifetime.NeedsPrincipalRefresh(session, now, Timeouts))
        {
            var refreshed = await RefreshPrincipalAsync(session, now).ConfigureAwait(false);
            if (refreshed is null)
            {
                return End("The identity provider no longer accepts this session.");
            }

            session = refreshed;
        }

        if (SessionLifetime.ShouldTouch(session, now, Timeouts))
        {
            await Sessions.TouchAsync(session.SessionId, now, CancellationToken.None).ConfigureAwait(false);
            session = session with { LastSeenAt = now };
        }

        Context.Items[SessionHttpContextExtensions.ItemKey] = session;
        return AuthenticateResult.Success(new AuthenticationTicket(ClaimMapping.ToPrincipal(session, Scheme.Name), Scheme.Name));
    }

    protected override async Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties)
    {
        ArgumentNullException.ThrowIfNull(user);
        properties ??= new AuthenticationProperties();

        var mapped = ClaimMapping.FromIdToken(user, settings.Value.Oidc.GroupsClaim)
            ?? throw new InvalidOperationException("The ID token has no 'sub' or 'iss' claim.");
        var (identity, context) = mapped;
        var now = time.GetUtcNow();

        // Rotate at login (D4.2): a session that was already open in this browser ends.
        if (TryReadKeyHash(Request, out var previousHash)
            && await Sessions.FindActiveAsync(previousHash, CancellationToken.None).ConfigureAwait(false) is { } previous)
        {
            await Sessions.RevokeAsync(previous.SessionId, SessionEndReason.Replaced, now, CancellationToken.None).ConfigureAwait(false);
        }

        var userId = await Users.ProvisionAsync(identity, now, CancellationToken.None).ConfigureAwait(false);
        var tokens = new SessionTokenSet(
            properties.GetTokenValue("access_token"),
            properties.GetTokenValue("refresh_token"),
            properties.GetTokenValue("id_token"),
            DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
                ? expiresAt
                : null);

        var session = new UserSession(
            SessionId: Guid.CreateVersion7(),
            UserId: userId,
            Issuer: identity.Issuer,
            Subject: identity.Subject,
            IdpSessionId: context.IdpSessionId,
            DisplayName: identity.DisplayName,
            Email: identity.Email,
            Groups: identity.Groups,
            Acr: context.Acr,
            Amr: context.Amr,
            CreatedAt: now,
            LastSeenAt: now,
            AbsoluteExpiresAt: now + settings.Value.Session.AbsoluteTimeout,
            PrincipalRefreshedAt: now,
            ProtectedTokens: tokenProtector.Protect(tokens));

        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        await Sessions.CreateAsync(SHA256.HashData(key), session, CancellationToken.None).ConfigureAwait(false);

        Response.Cookies.Append(SessionCookies.Session, WebEncoders.Base64UrlEncode(key), SessionCookieOptions());
        DeleteAntiforgeryCookies();
        Context.Items[SessionHttpContextExtensions.ItemKey] = session;

        var details = new Dictionary<string, string?>
        {
            ["idp"] = identity.Issuer,
            ["acr"] = context.Acr,
            ["amr"] = string.Join(' ', context.Amr),
        };
        await audit.SessionEventAsync(Context, AuditTaxonomy.Auth.SignIn, session, details: details).ConfigureAwait(false);
        if (properties.Items.ContainsKey(StepUpItem))
        {
            await audit.SessionEventAsync(Context, AuditTaxonomy.Auth.StepUp, session, details: details).ConfigureAwait(false);
        }

        LogSignedIn(Logger, userId);
    }

    protected override async Task HandleSignOutAsync(AuthenticationProperties? properties)
    {
        var session = Context.GetUserSession();
        if (session is null && TryReadKeyHash(Request, out var keyHash))
        {
            session = await Sessions.FindActiveAsync(keyHash, CancellationToken.None).ConfigureAwait(false);
        }

        if (session is not null
            && await Sessions.RevokeAsync(session.SessionId, SessionEndReason.SignOut, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false))
        {
            await audit.SessionEventAsync(Context, AuditTaxonomy.Auth.SignOut, session).ConfigureAwait(false);
        }

        DeleteSessionCookie();
        DeleteAntiforgeryCookies();
        Context.Items.Remove(SessionHttpContextExtensions.ItemKey);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var detail = Context.Items.TryGetValue(EndedItem, out var ended) && ended is string reason
            ? reason
            : "Authentication is required. Sign in through /bff/login.";
        return WriteProblemAsync(StatusCodes.Status401Unauthorized, ProblemCodes.Unauthorized, detail);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "The request is not permitted.");

    private async Task<UserSession?> RefreshPrincipalAsync(UserSession session, DateTimeOffset now)
    {
        var lazy = Refreshes.GetOrAdd(session.SessionId, _ => new Lazy<Task<PrincipalRefreshResult>>(
            () => refresher.RefreshAsync(session, tokenProtector.Unprotect(session.ProtectedTokens), CancellationToken.None)));
        PrincipalRefreshResult result;
        try
        {
            result = await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            Refreshes.TryRemove(new KeyValuePair<Guid, Lazy<Task<PrincipalRefreshResult>>>(session.SessionId, lazy));
        }

        switch (result)
        {
            case PrincipalRefreshResult.Refreshed refreshed:
                // Concurrent waiters share one outcome; persisting it more than once is harmless.
                await Sessions.UpdatePrincipalAsync(
                    session.SessionId, refreshed.Groups, tokenProtector.Protect(refreshed.Tokens), now, CancellationToken.None).ConfigureAwait(false);
                await Users.UpdateGroupsAsync(session.UserId, refreshed.Groups, now, CancellationToken.None).ConfigureAwait(false);
                return session with { Groups = refreshed.Groups, PrincipalRefreshedAt = now };

            case PrincipalRefreshResult.Failed failed:
                if (await Sessions.RevokeAsync(session.SessionId, SessionEndReason.PrincipalRefreshFailed, now, CancellationToken.None).ConfigureAwait(false))
                {
                    await audit.SessionEventAsync(
                        Context,
                        AuditTaxonomy.Auth.SessionRevoked,
                        session,
                        reasonCode: nameof(SessionEndReason.PrincipalRefreshFailed),
                        details: new Dictionary<string, string?> { ["refresh"] = failed.Reason }).ConfigureAwait(false);
                }

                return null;

            default:
                throw new InvalidOperationException("Unknown refresh result.");
        }
    }

    private AuthenticateResult End(string reason)
    {
        DeleteSessionCookie();
        Context.Items[EndedItem] = reason;
        return AuthenticateResult.Fail(reason);
    }

    private async Task WriteProblemAsync(int status, string code, string detail)
    {
        Response.StatusCode = status;
        Response.Headers.CacheControl = "no-store";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = new ProblemDetails { Status = status, Detail = detail, Extensions = { ["code"] = code } },
        }).ConfigureAwait(false);
    }

    private static bool TryReadKeyHash(HttpRequest request, out byte[] keyHash)
    {
        keyHash = [];
        if (!request.Cookies.TryGetValue(SessionCookies.Session, out var raw) || raw.Length is 0 or > 64)
        {
            return false;
        }

        try
        {
            var key = WebEncoders.Base64UrlDecode(raw);
            if (key.Length != KeyBytes)
            {
                return false;
            }

            keyHash = SHA256.HashData(key);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static CookieOptions SessionCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
    };

    private void DeleteSessionCookie()
    {
        if (Request.Cookies.ContainsKey(SessionCookies.Session))
        {
            Response.Cookies.Delete(SessionCookies.Session, SessionCookieOptions());
        }
    }

    // Anti-forgery tokens are bound to the user; a new session gets new ones on its first safe request.
    private void DeleteAntiforgeryCookies()
    {
        foreach (var name in new[] { SessionCookies.Antiforgery, SessionCookies.XsrfToken })
        {
            if (Request.Cookies.ContainsKey(name))
            {
                Response.Cookies.Delete(name, new CookieOptions { Secure = true, Path = "/" });
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} signed in; new server-side session created.")]
    private static partial void LogSignedIn(ILogger logger, Guid userId);
}
