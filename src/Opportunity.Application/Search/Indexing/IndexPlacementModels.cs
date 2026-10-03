namespace Opportunity.Application.Search.Indexing;

/// <summary>Physical placement tier of a workspace's search projection (ADR-006 §2).</summary>
public enum IndexPlacementKind
{
    /// <summary>One index of the shared pool, <c>routing = WorkspaceId</c>.</summary>
    Shared = 1,

    /// <summary>One index per workspace (one or more primaries).</summary>
    Dedicated = 2,
}

/// <summary>Lifecycle state of a placement (ADR-006 R2).</summary>
public enum IndexPlacementState
{
    Active = 1,

    /// <summary>Rebuilding into a new projection generation; writers dual-target.</summary>
    Building = 2,

    /// <summary>Moving to another placement (e.g. shared to dedicated); writers dual-target.</summary>
    Moving = 3,

    Deleting = 4,
}

/// <summary>
/// Where a workspace's projection lives, as PostgreSQL records it (ADR-006 R2). It holds placement facts only, never a
/// physical index or alias name: <c>Opportunity.Search</c> derives names from these values (ADR-006 R1).
/// </summary>
public sealed record WorkspaceIndexPlacement
{
    public required Guid WorkspaceId { get; init; }

    public required IndexPlacementKind Kind { get; init; }

    /// <summary>Shared pool number; null for dedicated placements.</summary>
    public int? SharedPool { get; init; }

    /// <summary>ProjectionGeneration = mapping template version (ADR-007 R2).</summary>
    public required int Generation { get; init; }

    public int PrimaryShards { get; init; } = 1;

    public IndexPlacementState State { get; init; } = IndexPlacementState.Active;

    /// <summary>Target of a running rebuild or move; all null while <see cref="IndexPlacementState.Active"/>.</summary>
    public IndexPlacementKind? PendingKind { get; init; }

    public int? PendingSharedPool { get; init; }

    public int? PendingGeneration { get; init; }

    public int? PendingPrimaryShards { get; init; }

    /// <summary>Admin flag <c>DedicatedIndex</c> (large or sensitive matter, ADR-006 §2).</summary>
    public bool DedicatedRequested { get; init; }

    public long EstimatedDocuments { get; init; }

    /// <summary>Calibrated primary-store estimate in bytes (ADR-006 R5).</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>Optimistic concurrency token; the store bumps it on every update.</summary>
    public long RowVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One shared index of the pool (installation-level, ADR-006 R6).</summary>
public sealed record SharedIndexPool
{
    public required int PoolNumber { get; init; }

    public required int Generation { get; init; }

    public int PrimaryShards { get; init; } = 1;

    /// <summary>Closed pools take no new workspaces.</summary>
    public bool Closed { get; init; }

    public int WorkspaceCount { get; init; }

    /// <summary>Sum of the estimated bytes of the workspaces assigned to it.</summary>
    public long AssignedBytes { get; init; }
}
