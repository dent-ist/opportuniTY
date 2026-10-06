using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

/// <summary>A job to create in <see cref="JobStatus.Created"/> (ADR-010 §1).</summary>
public sealed record NewJob
{
    public const int MaxClientIdempotencyKeyLength = 128;

    public required Guid WorkspaceId { get; init; }

    public Guid JobId { get; init; } = Guid.CreateVersion7();

    public required JobType JobType { get; init; }

    /// <summary>The actor every chunk runs as (ADR-010 §9); workers read it from PostgreSQL, never from a message.</summary>
    public required Guid InitiatedBy { get; init; }

    /// <summary>The materialized snapshot the job works on (ADR-002), when it has one.</summary>
    public Guid? TargetSnapshotId { get; init; }

    public Guid? ImportBatchId { get; init; }

    /// <summary>Validated parameters: identifiers and settings only, no document content.</summary>
    public JsonObject? Parameters { get; init; }

    /// <summary>
    /// The HTTP <c>Idempotency-Key</c> (ADR-019 §2.6). Creating a job again with the same key and initiator returns the
    /// existing job.
    /// </summary>
    public string? ClientIdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>
    /// The operator named on the operations CLI when the job is not submitted by a user: <c>Job.Created</c> is then
    /// attributed to the CLI service with this name, and <see cref="InitiatedBy"/> is <see cref="OperationsActor.CliPrincipalId"/>.
    /// </summary>
    public string? OperatorName { get; init; }

    public int MaxAttemptsPerChunk { get; init; } = ChunkRetryPolicy.DefaultMaxAttempts;

    /// <summary>
    /// A feature-specific audit event of the submission (e.g. <c>Coding.BulkSubmitted</c>), stored with the job in the
    /// same transaction when the job is created (not for a deduplicated retry). Workspace and job are filled in.
    /// </summary>
    public AuditEvent? SubmissionAudit { get; init; }
}

/// <summary>Result of <see cref="IJobRepository.CreateAsync"/>; <see cref="Created"/> is false for a deduplicated retry.</summary>
public sealed record JobCreation(JobInfo Job, bool Created);

/// <summary>The authoritative state of a job.</summary>
public sealed record JobInfo
{
    public required Guid WorkspaceId { get; init; }

    public required Guid JobId { get; init; }

    public required JobType JobType { get; init; }

    public required JobStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public Guid? TargetSnapshotId { get; init; }

    public Guid? ImportBatchId { get; init; }

    public required JsonObject Parameters { get; init; }

    public required Guid InitiatedBy { get; init; }

    public string? ClientIdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    public ChunkOperationKind? OperationKind { get; init; }

    public long ProjectionGeneration { get; init; }

    public int MaxAttemptsPerChunk { get; init; }

    /// <summary>ADR-001 §7: generation of the job's last chunk commit; null until then.</summary>
    public long? JobGeneration { get; init; }

    public required JobCounters Counters { get; init; }

    public Guid? CancelRequestedBy { get; init; }

    public DateTimeOffset? CancelRequestedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>One chunk as planned in <see cref="JobStatus.Preparing"/>; sequences are assigned 1…n in list order.</summary>
public sealed record ChunkPlan(ChunkMembership Membership, int ItemCount, long? EstimatedBytes = null);

/// <summary>Every chunk of a job, planned up front; starting the job inserts them and moves it to Running.</summary>
public sealed record JobStartRequest(
    Guid WorkspaceId,
    Guid JobId,
    ChunkOperationKind OperationKind,
    IReadOnlyList<ChunkPlan> Chunks,
    long ProjectionGeneration = 0);

public enum JobTransitionOutcome
{
    Applied,
    NotFound,

    /// <summary>The transition is not in the matrix for the job's current status (or already happened).</summary>
    NotAllowed,
}

/// <param name="Status">The job's status after the call (null when not found).</param>
public sealed record JobTransitionResult(JobTransitionOutcome Outcome, JobStatus? Status)
{
    public bool Applied => Outcome == JobTransitionOutcome.Applied;
}

public sealed record ChunkReplayResult(JobTransitionOutcome Outcome, int ChunksReplayed, JobStatus? Status);

/// <summary>Monitoring view of one chunk.</summary>
public sealed record JobChunkInfo
{
    public required Guid WorkspaceId { get; init; }

    public required Guid JobId { get; init; }

    public required Guid ChunkId { get; init; }

    public required int Sequence { get; init; }

    public required JobChunkStatus Status { get; init; }

    public required ChunkMembership Membership { get; init; }

    public int ItemCount { get; init; }

    public long? EstimatedBytes { get; init; }

    public required string IdempotencyKey { get; init; }

    public int AttemptCount { get; init; }

    public int MaxAttempts { get; init; }

    public DateTimeOffset AvailableAt { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public long LeaseToken { get; init; }

    public string? LastError { get; init; }

    public ChunkErrorClass? ErrorClass { get; init; }

    public string? ErrorCode { get; init; }

    public int ReplayCount { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    public DateTimeOffset? SettledAt { get; init; }
}

/// <summary>
/// A per-item outcome that is not plain success: a Q-07 skip, a Q-15 exclusion or an item error. Identify the item by
/// document, or by import row when no document was created.
/// </summary>
public sealed record JobItemResult(
    JobItemResultKind Kind,
    Guid? DocumentId,
    long? RowNo,
    int? FieldId,
    string ReasonCode,
    string? Detail = null)
{
    public const int MaxReasonCodeLength = 100;
    public const int MaxDetailLength = 2_000;
}

/// <summary>A stored item result with its position (keyset: ChunkSequence, ItemNo).</summary>
public sealed record StoredJobItemResult(Guid ChunkId, int ChunkSequence, int ItemNo, JobItemResult Result);

/// <param name="After">Keyset cursor: the last (ChunkSequence, ItemNo) of the previous page.</param>
public sealed record JobItemResultQuery(Guid WorkspaceId, Guid JobId)
{
    public JobItemResultKind? Kind { get; init; }

    public (int ChunkSequence, int ItemNo)? After { get; init; }

    public int Limit { get; init; } = 500;
}
