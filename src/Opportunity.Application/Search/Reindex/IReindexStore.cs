using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;

namespace Opportunity.Application.Search.Reindex;

/// <summary>
/// PostgreSQL state of reindex jobs (E07-T11): the run rows the coordinator drives, the key-range plan and chunk commits
/// of the backfill, and the projection states validation compares with the index. Implemented by <c>Opportunity.Data</c>.
/// </summary>
public interface IReindexStore
{
    /// <summary>
    /// Records the run of <paramref name="jobId"/> in <see cref="ReindexPhase.Pending"/> and registers the workspace with
    /// the coordinator. Returns the stored run: the existing one for a retried submission, or the workspace's in-flight
    /// run with <see cref="ReindexCreateOutcome.Conflict"/>.
    /// </summary>
    Task<(ReindexCreateOutcome Outcome, ReindexRun Run)> CreateAsync(
        Guid workspaceId, Guid jobId, ReindexRequest request, CancellationToken cancellationToken = default);

    Task<ReindexRun?> GetAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>The workspace's runs, newest first.</summary>
    Task<IReadOnlyList<ReindexRun>> ListAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>(workspace, job) of every run the coordinator still drives (installation-level registry).</summary>
    Task<IReadOnlyList<(Guid WorkspaceId, Guid JobId)>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes or renews the coordinator lease of a run until now + <paramref name="duration"/>; null when another owner
    /// holds a live lease (or the run is gone or finished).
    /// </summary>
    Task<ReindexRun?> TryLeaseAsync(
        Guid workspaceId, Guid jobId, string owner, TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the run while <paramref name="owner"/> holds its lease, sets the job's status reason to
    /// <see cref="ReindexRun.StatusReason"/> and, for a finished phase, unregisters the workspace. Null when the lease was
    /// lost (nothing changed).
    /// </summary>
    Task<ReindexRun?> SaveAsync(ReindexRun run, string owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans the backfill: contiguous, inclusive DocumentId key ranges of at most <paramref name="maxDocuments"/> current
    /// documents (tombstones included) that together cover every possible id, so a document created later still falls in
    /// one; at least one range. Streams the keys, holding only the boundaries.
    /// </summary>
    Task<(IReadOnlyList<ChunkPlan> Chunks, long Documents)> PlanKeyRangesAsync(
        Guid workspaceId, long projectionGeneration, int maxDocuments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a claimed reindex chunk (fence F3) with its one <c>Reindex</c> IndexChunkTask over the chunk's key range,
    /// in one transaction. The task carries no SearchGeneration (ADR-001 §7.5).
    /// </summary>
    Task<ChunkCommitResult> CommitChunkAsync(ClaimedChunk chunk, CancellationToken cancellationToken = default);

    Task<ReindexTaskProgress> GetTaskProgressAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Projection states after <paramref name="after"/> in DocumentId order, tombstones included.</summary>
    Task<DocumentVersionPage> ReadVersionsAsync(Guid workspaceId, Guid? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Projection states of the given documents (absent ones are left out).</summary>
    Task<DocumentVersionPage> ReadVersionsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>Live (not deleted) documents, with the SearchGeneration counter of the same snapshot.</summary>
    Task<(long Documents, long Generation)> CountDocumentsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
