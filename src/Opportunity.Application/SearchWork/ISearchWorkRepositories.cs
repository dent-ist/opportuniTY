using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;

namespace Opportunity.Application.SearchWork;

/// <summary>
/// The interactive search outbox (ADR-001 §1, §5, §6). Rows are created only by the transaction that changes the
/// authoritative state (the coding store does this for interactive writes); this port moves them through
/// Pending → Claimed → Dispatched → Applied. Every call is scoped to one workspace (RLS).
/// </summary>
public interface ISearchOutboxRepository
{
    /// <summary>
    /// Claims up to <paramref name="limit"/> Pending (or expired-claim) rows that are due, by lane then generation, with
    /// <c>FOR UPDATE SKIP LOCKED</c> so concurrent dispatchers never claim the same row. Each claim counts an attempt;
    /// rows that have used up their attempts are moved to Failed instead.
    /// </summary>
    Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
        Guid workspaceId, string owner, int limit, TimeSpan claimDuration, CancellationToken cancellationToken = default);

    /// <summary>Claimed → Dispatched for rows this owner still holds, after the broker confirmed them. Returns the count.</summary>
    Task<int> MarkDispatchedAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<ClaimedOutboxRow> rows, CancellationToken cancellationToken = default);

    /// <summary>
    /// The publish was not confirmed: Claimed → Pending with backoff (or Failed when the attempts are used up).
    /// </summary>
    Task<int> ReleaseAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<ClaimedOutboxRow> rows, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Index worker coalescing (ADR-001 §5.2): after writing <paramref name="documentId"/> at
    /// <paramref name="documentVersion"/>, every non-applied row of that document with a version ≤ it becomes Applied,
    /// whatever its status. Returns the number of rows applied.
    /// </summary>
    Task<int> MarkAppliedThroughAsync(
        Guid workspaceId, Guid documentId, long documentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// The index worker could not apply a Dispatched row (ADR-001 §6.4: retry state lives in PostgreSQL): it returns to
    /// Pending with the outbox backoff for the dispatcher to publish again, or becomes Failed when
    /// <paramref name="permanent"/> or its attempts are used up. Rows in any other status are left alone (another
    /// delivery is already on its way, or the row was applied meanwhile). Returns the resulting status, or null when
    /// nothing changed.
    /// </summary>
    Task<SearchOutboxStatus?> ReturnUnappliedAsync(
        Guid workspaceId, long outboxId, string reason, bool permanent, CancellationToken cancellationToken = default);

    Task<SearchOutboxRow?> GetAsync(Guid workspaceId, long outboxId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Chunk-level index tasks (§21, ADR-001 §1, ADR-010 §2–§3). Created only by the committing transaction of a job chunk
/// (bulk coding through <c>ICodingRepository.ApplyChunkAsync</c>; import and fix-ups through the Data-layer chunk
/// commit). The dispatcher claims and publishes them; the index worker leases, completes or fails them.
/// </summary>
public interface IIndexChunkTaskRepository
{
    /// <summary>Publish claim on due Pending/RetryWait tasks (status unchanged), skipping rows other dispatchers hold.</summary>
    Task<IReadOnlyList<ClaimedIndexTask>> ClaimForDispatchAsync(
        Guid workspaceId, string owner, int limit, TimeSpan claimDuration, CancellationToken cancellationToken = default);

    /// <summary>Pending/RetryWait → Dispatched for tasks this owner still holds, after a broker confirm.</summary>
    Task<int> MarkDispatchedAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default);

    /// <summary>Drops the publish claim after an unconfirmed publish; the task stays due after a short delay.</summary>
    Task<int> ReleaseClaimAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, TimeSpan retryAfter, CancellationToken cancellationToken = default);

    /// <summary>
    /// The index worker's claim (the consumer inbox, ADR-010 §5.3): Running with a fresh fencing token and an attempt
    /// counted. Anything but <see cref="IndexTaskLeaseOutcome.Leased"/> means ack and drop.
    /// </summary>
    Task<IndexTaskLeaseResult> LeaseAsync(
        Guid workspaceId, Guid taskId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Running → Applied for the current token; counts the job's applied index tasks. False when the lease was lost.</summary>
    Task<bool> CompleteAsync(IndexTaskLease lease, CancellationToken cancellationToken = default);

    /// <summary>Transient with attempts left → RetryWait with backoff; otherwise Failed (replayable, never cancelled).</summary>
    Task<IndexTaskFailureResult> FailAsync(IndexTaskLease lease, ChunkError failure, CancellationToken cancellationToken = default);

    /// <summary>
    /// Heartbeat and fence F2 of the index worker (ADR-010 §3.2, ADR-001 §4 R5): extends the lease by
    /// <paramref name="leaseDuration"/> while the token still matches and the workspace is Active.
    /// </summary>
    Task<IndexTaskRenewal> RenewLeaseAsync(IndexTaskLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Running → Pending without charging the attempt (worker shutdown): the dispatcher publishes the task again. False
    /// when the lease was lost.
    /// </summary>
    Task<bool> ReleaseAsync(IndexTaskLease lease, CancellationToken cancellationToken = default);

    Task<IndexChunkTaskInfo?> GetAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexChunkTaskInfo>> GetByJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the membership reference of an IndexChunkTask to document ids from authoritative state (§21 step 1,
/// ADR-010 §4), one keyset page at a time in ascending DocumentId order. Identifiers only: the projection is read
/// separately, in its own snapshot.
/// </summary>
public interface IIndexTaskMembershipReader
{
    /// <summary>
    /// Up to <paramref name="limit"/> member ids greater than <paramref name="after"/> (null: from the start). An empty
    /// page means the membership is exhausted. Throws <see cref="NotSupportedException"/> for a membership kind that
    /// cannot be resolved by this build (a permanent task failure, replayable once it can).
    /// </summary>
    Task<IReadOnlyList<Guid>> ReadPageAsync(
        Guid workspaceId, ChunkMembership membership, Guid? after, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Housekeeping of both search work tables: redispatch, backlog, day partitions and retention.</summary>
public interface ISearchWorkMaintenance
{
    /// <summary>
    /// Returns lost work to Pending: outbox rows Dispatched longer than <paramref name="dispatchedTimeout"/> without being
    /// applied (ADR-001 §6.3), tasks whose lease expired more than <paramref name="leaseGrace"/> ago, and tasks
    /// Dispatched longer than <paramref name="dispatchedTimeout"/> that no worker leased. Duplicates are harmless.
    /// </summary>
    Task<SearchWorkRecovery> RecoverAsync(
        Guid workspaceId, TimeSpan dispatchedTimeout, TimeSpan leaseGrace, CancellationToken cancellationToken = default);

    Task<SearchWorkBacklog> GetBacklogAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>SearchOutbox backlog of one workspace per lane (lanes without rows are left out).</summary>
    Task<IReadOnlyList<OutboxLaneBacklog>> GetOutboxLaneBacklogAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Workspaces whose work the dispatcher and the recovery visit (every workspace not yet purged).</summary>
    Task<IReadOnlyList<Guid>> GetWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the day partitions of both tables through <paramref name="through"/>; returns how many were new.</summary>
    Task<int> EnsurePartitionsAsync(DateTimeOffset through, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every day partition that ended at or before <paramref name="cutoff"/> and holds only Applied rows; never
    /// deletes rows (ADR-001 R4). Partitions still holding other rows are returned with <c>Dropped = false</c>.
    /// </summary>
    Task<IReadOnlyList<SearchWorkPartition>> DropExpiredPartitionsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
