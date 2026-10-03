using System.Text.Json.Nodes;

using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

/// <summary>
/// Proof of a claim. <see cref="LeaseToken"/> is the fencing token: every later claim increments it, so a worker
/// whose lease was taken over can no longer extend, commit or fail the chunk (ADR-010 §3).
/// </summary>
public sealed record ChunkLease(Guid WorkspaceId, Guid JobId, Guid ChunkId, long LeaseToken, string WorkerId);

/// <summary>
/// A claimed chunk with everything the worker needs, read from PostgreSQL: workspace, actor, job type and parameters
/// never come from the message envelope (ADR-010 §9).
/// </summary>
public sealed record ClaimedChunk
{
    public required ChunkLease Lease { get; init; }

    public required JobType JobType { get; init; }

    public required ChunkOperationKind OperationKind { get; init; }

    public required Guid InitiatedBy { get; init; }

    public Guid? TargetSnapshotId { get; init; }

    public Guid? ImportBatchId { get; init; }

    public required JsonObject Parameters { get; init; }

    public string? CorrelationId { get; init; }

    public required int Sequence { get; init; }

    public required ChunkMembership Membership { get; init; }

    public int ItemCount { get; init; }

    public long? EstimatedBytes { get; init; }

    public required string IdempotencyKey { get; init; }

    /// <summary>Attempt number of this claim (1-based); the envelope <c>attempt</c>.</summary>
    public int AttemptCount { get; init; }

    public int MaxAttempts { get; init; }

    public DateTimeOffset LeaseExpiresAt { get; init; }
}

public enum ChunkClaimOutcome
{
    Claimed,

    /// <summary>No such chunk in this workspace. For a message this is an envelope/PG mismatch (ADR-010 §9).</summary>
    NotFound,

    /// <summary>Already Committed, Cancelled or Failed: a duplicate delivery; ack and drop.</summary>
    AlreadySettled,

    /// <summary>Another worker holds a live lease: an early redelivery; ack and drop (the sweeper covers it).</summary>
    LeaseHeld,

    /// <summary>In RetryWait and not yet due.</summary>
    NotDue,

    /// <summary>
    /// Fence F1: the job is not Running (e.g. Paused) or the workspace is not Active. The chunk stays or returns to
    /// Pending for re-dispatch; ack the message.
    /// </summary>
    JobNotRunning,

    /// <summary>Fence F1 saw the job Cancelling (or Failed): the chunk was cancelled.</summary>
    Cancelled,

    /// <summary>No attempts left: the chunk was moved to Failed (ends crash loops, ADR-010 §3.1).</summary>
    AttemptsExhausted,

    /// <summary><see cref="IJobChunkRepository.ClaimNextAsync"/> found nothing claimable.</summary>
    NoneAvailable,
}

public sealed record ChunkClaimResult(ChunkClaimOutcome Outcome, ClaimedChunk? Chunk = null)
{
    public bool Claimed => Outcome == ChunkClaimOutcome.Claimed;
}

/// <summary>What a fence check (heartbeat, F2) tells the worker.</summary>
public enum ChunkFence
{
    /// <summary>Lease held, job Running, workspace Active: continue.</summary>
    Proceed,

    /// <summary>The lease token no longer matches: stop immediately, write nothing.</summary>
    LeaseLost,

    /// <summary>The job is Paused (or the workspace is not Active): stop at this fence and release the chunk.</summary>
    JobNotRunning,

    /// <summary>The job is Cancelling or Failed: stop at this fence and release the chunk (it is cancelled).</summary>
    JobCancelling,
}

public sealed record ChunkHeartbeat(ChunkFence Fence, DateTimeOffset? LeaseExpiresAt);

/// <summary>Outcome of a chunk's work, recorded by its commit transaction.</summary>
public sealed record ChunkCompletion
{
    public static readonly ChunkCompletion Empty = new();

    /// <summary>Items whose state changed.</summary>
    public long ItemsApplied { get; init; }

    /// <summary>Items already in the requested state (state-based operations, ADR-010 §5.4).</summary>
    public long ItemsUnchanged { get; init; }

    /// <summary>IndexChunkTask rows the chunk's transaction created (E06-T03); feeds the "indexed" progress.</summary>
    public int IndexTasks { get; init; }

    /// <summary>Skips, exclusions and item errors; counted into the job's item counters by kind.</summary>
    public IReadOnlyList<JobItemResult> ItemResults { get; init; } = [];
}

public enum ChunkCommitOutcome
{
    Committed,

    /// <summary>Fence F3: the lease token no longer matches; the chunk did not happen (roll back).</summary>
    LeaseLost,

    /// <summary>Fence F3: the job is Paused or the workspace not Active; the chunk returned to Pending.</summary>
    JobNotRunning,

    /// <summary>Fence F3: the job is Cancelling or Failed; the chunk was cancelled.</summary>
    Cancelled,
}

public sealed record ChunkCommitResult(ChunkCommitOutcome Outcome, JobStatus? JobStatus)
{
    public bool Committed => Outcome == ChunkCommitOutcome.Committed;
}

/// <summary>A failed attempt. <see cref="Message"/> must not contain document content (it is shown to admins).</summary>
public sealed record ChunkError(ChunkErrorClass Class, string Code, string Message)
{
    public const int MaxCodeLength = 100;
    public const int MaxMessageLength = 2_000;

    public static ChunkError Transient(string code, string message) => new(ChunkErrorClass.Transient, code, message);

    public static ChunkError Permanent(string code, string message) => new(ChunkErrorClass.Permanent, code, message);
}

public enum ChunkFailureOutcome
{
    /// <summary>Transient with attempts left: RetryWait until <see cref="ChunkFailureResult.RetryAt"/>.</summary>
    RetryScheduled,

    Failed,

    /// <summary>The job is Cancelling or Failed: the chunk was cancelled instead of retried.</summary>
    Cancelled,

    LeaseLost,
}

public sealed record ChunkFailureResult(ChunkFailureOutcome Outcome, DateTimeOffset? RetryAt, JobStatus? JobStatus);

public enum ChunkReleaseOutcome
{
    /// <summary>Back to Pending without charging the attempt.</summary>
    ReturnedToPending,

    Cancelled,

    LeaseLost,
}

/// <summary>A chunk the dispatcher may publish: Pending, or RetryWait and due.</summary>
public sealed record DispatchableChunk(
    Guid WorkspaceId, Guid JobId, Guid ChunkId, int Sequence, JobChunkStatus Status, string IdempotencyKey, int AttemptCount);

public sealed record LeaseRecoveryResult(int ReturnedToPending, int Failed, int Cancelled)
{
    public static readonly LeaseRecoveryResult None = new(0, 0, 0);

    public int Total => ReturnedToPending + Failed + Cancelled;

    public LeaseRecoveryResult Add(LeaseRecoveryResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(ReturnedToPending + other.ReturnedToPending, Failed + other.Failed, Cancelled + other.Cancelled);
    }
}
