using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

/// <summary>
/// Job lifecycle and progress (ADR-010). PostgreSQL owns job state: every transition is one conditional update whose
/// allowed sources come from <see cref="JobStateMachine"/>; a transition that is not allowed changes nothing and
/// returns <see cref="JobTransitionOutcome.NotAllowed"/> with the current status.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Creates the job in Created, with its <c>Job.Created</c> audit event in the same transaction; a retry with the
    /// same initiator and client key returns the existing job. Job-level failure and completion with errors are audited
    /// the same way (<c>Job.Failed</c>, <c>Job.CompletedWithErrors</c>, ADR-013 §2.4).
    /// </summary>
    Task<JobCreation> CreateAsync(NewJob job, CancellationToken cancellationToken = default);

    /// <summary>Created → Preparing: a planner takes the job to materialize its target and plan chunks.</summary>
    Task<JobTransitionResult> BeginPreparingAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Preparing → Running in one transaction with every chunk of <paramref name="request"/> (Pending, dense sequences,
    /// ADR-010 §5.1 idempotency keys) and <c>ChunksTotal</c>. A job without chunks completes immediately.
    /// </summary>
    Task<JobTransitionResult> StartAsync(JobStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Job-level permanent error (Preparing or Running → Failed). Open chunks are cancelled; running chunks are
    /// cancelled at their next fence.
    /// </summary>
    Task<JobTransitionResult> FailAsync(Guid workspaceId, Guid jobId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Running → Completed for a job that completes explicitly (<see cref="JobSettlement.CompletesExplicitly"/>): every
    /// chunk Committed and no item errors, else NotAllowed. <paramref name="reason"/> becomes the status reason.
    /// </summary>
    Task<JobTransitionResult> CompleteAsync(Guid workspaceId, Guid jobId, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Running → Paused. Running chunks return to Pending at their next fence.</summary>
    Task<JobTransitionResult> PauseAsync(Guid workspaceId, Guid jobId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Paused → Running; resets the circuit breaker.</summary>
    Task<JobTransitionResult> ResumeAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Created → Cancelled, or Preparing/Running/Paused → Cancelling: open chunks are cancelled at once, running chunks
    /// at their next fence (bounded by the lease), and the job becomes Cancelled when none is left running. Committed
    /// chunks stay (no undo, Q-34). Serializes with chunk commits on the job row (fence F3). Writes
    /// <c>Job.Cancelled</c> by <paramref name="requestedBy"/> in the same transaction.
    /// </summary>
    Task<JobTransitionResult> CancelAsync(
        Guid workspaceId, Guid jobId, Guid requestedBy, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Operator replay (ADR-010 §7.4): Failed chunks of the job (or the one given) → Pending with AttemptCount reset and
    /// ReplayCount + 1. A job in CompletedWithErrors returns to Running. A replay that moved chunks writes
    /// <c>Job.Replayed</c> in the same transaction, attributed to <paramref name="requestedBy"/> (else the job engine).
    /// </summary>
    Task<ChunkReplayResult> ReplayFailedChunksAsync(
        Guid workspaceId, Guid jobId, Guid? chunkId = null, Guid? requestedBy = null, CancellationToken cancellationToken = default);

    /// <summary>Records index tasks of the job applied by the indexing worker (the "indexed" progress, ADR-010 §10).</summary>
    Task RecordIndexTasksAppliedAsync(Guid workspaceId, Guid jobId, int count, CancellationToken cancellationToken = default);

    /// <summary>The job with its O(1) progress counters, or null.</summary>
    Task<JobInfo?> GetAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Chunks in sequence order after <paramref name="afterSequence"/>, optionally of one status.</summary>
    Task<IReadOnlyList<JobChunkInfo>> GetChunksAsync(
        Guid workspaceId, Guid jobId, JobChunkStatus? status = null, int afterSequence = 0, int limit = 500,
        CancellationToken cancellationToken = default);

    /// <summary>Item results (e.g. the Q-07 skipped-documents list), keyset-paged in chunk order.</summary>
    Task<IReadOnlyList<StoredJobItemResult>> GetItemResultsAsync(
        JobItemResultQuery query, CancellationToken cancellationToken = default);
}
