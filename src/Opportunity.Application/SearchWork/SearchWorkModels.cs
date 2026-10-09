using Opportunity.Application.Messaging;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;

namespace Opportunity.Application.SearchWork;

/// <summary>Lane rules of ADR-001 §5.3 (Q-10): security-affecting work never queues behind other work.</summary>
public static class SearchLanes
{
    public static MessageLane ForOutbox(SearchChangeMask mask) =>
        mask.HasFlag(SearchChangeMask.Security) ? MessageLane.Security : MessageLane.Interactive;

    public static MessageLane ForChunkTask(SearchChangeMask mask) =>
        mask.HasFlag(SearchChangeMask.Security) ? MessageLane.SecurityBulk : MessageLane.Bulk;

    public static WorkQueue Queue(MessageLane lane) => lane switch
    {
        MessageLane.Security => WorkQueues.IndexSecurity,
        MessageLane.Interactive => WorkQueues.IndexInteractive,
        MessageLane.SecurityBulk => WorkQueues.IndexSecurityBulk,
        MessageLane.Bulk => WorkQueues.IndexBulk,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Not an indexing lane."),
    };
}

/// <summary>
/// A SearchOutbox row held by one dispatcher's claim. <see cref="CreatedAt"/> is part of the row key (partitioning);
/// <see cref="AttemptCount"/> already counts this claim and is the envelope <c>attempt</c>.
/// </summary>
public sealed record ClaimedOutboxRow(
    Guid WorkspaceId,
    long OutboxId,
    DateTimeOffset CreatedAt,
    Guid DocumentId,
    long DocumentVersion,
    SearchChangeMask ChangeMask,
    MessageLane Lane,
    long SearchGeneration,
    DateTimeOffset CommittedAt,
    int AttemptCount);

/// <summary>A SearchOutbox row as stored.</summary>
public sealed record SearchOutboxRow(
    Guid WorkspaceId,
    long OutboxId,
    DateTimeOffset CreatedAt,
    Guid DocumentId,
    long DocumentVersion,
    SearchChangeMask ChangeMask,
    MessageLane Lane,
    long SearchGeneration,
    DateTimeOffset CommittedAt,
    SearchOutboxStatus Status,
    int AttemptCount,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? AppliedAt,
    string? LastError);

/// <summary>
/// The IndexChunkTask a committing chunk creates (ADR-001 §1 R2: exactly one per chunk, no SearchOutbox rows).
/// Lane, generation and CommittedAt are derived when it is written.
/// </summary>
public sealed record NewIndexChunkTask
{
    public required Guid JobId { get; init; }

    public required Guid ChunkId { get; init; }

    public required IndexTaskKind Kind { get; init; }

    /// <summary>The chunk's membership reference (ADR-010 §4); the worker resolves the documents from it.</summary>
    public required ChunkMembership Membership { get; init; }

    public required SearchChangeMask ChangeMask { get; init; }

    /// <summary>ADR-010 §5.1 key of the index work (<see cref="ChunkOperationKind.IndexChunk"/> or <c>ReindexChunk</c>).</summary>
    public required string IdempotencyKey { get; init; }

    public int MaxAttempts { get; init; } = ChunkRetryPolicy.DefaultMaxAttempts;
}

/// <summary>An IndexChunkTask held by one dispatcher's publish claim (status unchanged until it is marked dispatched).</summary>
public sealed record ClaimedIndexTask(
    Guid WorkspaceId,
    Guid TaskId,
    Guid JobId,
    Guid ChunkId,
    IndexTaskKind Kind,
    MessageLane Lane,
    long? SearchGeneration,
    DateTimeOffset CommittedAt,
    string IdempotencyKey,
    int AttemptCount);

/// <summary>An IndexChunkTask as stored, with everything the index worker reads (identifiers only).</summary>
public sealed record IndexChunkTaskInfo
{
    public required Guid WorkspaceId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid JobId { get; init; }

    public required Guid ChunkId { get; init; }

    public required IndexTaskKind Kind { get; init; }

    public required ChunkMembership Membership { get; init; }

    public required SearchChangeMask ChangeMask { get; init; }

    public required MessageLane Lane { get; init; }

    public long? SearchGeneration { get; init; }

    public required DateTimeOffset CommittedAt { get; init; }

    public required IndexChunkTaskStatus Status { get; init; }

    public required string IdempotencyKey { get; init; }

    public int AttemptCount { get; init; }

    public int MaxAttempts { get; init; }

    public long LeaseToken { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? LastError { get; init; }
}

/// <summary>Proof of an index worker's lease on a task; <see cref="LeaseToken"/> is the fencing token.</summary>
public sealed record IndexTaskLease(Guid WorkspaceId, Guid TaskId, long LeaseToken, string WorkerId);

public enum IndexTaskLeaseOutcome
{
    Leased,

    /// <summary>No such task in this workspace: an envelope/PG mismatch (ADR-010 §9); ack and drop.</summary>
    NotFound,

    /// <summary>Applied or Failed: a duplicate delivery; ack and drop.</summary>
    AlreadySettled,

    /// <summary>Another worker holds a live lease; ack and drop (lease expiry covers it).</summary>
    LeaseHeld,

    /// <summary>In RetryWait and not yet due.</summary>
    NotDue,

    /// <summary>The workspace is not Active (ADR-001 §4 R5): drop the work.</summary>
    WorkspaceNotActive,

    /// <summary>No attempts left: the task was moved to Failed.</summary>
    AttemptsExhausted,
}

public sealed record IndexTaskLeaseResult(IndexTaskLeaseOutcome Outcome, IndexChunkTaskInfo? Task = null, IndexTaskLease? Lease = null)
{
    public bool Leased => Outcome == IndexTaskLeaseOutcome.Leased;
}

public enum IndexTaskFailureOutcome
{
    RetryScheduled,
    Failed,
    LeaseLost,
}

/// <summary>What an index worker's heartbeat (fence F2) found.</summary>
public enum IndexTaskRenewal
{
    /// <summary>Lease extended: continue.</summary>
    Renewed,

    /// <summary>The fencing token no longer matches (another worker took over): stop, record nothing.</summary>
    LeaseLost,

    /// <summary>The workspace is no longer Active (ADR-001 §4 R5): stop before the next OpenSearch request.</summary>
    WorkspaceNotActive,
}

/// <param name="RetryAt">When the task becomes due again (RetryScheduled only).</param>
public sealed record IndexTaskFailureResult(IndexTaskFailureOutcome Outcome, DateTimeOffset? RetryAt);

/// <summary>Rows returned to Pending by recovery: expired claims are reclaimed directly and are not counted.</summary>
public sealed record SearchWorkRecovery(int OutboxRedispatched, int TasksLeaseExpired, int TasksRedispatched)
{
    public int Total => OutboxRedispatched + TasksLeaseExpired + TasksRedispatched;
}

/// <summary>Un-applied search work of one workspace: the source of the outbox and backlog gauges (ADR-017).</summary>
public sealed record SearchWorkBacklog(
    long OutboxUnapplied,
    DateTimeOffset? OldestOutboxCommittedAt,
    long OutboxFailed,
    long TasksUnapplied,
    DateTimeOffset? OldestTaskCommittedAt,
    long TasksFailed);

/// <summary>One examined day partition of the retention pass.</summary>
/// <param name="UnappliedRows">Rows not Applied: such a partition is kept and must raise an alert (ADR-001 R4).</param>
public sealed record SearchWorkPartition(string Table, string Partition, DateTimeOffset RangeEnd, long UnappliedRows, bool Dropped);

/// <summary>
/// SearchOutbox rows of one lane in one workspace: <paramref name="Unapplied"/> rows not yet Applied, and the oldest
/// commit among the rows not yet dispatched (Pending, Claimed or Failed) — the <c>opportunity.outbox.*</c> gauges.
/// </summary>
public sealed record OutboxLaneBacklog(MessageLane Lane, long Unapplied, DateTimeOffset? OldestUndispatchedCommittedAt);

/// <summary>
/// IndexChunkTasks of one status and lane: <paramref name="OldestSince"/> is the earliest <c>started_at</c> of Running
/// tasks (how long the oldest has been running) and the earliest <c>committed_at</c> otherwise (how long its change has
/// waited to become searchable).
/// </summary>
public sealed record IndexTaskBacklog(IndexChunkTaskStatus Status, MessageLane Lane, long Count, DateTimeOffset? OldestSince);

/// <summary>
/// Open job chunks (Pending, Dispatched, Running, RetryWait) of one job type and status: <paramref name="OldestSince"/> is
/// the earliest claim of Running chunks and the earliest last status change otherwise.
/// </summary>
public sealed record JobChunkBacklog(JobType JobType, JobChunkStatus Status, long Count, DateTimeOffset? OldestSince);

/// <summary>Jobs of one type in a non-terminal status.</summary>
public sealed record ActiveJobCount(JobType JobType, long Count);

/// <summary>The pipeline backlog of one workspace (<see cref="ISearchWorkMaintenance.GetPipelineBacklogAsync"/>).</summary>
public sealed record PipelineBacklog(
    IReadOnlyList<IndexTaskBacklog> IndexTasks, IReadOnlyList<JobChunkBacklog> JobChunks, IReadOnlyList<ActiveJobCount> ActiveJobs)
{
    public static PipelineBacklog Empty { get; } = new([], [], []);
}
