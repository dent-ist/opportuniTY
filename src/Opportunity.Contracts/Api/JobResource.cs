namespace Opportunity.Contracts.Api;

/// <summary>
/// The job resource (ADR-019 §2.5): <c>GET /api/v1/workspaces/{workspaceId}/jobs/{jobId}</c> and the body of every
/// <c>202 Accepted</c>. Progress has two phases (ADR-010 §10): <see cref="Committed"/> is authoritative (PostgreSQL),
/// <see cref="Indexed"/> tells when search reflects it.
/// </summary>
public sealed record JobResource(
    Guid JobId,
    Guid WorkspaceId,
    JobResourceType JobType,
    JobResourceStatus Status,
    string? StatusReason,
    Guid InitiatedBy,
    Guid? TargetSnapshotId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    JobCommittedProgress Committed,
    JobIndexedProgress Indexed);

/// <summary>
/// PostgreSQL progress: chunks and items, from O(1) counters. <see cref="Done"/> of <see cref="Total"/> is the progress
/// bar: chunks committed in PostgreSQL of all planned chunks (failed and cancelled chunks never count as done).
/// </summary>
public sealed record JobCommittedProgress(
    long ChunksTotal,
    long ChunksCommitted,
    long ChunksFailed,
    long ChunksCancelled,
    long ChunksPending,
    long ItemsApplied,
    long ItemsUnchanged,
    long ItemsSkippedConcurrentEdit,
    long ItemsExcludedNoAccess,
    long ItemsFailed)
{
    public long Done => ChunksCommitted;

    public long Total => ChunksTotal;
}

/// <summary>Search progress: index tasks created by committed chunks and how many have been applied.</summary>
public sealed record JobIndexedProgress(JobIndexState State, long IndexTasksTotal, long IndexTasksApplied);

public enum JobIndexState
{
    /// <summary>Committed changes are still being applied to search ("search updating").</summary>
    Indexing,

    /// <summary>Every committed change of the finished job has been applied to search.</summary>
    Current,
}

public enum JobResourceType
{
    Import,
    BulkCoding,
    RelationshipFixup,
    Reindex,
    Export,
    Production,
    Render,
}

public enum JobResourceStatus
{
    Created,
    Preparing,
    Running,
    Paused,
    Cancelling,
    Cancelled,
    Completed,
    CompletedWithErrors,
    Failed,
}
