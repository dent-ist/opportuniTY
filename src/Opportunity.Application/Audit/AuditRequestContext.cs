namespace Opportunity.Application.Audit;

/// <summary>
/// The request an audit event is written in (E14-T02, ADR-013 §4): who is calling, from where, in which session and
/// under which correlation ID. The API sets it once per request after authentication; the audit store fills the
/// envelope fields an event leaves empty from it, so no endpoint or service has to copy them. It applies only to events
/// of the request's own actor: an event about someone else, or one written by a background loop that inherited the
/// execution context, keeps what its writer set.
/// </summary>
public sealed record AuditRequestContext
{
    /// <summary>The resource type of an event that names no other object.</summary>
    public const string WorkspaceResourceType = "Workspace";

    private static readonly AsyncLocal<AuditRequestContext?> Ambient = new();

    /// <summary>The signed-in user's ID as audit records it (<see cref="AuditEvent.ActorId"/>), or <c>anonymous</c>.</summary>
    public required string ActorId { get; init; }

    public string? ClientIp { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>HMAC-SHA-256 of the server-side session ID (ADR-013 §4); null without a session or a hash key.</summary>
    public ReadOnlyMemory<byte>? SessionIdHash { get; init; }

    /// <summary>The request's correlation ID (<c>X-Correlation-Id</c> or the trace ID, ADR-019 §2.10).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The context of the current request, if any.</summary>
    public static AuditRequestContext? Current => Ambient.Value;

    /// <summary>Makes <paramref name="context"/> current until the returned scope is disposed.</summary>
    public static IDisposable Enter(AuditRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(previous);
    }

    /// <summary>
    /// <paramref name="auditEvent"/> with its empty client, session and correlation fields taken from the current request
    /// (and its workspace as the object when it names none) when the event's actor is the request's actor; otherwise
    /// unchanged.
    /// </summary>
    public static AuditEvent Complete(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (Current is not { } context || !string.Equals(auditEvent.ActorId, context.ActorId, StringComparison.Ordinal))
        {
            return auditEvent;
        }

        // An event that names no object is about its workspace.
        var aboutWorkspace = auditEvent is { ResourceType: null, ResourceId: null, WorkspaceId: not null };
        return auditEvent with
        {
            ResourceType = aboutWorkspace ? WorkspaceResourceType : auditEvent.ResourceType,
            ResourceId = aboutWorkspace ? auditEvent.WorkspaceId!.Value.ToString() : auditEvent.ResourceId,
            ClientIp = auditEvent.ClientIp ?? context.ClientIp,
            UserAgent = auditEvent.UserAgent ?? context.UserAgent,
            SessionIdHash = auditEvent.SessionIdHash ?? context.SessionIdHash,
            CorrelationId = auditEvent.CorrelationId ?? context.CorrelationId,
        };
    }

    private sealed class Scope(AuditRequestContext? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
