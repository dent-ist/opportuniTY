using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using Opportunity.Application.Identity;

namespace Opportunity.Security.Authentication;

/// <summary>
/// <c>Authentication</c> configuration section (ADR-015 D3, D4). One OIDC provider per installation; any provider that
/// implements OpenID Connect discovery, the authorization code flow with PKCE and (optionally) back-channel logout.
/// </summary>
public sealed class OpportunityAuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// The origin users reach the application on (scheme, host, port; e.g. <c>https://review.example.com</c>).
    /// Unsafe requests must carry an <c>Origin</c> equal to it (D4.3); post-logout redirects return to it.
    /// </summary>
    [Required]
    public Uri? PublicOrigin { get; set; }

    [Required]
    [ValidateObjectMembers]
    public OidcProviderOptions Oidc { get; set; } = new();

    [Required]
    [ValidateObjectMembers]
    public SessionOptions Session { get; set; } = new();

    [Required]
    public MfaOptions Mfa { get; set; } = new();

    internal string PublicOriginString => PublicOrigin!.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// A plain-http public origin on the loopback host: the developer profile (http://localhost). Browsers treat it as a
    /// secure context and accept Secure / __Host- cookies there; the validator allows http for loopback hosts only.
    /// </summary>
    internal bool IsLoopbackHttpOrigin => PublicOrigin is { Scheme: "http", IsLoopback: true };
}

public sealed class OidcProviderOptions
{
    /// <summary>The issuer URL; discovery is read from <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    [Required]
    public Uri? Authority { get; set; }

    /// <summary>
    /// Optional discovery URL when the API reaches the IdP on a different address than browsers do (e.g. a container
    /// network). The issuer in the document must still be <see cref="Authority"/>'s issuer.
    /// </summary>
    public Uri? MetadataAddress { get; set; }

    [Required]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Confidential-client secret (D3.2). Supply it through a secret store or <c>*_FILE</c>, never committed.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Requested scopes; empty means <c>openid profile email</c>.</summary>
    public IList<string> Scopes { get; } = [];

    /// <summary>ID-token or userinfo claim that carries the user's groups (D3.4).</summary>
    [Required]
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>Only for a local developer IdP over plain HTTP. Never disable in Full.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Also read claims from the userinfo endpoint at sign-in (for providers that omit groups from the ID token).</summary>
    public bool GetClaimsFromUserInfoEndpoint { get; set; }

    [Range(typeof(TimeSpan), "00:00:00", "00:02:00")]
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    internal IReadOnlyList<string> EffectiveScopes => Scopes.Count > 0 ? [.. Scopes] : ["openid", "profile", "email"];
}

public sealed class SessionOptions
{
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan IdleTimeout { get; set; } = SessionTimeouts.Default.Idle;

    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan AbsoluteTimeout { get; set; } = SessionTimeouts.Default.Absolute;

    /// <summary>Re-validation with the IdP; ADR-015 D3.5 allows 5–60 min.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "01:00:00")]
    public TimeSpan PrincipalRefreshInterval { get; set; } = SessionTimeouts.Default.PrincipalRefresh;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan TouchInterval { get; set; } = SessionTimeouts.Default.TouchInterval;

    /// <summary>
    /// Base64 key (≥ 32 bytes) for the audit <c>SessionIdHash</c> HMAC (ADR-013 §4). Without it audit events carry no
    /// session hash. Managed through the secret provider once E05-T09 lands.
    /// </summary>
    public string? AuditHashKey { get; set; }

    internal SessionTimeouts ToTimeouts() => new(IdleTimeout, AbsoluteTimeout, PrincipalRefreshInterval, TouchInterval);
}

/// <summary>What counts as MFA (D3.6). Empty means MFA cannot be satisfied, so MFA-protected resources stay closed.</summary>
public sealed class MfaOptions
{
    public IList<string> AcrValues { get; } = [];

    public IList<string> AmrValues { get; } = [];

    /// <summary><c>acr_values</c> sent on a step-up request; defaults to the first of <see cref="AcrValues"/>.</summary>
    public string? StepUpAcrValue { get; set; }

    internal MfaPolicy ToPolicy() => new([.. AcrValues], [.. AmrValues]);

    internal string? EffectiveStepUpAcr => StepUpAcrValue ?? AcrValues.FirstOrDefault();
}

internal sealed class OpportunityAuthenticationOptionsValidator : IValidateOptions<OpportunityAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, OpportunityAuthenticationOptions options)
    {
        var failures = new List<string>();
        if (options.PublicOrigin is { } origin && (!origin.IsAbsoluteUri || origin.AbsolutePath != "/" || origin.Query.Length > 0))
        {
            failures.Add("Authentication:PublicOrigin must be an origin such as https://review.example.com (no path).");
        }

        if (options.PublicOrigin is { IsAbsoluteUri: true } publicOrigin
            && (publicOrigin.Scheme is not ("https" or "http") || (publicOrigin.Scheme == "http" && !publicOrigin.IsLoopback)))
        {
            failures.Add("Authentication:PublicOrigin must use https; plain http is allowed only for localhost (developer profile).");
        }

        if (options.Oidc.Authority is { IsAbsoluteUri: false })
        {
            failures.Add("Authentication:Oidc:Authority must be an absolute URL.");
        }

        if (options.Oidc.RequireHttpsMetadata && options.Oidc.Authority is { Scheme: not "https" })
        {
            failures.Add("Authentication:Oidc:Authority must use https unless RequireHttpsMetadata is false (developer IdP only).");
        }

        if (!options.Oidc.EffectiveScopes.Contains("openid"))
        {
            failures.Add("Authentication:Oidc:Scopes must include 'openid'.");
        }

        if (options.Session.AbsoluteTimeout < options.Session.IdleTimeout)
        {
            failures.Add("Authentication:Session:AbsoluteTimeout must not be shorter than IdleTimeout.");
        }

        if (options.Session.AuditHashKey is { } key && !IsKey(key))
        {
            failures.Add("Authentication:Session:AuditHashKey must be base64 of at least 32 bytes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsKey(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) && written >= 32;
    }
}
