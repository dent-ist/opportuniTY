using System.Text.Json.Nodes;

using Opportunity.Core.Workspaces;

namespace Opportunity.Application.Backup;

/// <summary>Phases of a restore run (<c>opportunity.restore_run.phase</c>).</summary>
public enum RestorePhase
{
    /// <summary>R6 started; reconcile may be re-run while every writer is still stopped.</summary>
    Reconciling,

    /// <summary>Catch-up, redrive and security reset are done for every workspace.</summary>
    Reconciled,

    /// <summary>The restore record is in every audit chain, checkpointed and stored under <c>sys/restores/</c>.</summary>
    Recorded,

    /// <summary>The pre-open gates passed.</summary>
    Verified,
}

/// <summary>One restore of the installation (<c>opportunity.restore_run</c>).</summary>
public sealed record RestoreRun(
    Guid RestoreId,
    RestorePath Path,
    DateTimeOffset? TargetTime,
    string? BackupLabel,
    string? SnapshotName,
    string OperatorName,
    RestorePhase Phase,
    DateTimeOffset StartedAt,
    string? RecordKey = null,
    string? RecordSha256 = null,
    JsonObject? Report = null);

public sealed record RestoreWorkspace(Guid WorkspaceId, WorkspaceStatus Status);

/// <summary>What reconcile changed in one workspace (design §4.3–§4.5). Counts only.</summary>
/// <param name="AnchorGeneration">The anchor used for catch-up; null when the workspace was not caught up (path 0 or C).</param>
/// <param name="OutboxRepended">SearchOutbox rows above the anchor returned to Pending.</param>
/// <param name="TasksRepended">IndexChunkTasks above the anchor returned to Pending.</param>
/// <param name="WatermarkBefore">The visible watermark before the reset.</param>
/// <param name="WatermarkAfter">The visible watermark after it (≤ anchor).</param>
/// <param name="Redriven">In-flight outbox rows, index tasks and job chunks returned to Pending (claims and leases cleared).</param>
/// <param name="BreakGlassEnded">Open break-glass activations ended as Revoked.</param>
/// <param name="SearchSessionsDropped">Search sessions (and their cursors) deleted: their point-in-time readers are gone.</param>
/// <param name="ReindexesAborted">Reindex jobs that were not terminal, set to Cancelling so the coordinator aborts them.</param>
public sealed record WorkspaceReconcileResult(
    Guid WorkspaceId,
    long? AnchorGeneration,
    long OutboxRepended,
    long TasksRepended,
    long? WatermarkBefore,
    long? WatermarkAfter,
    long Redriven,
    long BreakGlassEnded,
    long SearchSessionsDropped,
    long ReindexesAborted);

/// <summary>The state the pre-open gates check in one workspace (design §7.1).</summary>
/// <param name="InFlight">Claimed, Dispatched or Running outbox rows, index tasks and job chunks (must be 0).</param>
/// <param name="Watermark">The visible watermark.</param>
/// <param name="SearchGeneration">The workspace's SearchGeneration counter.</param>
/// <param name="NotPendingAboveAnchor">Work records above the anchor that are not Pending (must be 0 before reopening on path A).</param>
/// <param name="OpenBreakGlass">Break-glass activations not ended (must be 0).</param>
/// <param name="SearchSessions">Search sessions (must be 0).</param>
/// <param name="FailedWork">Failed outbox rows and index tasks (convergence needs 0).</param>
public sealed record WorkspaceRestoreState(
    Guid WorkspaceId,
    long InFlight,
    long Watermark,
    long SearchGeneration,
    long NotPendingAboveAnchor,
    long OpenBreakGlass,
    long SearchSessions,
    long FailedWork);

/// <summary>A registered object (<c>stored_object</c>) for the inventory check.</summary>
public sealed record StoredObjectEntry(string ObjectKey, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>PostgreSQL side of the restore tool (V0070 and the tables the reconcile touches). Implemented by <c>Opportunity.Data</c>.</summary>
public interface IRestoreStore
{
    Task<RestoreRun?> GetRunAsync(Guid restoreId, CancellationToken cancellationToken = default);

    /// <summary>Inserts the run, or returns the existing one with the same id (a re-run).</summary>
    Task<RestoreRun> BeginRunAsync(RestoreRun run, CancellationToken cancellationToken = default);

    Task UpdateRunAsync(Guid restoreId, RestorePhase phase, JsonObject? report, string? recordKey, string? recordSha256,
        CancellationToken cancellationToken = default);

    /// <summary>Every workspace in the registry with its status.</summary>
    Task<IReadOnlyList<RestoreWorkspace>> ListWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// R6 for one workspace in one transaction: with an anchor, re-pends every work record above it and lowers the
    /// visible watermark to it (ADR-016 B-4); then redrives in-flight work, ends break-glass, drops search sessions and
    /// aborts reindex runs that were not terminal.
    /// </summary>
    /// <param name="anchor">The snapshot anchor <c>G_s</c> (0 for a workspace without one); null skips catch-up.</param>
    Task<WorkspaceReconcileResult> ReconcileWorkspaceAsync(Guid workspaceId, long? anchor, CancellationToken cancellationToken = default);

    /// <summary>ADR-016 §7.1: revokes every live browser session with reason <c>Restore</c>.</summary>
    Task<long> RevokeSessionsAsync(CancellationToken cancellationToken = default);

    Task<long> CountLiveSessionsAsync(CancellationToken cancellationToken = default);

    /// <param name="anchor">With an anchor, counts work records above it that are not Pending.</param>
    Task<WorkspaceRestoreState> InspectWorkspaceAsync(Guid workspaceId, long? anchor, CancellationToken cancellationToken = default);

    /// <summary>The statuses of the given deletions in the restored database (missing ones are left out).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDeletionStatusesAsync(IReadOnlyCollection<Guid> deletionIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes an Active workspace whose destruction was certified after the recovery target (design §4.4.4), so it does
    /// not reopen for review until the deletion is re-run. Returns whether its status changed.
    /// </summary>
    Task<bool> CloseWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Registered objects of a workspace created at or after <paramref name="createdFrom"/>, plus a random sample of the older ones.</summary>
    Task<IReadOnlyList<StoredObjectEntry>> SampleStoredObjectsAsync(
        Guid workspaceId, DateTimeOffset createdFrom, int sampleOfOlder, CancellationToken cancellationToken = default);

    /// <summary>Whether the key of every listed object is registered (orphans are the rest).</summary>
    Task<IReadOnlySet<string>> RegisteredKeysAsync(Guid workspaceId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    /// <summary>Production and export ids the restored database knows in the workspace.</summary>
    Task<(IReadOnlySet<Guid> Productions, IReadOnlySet<Guid> Exports)> KnownOutputsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>Placement of a workspace's search index after a restore (design §4.5, §7.1.2).</summary>
/// <param name="Placed">The workspace has a placement row.</param>
/// <param name="IndexExists">The physical index of its read location exists.</param>
/// <param name="AliasExists">The read alias exists (dedicated) or the shared index is reachable.</param>
/// <param name="InTransition">The placement is Building or Moving.</param>
public sealed record SearchPlacementCheck(Guid WorkspaceId, bool Placed, bool IndexExists, bool AliasExists, bool InTransition)
{
    public bool Resolves => !Placed || (IndexExists && AliasExists);
}

/// <summary>PostgreSQL versus OpenSearch for one workspace (design §2, §7.1.3, §7.3).</summary>
/// <param name="Ahead">Indexed at a version above PostgreSQL's, or indexed while PostgreSQL has it deleted or not at all (corruption).</param>
/// <param name="Stale">Indexed below PostgreSQL's version (lag).</param>
/// <param name="Missing">Live in PostgreSQL, not indexed (lag).</param>
public sealed record SearchVersionComparison(
    Guid WorkspaceId,
    bool Full,
    long Compared,
    long Ahead,
    long Stale,
    long Missing,
    IReadOnlyList<Guid> Examples)
{
    public bool Converged => Ahead == 0 && Stale == 0 && Missing == 0;
}

/// <summary>Search checks of the restore tool. Implemented by <c>Opportunity.Search</c> (index names never leave it).</summary>
public interface ISearchRestoreChecks
{
    Task<SearchPlacementCheck> CheckPlacementAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares OpenSearch with PostgreSQL. <paramref name="sample"/> null compares every document (range scans in
    /// DocumentId order); otherwise that many randomly chosen documents plus <paramref name="alsoCheck"/>. Refreshes the
    /// read index first. Nothing is written.
    /// </summary>
    Task<SearchVersionComparison> CompareAsync(
        Guid workspaceId, int? sample, IReadOnlyCollection<Guid> alsoCheck, CancellationToken cancellationToken = default);
}
