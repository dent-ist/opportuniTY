using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Opportunity.Application.Audit;
using Opportunity.Application.Identity;

namespace Opportunity.Security.Authentication;

/// <summary>Builds and writes the ADR-013 <c>Auth.*</c> events (ADR-015 D13.2) through <see cref="IAuditEventWriter"/>.</summary>
internal sealed class AuthenticationAudit(
    IAuditEventWriter writer,
    IOptions<OpportunityAuthenticationOptions> options,
    TimeProvider time)
{
    private const int MaxUserAgentLength = 512;
    private const string CorrelationTag = "opportunity.correlation_id";

    private readonly byte[]? _hashKey = options.Value.Session.AuditHashKey is { } key ? Convert.FromBase64String(key) : null;

    public ValueTask SessionEventAsync(
        HttpContext? context,
        string action,
        UserSession session,
        AuditOutcome outcome = AuditOutcome.Success,
        string? reasonCode = null,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        return writer.WriteAsync(
            Create(context, action, outcome, reasonCode, details) with
            {
                ActorType = AuditActorType.User,
                ActorId = session.UserId.ToString(),
                ActorDisplay = session.DisplayName ?? session.Subject,
                SessionIdHash = HashSession(session.SessionId),
                ResourceType = "User",
                ResourceId = session.UserId.ToString(),
            },
            context?.RequestAborted ?? CancellationToken.None);
    }

    /// <summary><c>AuthZ.Denied</c> for the signed-in user (e.g. MFA required by the workspace).</summary>
    public ValueTask DeniedAsync(HttpContext context, UserSession session, Guid? workspaceId, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);

        return writer.WriteAsync(
            Create(context, AuditTaxonomy.AuthZ.Denied, AuditOutcome.Denied, reasonCode, details: null) with
            {
                Category = AuditTaxonomy.AuthZ.Category,
                WorkspaceId = workspaceId,
                ActorType = AuditActorType.User,
                ActorId = session.UserId.ToString(),
                ActorDisplay = session.DisplayName ?? session.Subject,
                SessionIdHash = HashSession(session.SessionId),
                ResourceType = workspaceId is null ? null : "Workspace",
                ResourceId = workspaceId?.ToString(),
            },
            context.RequestAborted);
    }

    /// <summary>An event without a session (e.g. <c>SignInFailed</c>, or an MFA denial before any session exists).</summary>
    public ValueTask AnonymousEventAsync(
        HttpContext context, string category, string action, AuditOutcome outcome, string reasonCode, IReadOnlyDictionary<string, string?>? details = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return writer.WriteAsync(Create(context, action, outcome, reasonCode, details) with { Category = category }, context.RequestAborted);
    }

    private AuditEvent Create(
        HttpContext? context, string action, AuditOutcome outcome, string? reasonCode, IReadOnlyDictionary<string, string?>? details)
    {
        var userAgent = context?.Request.Headers.UserAgent.ToString();
        return new AuditEvent
        {
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Auth.Category,
            Action = action,
            ActorType = AuditActorType.System,
            ActorId = "anonymous",
            ActorDisplay = "anonymous",
            ClientIp = context?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, MaxUserAgentLength)],
            Outcome = outcome,
            ReasonCode = reasonCode,
            CorrelationId = Activity.Current?.GetTagItem(CorrelationTag) as string ?? context?.TraceIdentifier,
            Details = details ?? new Dictionary<string, string?>(),
        };
    }

    private ReadOnlyMemory<byte>? HashSession(Guid sessionId)
    {
        if (_hashKey is null)
        {
            return null;
        }

        return HMACSHA256.HashData(_hashKey, Encoding.ASCII.GetBytes(sessionId.ToString("N")));
    }
}
