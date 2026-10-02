using System.Diagnostics;

namespace Opportunity.Api.Conventions;

/// <summary>
/// ADR-019 §2.10: echoes <c>X-Correlation-Id</c> (or the W3C trace id when the client sent none or an unusable one)
/// and adds it to the logging scope. <c>traceparent</c> itself is handled by ASP.NET Core's activity propagation.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsUsable(supplied)
            ? supplied
            : Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    private static bool IsUsable(string value) =>
        value.Length is > 0 and <= MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
