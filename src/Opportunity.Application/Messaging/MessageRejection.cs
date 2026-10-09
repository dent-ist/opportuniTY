using Opportunity.Application.Audit;
using Opportunity.Contracts.Messaging;

namespace Opportunity.Application.Messaging;

/// <summary>
/// Why a message was refused (E05-T07, ADR-015 D9.3/D9.5). The reason is the audit event's <c>ReasonCode</c> and the
/// dead-letter record's error class; the message itself is dead-lettered and nothing of its work is written.
/// </summary>
public static class MessageRejectionReasons
{
    /// <summary>The envelope's workspace, job, operation or idempotency key disagrees with PostgreSQL, or its work row is
    /// invisible under RLS with the hinted workspace (a forged, foreign or stale message).</summary>
    public const string EnvelopeMismatch = "EnvelopeMismatch";

    /// <summary>Envelope signing is on and the message carries no signature.</summary>
    public const string SignatureMissing = "SignatureMissing";

    /// <summary>The signature does not match the envelope (tampered body, other destination, wrong key).</summary>
    public const string SignatureInvalid = "SignatureInvalid";

    /// <summary>The signature names a key id this consumer does not accept.</summary>
    public const string SignatureKeyUnknown = "SignatureKeyUnknown";
}

/// <summary>Builds the installation-level <c>Integrity.MessageRejected</c> audit event (ADR-015 D9.3).</summary>
public static class MessageRejection
{
    /// <param name="message">The envelope as received (claims only; never trusted).</param>
    /// <param name="queue">The work queue it was consumed from.</param>
    /// <param name="reason">One of <see cref="MessageRejectionReasons"/>.</param>
    /// <param name="actorId">The rejecting worker (process id or client name).</param>
    /// <param name="resourceType">The work row type the envelope claims (e.g. <c>JobChunk</c>), or <c>Message</c>.</param>
    /// <param name="resourceId">The claimed work row id, or the message id.</param>
    /// <param name="details">Extra non-sensitive details (field names, key ids); identifiers only.</param>
    public static AuditEvent AuditEvent(
        MessageEnvelope? message,
        WorkQueue queue,
        string reason,
        string actorId,
        string resourceType,
        string? resourceId,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var all = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["claimedWorkspaceId"] = message?.WorkspaceId?.ToString(),
            ["claimedJobId"] = message?.JobId?.ToString(),
            ["messageType"] = message?.MessageType,
            ["messageId"] = message?.MessageId.ToString(),
            ["queue"] = queue.Name,
        };
        if (details is not null)
        {
            foreach (var (key, value) in details)
            {
                all[key] = value;
            }
        }

        return new AuditEvent
        {
            // Installation level: the claimed workspace is exactly what cannot be trusted.
            WorkspaceId = null,
            OccurredAt = occurredAt,
            Category = AuditTaxonomy.Integrity.Category,
            Action = AuditTaxonomy.Integrity.MessageRejected,
            ActorType = AuditActorType.Service,
            ActorId = actorId.Length > 200 ? actorId[..200] : actorId,
            ActorDisplay = queue.WorkerType,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = AuditOutcome.Denied,
            ReasonCode = reason,
            CorrelationId = message?.CorrelationId,
            Details = all,
        };
    }
}
