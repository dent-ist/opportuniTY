namespace Opportunity.Application.Jobs;

/// <summary>
/// Chunk execution with leases and fencing tokens (ADR-010 §2–§3, §7): what the consumer framework (E06-T05), the
/// dispatcher (E06-T04) and the lease sweeper call. The claim is the consumer inbox: a delivery whose claim does not
/// return <see cref="ChunkClaimOutcome.Claimed"/> is acked and dropped. Workers record every outcome here and then
/// ack; they never requeue (PostgreSQL is the retry ledger).
/// </summary>
public interface IJobChunkRepository
{
    /// <summary>
    /// Claims one chunk (Pending, Dispatched, due RetryWait, or Running with an expired lease) for
    /// <paramref name="workerId"/>: AttemptCount + 1, LeaseToken + 1, lease until now + <paramref name="leaseDuration"/>.
    /// Applies fence F1 (job Running, workspace Active) and moves a chunk without attempts left to Failed.
    /// </summary>
    Task<ChunkClaimResult> ClaimAsync(
        Guid workspaceId, Guid chunkId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the lowest-sequence claimable chunk of a Running job, skipping rows other transactions hold
    /// (<c>FOR UPDATE SKIP LOCKED</c>), so concurrent claimers never get the same chunk.
    /// </summary>
    Task<ChunkClaimResult> ClaimNextAsync(
        Guid workspaceId, Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease while the token still matches and reports the fence state (F2: call before each external side
    /// effect batch). Anything but <see cref="ChunkFence.Proceed"/> means: stop at this fence.
    /// </summary>
    Task<ChunkHeartbeat> HeartbeatAsync(ChunkLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fence F3 and commit: under the job row lock, Running → Committed only for the current lease token and only while
    /// the job is Running and the workspace Active; item results and job counters are written in the same transaction,
    /// and the job completes when its last chunk settles. Otherwise the chunk returns to Pending (job paused) or is
    /// cancelled (job cancelling), and nothing of the completion is recorded.
    /// </summary>
    Task<ChunkCommitResult> CompleteAsync(ChunkLease lease, ChunkCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed attempt: transient errors with attempts left go to RetryWait with backoff, everything else to
    /// Failed (counted, circuit breaker applied). Ignored for a stale lease token.
    /// </summary>
    Task<ChunkFailureResult> FailAsync(ChunkLease lease, ChunkError failure, CancellationToken cancellationToken = default);

    /// <summary>
    /// The worker stops at a fence (job paused or cancelling) or shuts down: Running → Pending without charging the
    /// attempt, or → Cancelled when the job is Cancelling or Failed.
    /// </summary>
    Task<ChunkReleaseOutcome> ReleaseAsync(ChunkLease lease, CancellationToken cancellationToken = default);

    /// <summary>Pending → Dispatched after a confirmed publish. False when the chunk is no longer Pending.</summary>
    Task<bool> MarkDispatchedAsync(Guid workspaceId, Guid chunkId, CancellationToken cancellationToken = default);

    /// <summary>Chunks of a job to publish: Pending, or RetryWait whose backoff elapsed, in sequence order.</summary>
    Task<IReadOnlyList<DispatchableChunk>> GetDispatchableAsync(
        Guid workspaceId, Guid jobId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Workspaces the lease sweeper visits (every workspace not yet purged; ADR-005 P10 iterates workspaces).</summary>
    Task<IReadOnlyList<Guid>> GetWorkspacesToSweepAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lease sweeper (ADR-010 §3.3), one workspace: Running chunks whose lease expired more than
    /// <paramref name="grace"/> ago return to Pending, are failed when their attempts are used up, or are cancelled when
    /// their job is cancelling. At most <paramref name="limit"/> jobs per call.
    /// </summary>
    Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
        Guid workspaceId, TimeSpan grace, int limit = 100, CancellationToken cancellationToken = default);
}
