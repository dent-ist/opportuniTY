using Opportunity.Core.Jobs;

namespace Opportunity.Application.Jobs;

/// <summary>
/// What the dispatcher may publish for one workspace in one pass (ADR-010 §6 concurrency defaults and index
/// backpressure). Chunks already Dispatched or Running, or held by a publish claim, count as in flight.
/// </summary>
public sealed record JobChunkDispatchLimits
{
    /// <summary>Chunks claimed per pass (the workspace budget can make it fewer).</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>ADR-010 §6: at most this many chunks of one job are in flight.</summary>
    public int MaxInFlightPerJob { get; init; } = 4;

    /// <summary>ADR-010 §6: at most this many chunks of one workspace are in flight, over all its jobs.</summary>
    public int MaxInFlightPerWorkspace { get; init; } = 8;

    /// <summary>Index backpressure: no new chunk of a job while more of its IndexChunkTasks are un-applied.</summary>
    public int MaxUnappliedIndexTasksPerJob { get; init; } = 50;

    /// <summary>
    /// Security throttle (Q-10): a job with security-affecting index tasks (lane L2) keeps its un-applied tasks plus
    /// its in-flight chunks (each commits one more task) at or below this.
    /// </summary>
    public int MaxUnappliedSecurityIndexTasksPerJob { get; init; } = 4;

    /// <summary>A chunk Dispatched this long ago that no worker claimed is published again (a lost message).</summary>
    public TimeSpan RedispatchAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Only jobs of these operation kinds are dispatched (the kinds that have a work queue).</summary>
    public required IReadOnlyCollection<ChunkOperationKind> Operations { get; init; }
}

/// <summary>
/// A job chunk held by one dispatcher's publish claim (ADR-001 §6.1, ADR-010 §2: the claim is not a chunk status).
/// <see cref="Status"/> is Pending or RetryWait for a first dispatch, Dispatched for a re-dispatch.
/// </summary>
public sealed record ClaimedJobChunk(
    Guid WorkspaceId,
    Guid JobId,
    Guid ChunkId,
    int Sequence,
    JobChunkStatus Status,
    ChunkOperationKind Operation,
    string IdempotencyKey,
    int AttemptCount,
    string? CorrelationId);

/// <summary>
/// The dispatcher's side of <c>job_chunk</c>: publish claims with <c>FOR UPDATE SKIP LOCKED</c>, so N dispatchers
/// never publish the same chunk in the same pass, and the Dispatch transition after a broker confirm.
/// </summary>
public interface IJobChunkDispatchRepository
{
    /// <summary>
    /// Claims the next chunks to publish of the workspace's Running jobs (workspace Active), lowest sequence first and
    /// within <paramref name="limits"/>: Pending chunks, RetryWait chunks whose backoff elapsed, and Dispatched chunks
    /// no worker claimed within <see cref="JobChunkDispatchLimits.RedispatchAfter"/>.
    /// </summary>
    Task<IReadOnlyList<ClaimedJobChunk>> ClaimForDispatchAsync(
        Guid workspaceId, string owner, JobChunkDispatchLimits limits, TimeSpan claimDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// After a broker confirm: Pending/RetryWait → Dispatched (Dispatched stays, with a new dispatch time) and the claim
    /// is dropped, for chunks this owner still holds. A chunk a worker claimed meanwhile keeps its status.
    /// </summary>
    Task<int> MarkDispatchedAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<Guid> chunkIds, CancellationToken cancellationToken = default);

    /// <summary>The publish was not confirmed: the claim is kept until <paramref name="retryAfter"/> from now, then the chunk is due again.</summary>
    Task<int> ReleaseDispatchClaimAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<Guid> chunkIds, TimeSpan retryAfter, CancellationToken cancellationToken = default);
}
