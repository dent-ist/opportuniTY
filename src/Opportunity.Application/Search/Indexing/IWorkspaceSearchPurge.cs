namespace Opportunity.Application.Search.Indexing;

/// <summary>
/// Removes a workspace from OpenSearch for its deletion (E20-T02, ADR-014 §4 step 4) and counts what is left (§7). The
/// caller never sees an index name: a dedicated index (every revision and generation) is deleted with its alias, and
/// every shared index loses the workspace's documents (term and routing), whatever its placement record says now.
/// </summary>
public interface IWorkspaceSearchPurge
{
    /// <summary>One purge pass; idempotent. Waits until the cluster has removed the documents and refreshed.</summary>
    Task<WorkspaceSearchPurgeResult> PurgeAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Documents of the workspace in any index, and dedicated indexes that still exist.</summary>
    Task<WorkspaceSearchInventory> CountAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

public sealed record WorkspaceSearchPurgeResult(int IndexesDeleted, long DocumentsDeleted);

public sealed record WorkspaceSearchInventory(long Documents, int DedicatedIndexes)
{
    public bool IsEmpty => Documents == 0 && DedicatedIndexes == 0;
}
