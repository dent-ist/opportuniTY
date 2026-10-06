using Opportunity.Application.Search.Indexing;

namespace Opportunity.Search.Indexing;

public enum IndexPurpose
{
    Read,
    Write,
}

/// <summary>
/// Index Management (ADR-006 R1): resolves a workspace to its physical placement and drives create, rebuild and move.
/// Only search-module code (search service, projection writers, reindex) uses this; everything else addresses a
/// workspace through <see cref="IWorkspaceSearchPlacement"/> and never sees a name.
/// </summary>
public interface IIndexManager
{
    /// <summary>
    /// The placement for reads or writes. Writes place an unplaced workspace with default sizing; reads of an unplaced
    /// workspace throw <see cref="WorkspaceNotPlacedException"/>.
    /// </summary>
    Task<Placement> ResolveAsync(Guid workspaceId, IndexPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>Places the workspace (idempotent) and makes sure its index and alias exist.</summary>
    Task<Placement> PlaceAsync(Guid workspaceId, WorkspacePlacementRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// R13 steps 1-2: creates the target (new generation and/or dedicated placement), records it as pending and starts
    /// dual-target writes. Backfill and validation (steps 3-5) belong to the reindex job.
    /// </summary>
    Task<Placement> BeginRebuildAsync(Guid workspaceId, IndexRebuildRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// R13 steps 4 and 6: restores the target's refresh and replicas, refreshes it and switches reads in one
    /// <c>_aliases</c> request (or the placement row for a move), and releases a shared pool the workspace left.
    /// Retry-safe. The previous location keeps its documents: the reindex job write-blocks it once every process saw
    /// the switch (<see cref="BlockWritesAsync"/>) and drops it after the retention period (<see cref="DropAsync"/>,
    /// R13 step 7), so a writer or reader still holding the cached placement for up to the cache TTL never fails.
    /// </summary>
    Task<Placement> CompleteRebuildAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the pending target and stops dual-target writes; the current placement keeps serving. A writer that still
    /// held the cached placement may recreate a dropped dedicated target within the cache TTL, so the reindex job drops
    /// it once more after that (<see cref="DropAsync"/>).
    /// </summary>
    Task<Placement> AbortRebuildAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a retired dedicated index read-only (R13 step 7: kept for rollback and open readers). A shared index serves
    /// other workspaces and is left writable. Refuses the workspace's current or pending location; a missing index is
    /// fine.
    /// </summary>
    Task BlockWritesAsync(Guid workspaceId, IndexLocation location, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a retired location of the workspace: a dedicated index is deleted, a shared index loses the workspace's
    /// documents (term + routing, R13 step 7). Refuses the workspace's current or pending location; idempotent.
    /// </summary>
    Task DropAsync(Guid workspaceId, IndexLocation location, CancellationToken cancellationToken = default);
}

/// <param name="Kind">Target tier; null keeps the current one. Dedicated to shared is refused (promotion is one-way, R4).</param>
/// <param name="Generation">Target ProjectionGeneration; null keeps the current one.</param>
/// <param name="PrimaryShards">Dedicated target shard count; null derives it from the workspace's size estimate.</param>
public sealed record IndexRebuildRequest(IndexPlacementKind? Kind = null, int? Generation = null, int? PrimaryShards = null);

/// <summary>A physical index plus the routing every request against it must carry (null: none).</summary>
public sealed record IndexTarget(string Index, string? Routing)
{
    /// <summary>The ProjectionGeneration (mapping) of the index; 0 when unknown.</summary>
    public int Generation { get; init; }
}

/// <summary>
/// Resolved physical placement. Reads go to the stable alias; writes go to every target (two while rebuilding or
/// moving). <see cref="WorkspaceFilterValue"/> is the <c>workspaceId</c> term the search service always injects (R7/R8).
/// </summary>
public sealed record Placement(
    Guid WorkspaceId,
    IndexPlacementKind Kind,
    int Generation,
    IndexPlacementState State,
    IndexTarget Read,
    IReadOnlyList<IndexTarget> WriteTargets)
{
    public string WorkspaceFilterValue => WorkspaceId.ToString("D");

    /// <summary>The location reads are served from (facts only).</summary>
    public IndexLocation? Location { get; init; }

    /// <summary>The target of a running rebuild or move; null while Active.</summary>
    public IndexLocation? PendingLocation { get; init; }

    /// <summary>The rebuild target's write target (the second write target while Building or Moving), else null.</summary>
    public IndexTarget? RebuildTarget =>
        State is IndexPlacementState.Building or IndexPlacementState.Moving && WriteTargets.Count > 1 ? WriteTargets[^1] : null;
}

public sealed class WorkspaceNotPlacedException : Exception
{
    public WorkspaceNotPlacedException()
    {
    }

    public WorkspaceNotPlacedException(string message)
        : base(message)
    {
    }

    public WorkspaceNotPlacedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WorkspaceNotPlacedException(Guid workspaceId)
        : base($"Workspace {workspaceId} has no search placement yet.")
    {
    }
}

/// <summary>The placement changed underneath (concurrent rebuild, move or state change); re-read and retry.</summary>
public sealed class IndexPlacementConflictException : Exception
{
    public IndexPlacementConflictException()
    {
    }

    public IndexPlacementConflictException(string message)
        : base(message)
    {
    }

    public IndexPlacementConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
