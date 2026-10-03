using Microsoft.AspNetCore.Http;

namespace Opportunity.Security.Http;

/// <summary>
/// ADR-015 D4.4–D4.6 response headers on every API response, including errors and redirects. The API serves JSON
/// only, so its CSP denies everything; the web app's CSP is set by its nginx image (deploy/docker/web).
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            if (string.IsNullOrEmpty(headers.CacheControl))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
