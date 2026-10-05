namespace Opportunity.Application.Messaging;

/// <summary>
/// The PostgreSQL copy of dead-lettered broker messages (ADR-010 §7.3, <c>DeadLetterRecord</c>): written by the
/// dead-letter recorder in the dispatcher, read by the job failures listing and the operations CLI. A message whose
/// envelope names an existing workspace is a workspace row (RLS); any other is an installation-level row. Records are
/// diagnostics only: nothing is ever re-published from them (replay resets PostgreSQL state, ADR-010 §7.4).
/// Implemented by <c>Opportunity.Data</c>.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>
    /// Stores <paramref name="message"/> unless a record with its message id already exists in the same scope
    /// (idempotent: a redelivered or duplicated dead-letter changes nothing). Returns where it was stored.
    /// </summary>
    Task<DeadLetterWriteOutcome> RecordAsync(DeadLetterMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records of <paramref name="workspaceId"/> (null: the installation-level records), newest first, optionally only
    /// those naming <paramref name="jobId"/>.
    /// </summary>
    Task<IReadOnlyList<DeadLetterRecord>> ListAsync(
        Guid? workspaceId, Guid? jobId, int limit, CancellationToken cancellationToken = default);

    /// <summary>One record by message id in the workspace (null: installation level), or null.</summary>
    Task<DeadLetterRecord?> GetAsync(Guid? workspaceId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Deletes records recorded before <paramref name="cutoff"/> in every workspace and at installation level.</summary>
    Task<int> DeleteRecordedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

/// <summary>
/// A dead-lettered message as the recorder read it from the broker.
/// </summary>
/// <param name="MessageId">The AMQP message id, else the envelope's, else a hash of the body; at most 64 characters.</param>
/// <param name="Queue">The work queue the message died in.</param>
/// <param name="Exchange">The dead-letter exchange it was routed through.</param>
/// <param name="RoutingKey">Its routing key there: the work queue's name, or <c>parking</c>.</param>
/// <param name="WorkspaceId">The workspace the envelope names, if any (not trusted: only a scope for the record).</param>
/// <param name="SubjectId">The work row the payload names (<c>chunkId</c> or <c>taskId</c>), if any.</param>
/// <param name="DeathReason">The transport's failure reason (e.g. <c>permanent</c>, <c>retries-exhausted</c>,
/// <c>malformed</c>) or the broker's (<c>delivery_limit</c>, <c>rejected</c>, <c>expired</c>).</param>
/// <param name="DeathCount">How often the broker dead-lettered it from <paramref name="Queue"/> (at least 1).</param>
/// <param name="HeadersJson">The AMQP headers and the envelope's fields (without payload) as JSON.</param>
/// <param name="Body">The body, cut to the recorder's limit.</param>
/// <param name="BodySize">The full body size in bytes.</param>
public sealed record DeadLetterMessage(
    string MessageId,
    string Queue,
    string Exchange,
    string RoutingKey,
    Guid? WorkspaceId,
    Guid? JobId,
    Guid? SubjectId,
    string? MessageType,
    string? CorrelationId,
    string DeathReason,
    int DeathCount,
    string? ErrorType,
    string? Error,
    DateTimeOffset? FirstDeathAt,
    string HeadersJson,
    ReadOnlyMemory<byte> Body,
    int BodySize);

public enum DeadLetterWriteOutcome
{
    /// <summary>Stored as a row of the workspace its envelope names.</summary>
    Workspace,

    /// <summary>Stored at installation level (no workspace, or one that does not exist).</summary>
    Installation,

    /// <summary>Already recorded; nothing changed.</summary>
    Duplicate,
}

/// <summary>A stored dead-letter record.</summary>
/// <param name="WorkspaceId">Null for an installation-level record.</param>
/// <param name="ClaimedWorkspaceId">Installation-level only: a workspace the envelope named that does not exist.</param>
public sealed record DeadLetterRecord(
    Guid? WorkspaceId,
    string MessageId,
    string Queue,
    string Exchange,
    string RoutingKey,
    Guid? JobId,
    Guid? SubjectId,
    string? MessageType,
    string? CorrelationId,
    string DeathReason,
    int DeathCount,
    string? ErrorType,
    string? Error,
    DateTimeOffset? FirstDeathAt,
    string HeadersJson,
    byte[] Body,
    int BodySize,
    DateTimeOffset RecordedAt,
    Guid? ClaimedWorkspaceId = null);
