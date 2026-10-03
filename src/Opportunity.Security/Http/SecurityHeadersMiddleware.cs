using Microsoft.AspNetCore.Http;

namespace Opportunity.Security.Http;

/// <summary>Content-Security-Policy values of API responses (ADR-015 D4.4, D12.2).</summary>
public static class ApiContentSecurityPolicies
{
    /// <summary>Every JSON response: deny everything.</summary>
    public const string Api = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// Document content streamed by the protected-content gateway (D12.2): even if a browser renders it as a document,
    /// it runs sandboxed with no script, no plugins and no subresources.
    /// </summary>
    public const string ProtectedContent = "sandbox; default-src 'none'";
}

/// <summary>
/// ADR-015 D4.4–D4.6 response headers on every API response, including errors and redirects. The API serves JSON,
/// so its CSP denies everything; the web app's CSP is set by its nginx image (deploy/docker/web). The one exception
/// is the protected-content gateway, which sets <see cref="ApiContentSecurityPolicies.ProtectedContent"/> (D12.2);
/// no other value survives.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy = ApiContentSecurityPolicies.Api;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = headers.ContentSecurityPolicy == ApiContentSecurityPolicies.ProtectedContent
                ? ApiContentSecurityPolicies.ProtectedContent
                : ContentSecurityPolicy;
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
