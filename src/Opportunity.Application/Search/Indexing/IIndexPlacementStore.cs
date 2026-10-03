namespace Opportunity.Application.Search.Indexing;

/// <summary>
/// Persistence of workspace index placements and the shared index pool (ADR-006 R2/R6). Only the index manager in
/// <c>Opportunity.Search</c> uses this port; the decisions (thresholds, pool choice) are made there.
/// </summary>
public interface IIndexPlacementStore
{
    Task<WorkspaceIndexPlacement?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Inserts the placement unless one exists; returns the stored row (the existing one on conflict).</summary>
    Task<(WorkspaceIndexPlacement Placement, bool Created)> InsertAsync(
        WorkspaceIndexPlacement placement, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the placement if its <see cref="WorkspaceIndexPlacement.RowVersion"/> still matches; returns the stored
    /// row with the new version, or null when another writer got there first.
    /// </summary>
    Task<WorkspaceIndexPlacement?> UpdateAsync(WorkspaceIndexPlacement placement, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharedIndexPool>> ListSharedPoolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers a new shared pool with the next free number and returns it.</summary>
    Task<SharedIndexPool> CreateSharedPoolAsync(int generation, int primaryShards, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adjusts a pool's counters and closes it once <see cref="SharedIndexPool.AssignedBytes"/> reaches
    /// <paramref name="closeAtBytes"/> (a closed pool never reopens automatically).
    /// </summary>
    Task AdjustSharedPoolAsync(
        int poolNumber, int workspaceDelta, long bytesDelta, long closeAtBytes, CancellationToken cancellationToken = default);
}
