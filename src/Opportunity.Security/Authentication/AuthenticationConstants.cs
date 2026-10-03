namespace Opportunity.Security.Authentication;

public static class AuthenticationSchemes
{
    /// <summary>The BFF session cookie scheme: the default for authenticate, challenge, forbid, sign-in and sign-out.</summary>
    public const string Session = "opp-session";

    /// <summary>The OIDC provider; only the login endpoint challenges it explicitly.</summary>
    public const string Oidc = "oidc";
}

/// <summary>Cookie names (ADR-015 D4.1, D4.3). The <c>__Host-</c> prefix forces Secure, Path=/ and no Domain.</summary>
public static class SessionCookies
{
    /// <summary>256-bit random session key; HttpOnly, Secure, SameSite=Lax.</summary>
    public const string Session = "__Host-opp-session";

    /// <summary>ASP.NET Core anti-forgery cookie token; HttpOnly, SameSite=Strict.</summary>
    public const string Antiforgery = "__Host-opp-csrf";

    /// <summary>
    /// The anti-forgery request token, readable by the SPA (Angular <c>HttpClient</c> XSRF support) and echoed in
    /// <see cref="AntiforgeryHeader"/> on unsafe requests.
    /// </summary>
    public const string XsrfToken = "__Host-opp-xsrf";

    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    /// <summary>Prefix of the short-lived OIDC correlation and nonce cookies.</summary>
    public const string OidcPrefix = "__Host-opp-oidc";
}

/// <summary>Claims of the request principal built from the server-side session.</summary>
public static class OpportunityClaimTypes
{
    /// <summary>The opportuniTY user ID (<c>app_user.user_id</c>).</summary>
    public const string UserId = "opp_uid";

    /// <summary>The server-side session row ID (not the cookie value).</summary>
    public const string SessionId = "opp_sid";

    public const string Issuer = "iss";
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Email = "email";

    /// <summary>One claim per IdP group (ADR-015 D3.4).</summary>
    public const string Group = "groups";

    public const string Acr = "acr";
    public const string Amr = "amr";
}
