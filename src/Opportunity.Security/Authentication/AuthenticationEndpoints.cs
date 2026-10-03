using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Opportunity.Application.Identity;
using Opportunity.Contracts.Api;
using Opportunity.Security.Http;

namespace Opportunity.Security.Authentication;

/// <summary>
/// BFF endpoints (the contract the SPA's session service uses, ADR-018): <c>GET /bff/login</c>, <c>POST /bff/logout</c>,
/// <c>POST /bff/backchannel-logout</c> (IdP to API) and <c>GET /api/v1/me</c>. The <c>/bff</c> routes are browser
/// navigation and protocol endpoints, not part of the versioned REST API, so they stay out of the OpenAPI document. The
/// OIDC callback (<c>/bff/callback</c>) is served by the OIDC handler itself.
/// </summary>
public static class AuthenticationEndpoints
{
    public const string BffPrefix = "/bff";
    public const string LoginPath = BffPrefix + "/login";
    private const int MaxReturnUrlLength = 2048;

    /// <summary>Maps <c>/bff/*</c> on <paramref name="app"/> and <c>/me</c> on the <c>/api/v1</c> group <paramref name="v1"/>.</summary>
    public static void MapOpportunityAuthenticationEndpoints(this IEndpointRouteBuilder app, IEndpointRouteBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(v1);

        var auth = app.MapGroup(BffPrefix).WithTags("Authentication");

        auth.MapGet("/login", (string? returnUrl, bool? stepUp) =>
            {
                var properties = new OpenIdConnectChallengeProperties { RedirectUri = LocalReturnUrl(returnUrl) };
                if (stepUp == true)
                {
                    properties.Items[SessionAuthenticationHandler.StepUpItem] = "1";
                }

                return TypedResults.Challenge(properties, [AuthenticationSchemes.Oidc]);
            })
            .AllowAnonymous()
            .WithSummary("Starts OIDC sign-in (authorization code + PKCE) and redirects to the identity provider.")
            .WithDescription("returnUrl must be a local path; stepUp=true requests the MFA acr and a fresh authentication.")
            .Produces(StatusCodes.Status302Found);

        auth.MapPost("/logout", async (HttpContext context, SignOutService signOut) =>
                TypedResults.Ok(new LogoutResponse(await signOut.SignOutAsync(context).ConfigureAwait(false))))
            .WithSummary("Ends the server session; the SPA then navigates to endSessionUrl to end the IdP session.");

        auth.MapPost("/backchannel-logout", async Task<Results<Ok, BadRequest<BackChannelLogoutError>>> (HttpContext context, BackChannelLogoutService logout) =>
            {
                // OpenID Connect Back-Channel Logout 1.0 §2.8: 200 on success, 400 with an OAuth error otherwise.
                var token = context.Request.HasFormContentType
                    ? (await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false))["logout_token"].ToString()
                    : null;
                return await logout.HandleAsync(context, token).ConfigureAwait(false)
                    ? TypedResults.Ok()
                    : TypedResults.BadRequest(new BackChannelLogoutError("invalid_request"));
            })
            .AllowAnonymous()
            .DisableCsrfProtection()
            .DisableAntiforgery()
            .WithSummary("OIDC back-channel logout receiver, called by the identity provider with a signed logout_token.")
            .Accepts<BackChannelLogoutForm>("application/x-www-form-urlencoded");

        v1.MapGet("/me", (HttpContext context, IOptions<OpportunityAuthenticationOptions> options) =>
            {
                var user = context.User;
                var session = context.GetUserSession();
                var settings = options.Value;
                return TypedResults.Ok(new MeResponse(
                    UserId: user.FindFirst(OpportunityClaimTypes.UserId)?.Value ?? string.Empty,
                    DisplayName: user.Identity?.Name ?? string.Empty,
                    Email: user.FindFirst(OpportunityClaimTypes.Email)?.Value,
                    Groups: [.. user.FindAll(OpportunityClaimTypes.Group).Select(c => c.Value)],
                    Mfa: user.HasMfa(settings.Mfa.ToPolicy()),
                    SessionExpiresAt: session is null ? null : SessionLifetime.ExpiresAt(session, settings.Session.ToTimeouts())));
            })
            .WithName("GetCurrentUser")
            .WithTags("Authentication")
            .WithSummary("The signed-in user. Also issues the anti-forgery token cookie for later unsafe requests.")
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    /// <summary>Only same-site paths: no scheme, no host, no protocol-relative <c>//</c> or <c>/\</c> (open redirect).</summary>
    internal static string LocalReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 and <= MaxReturnUrlLength }
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/";
}

public sealed record BackChannelLogoutError(string Error);

/// <summary>Form body of the back-channel logout request (OpenAPI description only).</summary>
public sealed record BackChannelLogoutForm([property: JsonPropertyName("logout_token")] string LogoutToken);
