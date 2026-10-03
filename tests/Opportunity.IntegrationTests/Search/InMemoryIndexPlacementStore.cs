using Opportunity.Application.Search.Indexing;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// <see cref="IIndexPlacementStore"/> held in memory, so the OpenSearch suites run in the OpenSearch collection without a
/// PostgreSQL container. The PostgreSQL store is covered by <see cref="IndexPlacementStoreTests"/> with the same contract.
/// </summary>
internal sealed class InMemoryIndexPlacementStore : IIndexPlacementStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, WorkspaceIndexPlacement> _placements = [];
    private readonly SortedDictionary<int, SharedIndexPool> _pools = [];

    public Task<WorkspaceIndexPlacement?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_placements.GetValueOrDefault(workspaceId));
        }
    }

    public Task<(WorkspaceIndexPlacement Placement, bool Created)> InsertAsync(
        WorkspaceIndexPlacement placement, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_placements.TryGetValue(placement.WorkspaceId, out var existing))
            {
                return Task.FromResult((existing, false));
            }

            var stored = placement with { RowVersion = 1, UpdatedAt = DateTimeOffset.UtcNow };
            _placements[placement.WorkspaceId] = stored;
            return Task.FromResult((stored, true));
        }
    }

    public Task<WorkspaceIndexPlacement?> UpdateAsync(WorkspaceIndexPlacement placement, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_placements.TryGetValue(placement.WorkspaceId, out var existing) || existing.RowVersion != placement.RowVersion)
            {
                return Task.FromResult<WorkspaceIndexPlacement?>(null);
            }

            var stored = placement with { RowVersion = existing.RowVersion + 1, UpdatedAt = DateTimeOffset.UtcNow };
            _placements[placement.WorkspaceId] = stored;
            return Task.FromResult<WorkspaceIndexPlacement?>(stored);
        }
    }

    public Task<IReadOnlyList<SharedIndexPool>> ListSharedPoolsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SharedIndexPool>>([.. _pools.Values]);
        }
    }

    public Task<SharedIndexPool> CreateSharedPoolAsync(int generation, int primaryShards, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var pool = new SharedIndexPool { PoolNumber = _pools.Count + 1, Generation = generation, PrimaryShards = primaryShards };
            _pools[pool.PoolNumber] = pool;
            return Task.FromResult(pool);
        }
    }

    public Task AdjustSharedPoolAsync(
        int poolNumber, int workspaceDelta, long bytesDelta, long closeAtBytes, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var pool = _pools[poolNumber];
            var bytes = Math.Max(0, pool.AssignedBytes + bytesDelta);
            _pools[poolNumber] = pool with
            {
                WorkspaceCount = Math.Max(0, pool.WorkspaceCount + workspaceDelta),
                AssignedBytes = bytes,
                Closed = pool.Closed || bytes >= closeAtBytes,
            };
            return Task.CompletedTask;
        }
    }
}
