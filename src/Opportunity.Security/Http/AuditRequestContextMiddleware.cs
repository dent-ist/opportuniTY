using System.Diagnostics;

using Microsoft.AspNetCore.Http;

using Opportunity.Application.Audit;
using Opportunity.Application.Telemetry;
using Opportunity.Security.Authentication;

namespace Opportunity.Security.Http;

/// <summary>The request's correlation ID as every audit event and log line of the request carries it (ADR-019 §2.10).</summary>
public static class RequestCorrelation
{
    /// <summary>
    /// The ID the correlation middleware chose (<c>X-Correlation-Id</c> or the trace ID), else the span's tag, else the
    /// ASP.NET trace identifier.
    /// </summary>
    public static string Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[TelemetryAttributes.CorrelationId] as string
            ?? Activity.Current?.GetTagItem(TelemetryAttributes.CorrelationId) as string
            ?? context.TraceIdentifier;
    }

    /// <summary>Records <paramref name="correlationId"/> as the request's correlation ID.</summary>
    public static void Set(HttpContext context, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[TelemetryAttributes.CorrelationId] = correlationId;
    }
}

/// <summary>
/// After authentication: makes the request's actor, client IP, user agent, session hash and correlation ID the current
/// <see cref="AuditRequestContext"/>, so every audit event the request writes (through <see cref="IAuditEventWriter"/>
/// or inside a repository transaction) records them even when its writer did not (E14-T02).
/// </summary>
internal sealed class AuditRequestContextMiddleware(RequestDelegate next, AuthenticationAudit audit)
{
    private const int MaxUserAgentLength = AuditEventRules.MaxUserAgentLength;

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        var signedIn = user.Identity?.IsAuthenticated == true;
        var userId = signedIn ? user.FindFirst(OpportunityClaimTypes.UserId)?.Value : null;
        var sessionId = signedIn && Guid.TryParse(user.FindFirst(OpportunityClaimTypes.SessionId)?.Value, out var sid) ? sid : (Guid?)null;
        var userAgent = context.Request.Headers.UserAgent.ToString();

        using (AuditRequestContext.Enter(new AuditRequestContext
        {
            ActorId = Guid.TryParse(userId, out var id) && id != Guid.Empty ? id.ToString() : "anonymous",
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, MaxUserAgentLength)],
            SessionIdHash = sessionId is { } session ? audit.HashSession(session) : null,
            CorrelationId = RequestCorrelation.Get(context),
        }))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
