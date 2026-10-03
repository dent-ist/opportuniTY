using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Opportunity.Contracts.Api;
using Opportunity.Security.Authentication;

namespace Opportunity.Security.Http;

/// <summary>Marks an endpoint that must not use the session cookie's CSRF check (e.g. server-to-server back-channel logout).</summary>
public sealed class CsrfExemptMetadata
{
    public static CsrfExemptMetadata Instance { get; } = new();
}

public static class CsrfEndpointConventions
{
    public static TBuilder DisableCsrfProtection<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(CsrfExemptMetadata.Instance));
        return builder;
    }
}

/// <summary>
/// ADR-015 D4.3 for cookie-authenticated requests: every unsafe method needs an <c>Origin</c> equal to the configured
/// public origin <b>and</b> a valid anti-forgery token in <c>X-XSRF-TOKEN</c> (ASP.NET Core antiforgery, bound to the
/// user), otherwise 403. Safe requests of a signed-in user receive the request token in the readable
/// <c>__Host-opp-xsrf</c> cookie, which Angular's <c>HttpClient</c> echoes. Requests without the session cookie carry
/// no ambient credential and are not subject to this check (they are unauthenticated and get 401).
/// </summary>
internal sealed class CsrfProtectionMiddleware(
    RequestDelegate next,
    IAntiforgery antiforgery,
    IOptions<OpportunityAuthenticationOptions> options,
    IProblemDetailsService problems)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var identity = context.User.Identity;
        if (identity is not { IsAuthenticated: true, AuthenticationType: AuthenticationSchemes.Session })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method) || HttpMethods.IsTrace(context.Request.Method))
        {
            if (!context.Request.Cookies.ContainsKey(SessionCookies.XsrfToken))
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                context.Response.Cookies.Append(SessionCookies.XsrfToken, tokens.RequestToken!, new CookieOptions
                {
                    HttpOnly = false,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    IsEssential = true,
                });
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<CsrfExemptMetadata>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.Equals(origin, options.Value.PublicOriginString, StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context, "The request origin is not allowed.").ConfigureAwait(false);
            return;
        }

        if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
        {
            await RejectAsync(context, $"A valid anti-forgery token is required in the {SessionCookies.AntiforgeryHeader} header.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private async Task RejectAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Detail = detail,
                Extensions = { ["code"] = ProblemCodes.CsrfValidationFailed },
            },
        }).ConfigureAwait(false);
    }
}
