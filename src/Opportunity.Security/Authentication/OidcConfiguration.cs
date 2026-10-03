using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

using Opportunity.Application.Audit;

namespace Opportunity.Security.Authentication;

/// <summary>
/// The OIDC client (ADR-015 D3.2): authorization code + PKCE (S256), <c>state</c> and <c>nonce</c> required, issuer
/// pinned by discovery, audience = client ID, asymmetric signatures only, clock skew ≤ 2 min. Tokens are handed to
/// the session scheme on sign-in and never reach the browser.
/// </summary>
internal sealed class OidcConfiguration(IOptions<OpportunityAuthenticationOptions> settings) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string CallbackPath = "/bff/callback";
    public const string SignInFailedRedirect = "/?signin=failed";

    private static readonly string[] AsymmetricAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != AuthenticationSchemes.Oidc)
        {
            return;
        }

        var value = settings.Value;
        var oidc = value.Oidc;

        options.Authority = oidc.Authority?.ToString().TrimEnd('/');
        if (oidc.MetadataAddress is not null)
        {
            options.MetadataAddress = oidc.MetadataAddress.ToString();
        }

        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.Scope.Clear();
        foreach (var scope in oidc.EffectiveScopes)
        {
            options.Scope.Add(scope);
        }

        options.SignInScheme = AuthenticationSchemes.Session;
        options.SignOutScheme = AuthenticationSchemes.Session;
        options.CallbackPath = CallbackPath;
        options.SaveTokens = true;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = oidc.GetClaimsFromUserInfoEndpoint;

        // Keep acr (MFA, D3.6) and iss (user key, D3.3); the handler deletes both by default.
        options.ClaimActions.Remove("acr");
        options.ClaimActions.Remove("iss");
        options.ClaimActions.MapJsonKey(oidc.GroupsClaim, oidc.GroupsClaim);
        options.ClaimActions.MapJsonKey(OpportunityClaimTypes.Amr, OpportunityClaimTypes.Amr);

        var validation = options.TokenValidationParameters;
        validation.NameClaimType = OpportunityClaimTypes.Name;
        validation.RoleClaimType = oidc.GroupsClaim;
        validation.ValidAlgorithms = AsymmetricAlgorithms;
        validation.ClockSkew = oidc.ClockSkew;
        validation.ValidateIssuer = true;
        validation.ValidateAudience = true;
        validation.ValidateLifetime = true;
        validation.RequireSignedTokens = true;

        options.ProtocolValidator.RequireNonce = true;
        options.ProtocolValidator.RequireState = true;

        // __Host- cookies: Secure, Path=/, no Domain. SameSite=None is what the handler needs for a cross-site return.
        options.CorrelationCookie.Name = SessionCookies.OidcPrefix + "-correlation.";
        options.CorrelationCookie.Path = "/";
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.Name = SessionCookies.OidcPrefix + "-nonce.";
        options.NonceCookie.Path = "/";
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            if (context.Properties.Items.ContainsKey(SessionAuthenticationHandler.StepUpItem))
            {
                // Step-up (D3.6): ask for the MFA acr and a fresh authentication.
                if (value.Mfa.EffectiveStepUpAcr is { } acr)
                {
                    context.ProtocolMessage.AcrValues = acr;
                }

                context.ProtocolMessage.MaxAge = "0";
            }

            return Task.CompletedTask;
        };

        options.Events.OnRemoteFailure = async context =>
        {
            var audit = context.HttpContext.RequestServices.GetRequiredService<AuthenticationAudit>();
            await audit.AnonymousEventAsync(
                context.HttpContext,
                AuditTaxonomy.Auth.Category,
                AuditTaxonomy.Auth.SignInFailed,
                AuditOutcome.Failure,
                context.Failure?.GetType().Name ?? "RemoteFailure").ConfigureAwait(false);
            context.Response.Redirect(SignInFailedRedirect);
            context.HandleResponse();
        };

        // Front-channel logout is not supported (it is a GET that ends sessions); back-channel logout is.
        options.Events.OnRemoteSignOut = context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }
}
