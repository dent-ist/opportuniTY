using Opportunity.Application.Audit;

namespace Opportunity.Application.Workspaces;

/// <summary>
/// A workspace preservation lock (legal hold; E20-T01, ADR-014 §2). Several may be active at once; while any is, every
/// delete or purge of the workspace's preserved records is refused by the database. Released locks stay as history.
/// </summary>
public sealed record PreservationLock
{
    public required Guid WorkspaceId { get; init; }

    public required Guid LockId { get; init; }

    /// <summary>Why the data must be preserved (litigation, investigation, preservation letter). Stored, never audited.</summary>
    public required string Reason { get; init; }

    /// <summary>Optional reference to the matter, order or preservation notice.</summary>
    public string? MatterReference { get; init; }

    /// <summary>Release needs a request by one person and the approval of a different one (Q-23 pattern).</summary>
    public required bool ReleaseRequiresApproval { get; init; }

    public required Guid PlacedBy { get; init; }

    public string? PlacedByName { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }

    public Guid? ReleaseRequestedBy { get; init; }

    public string? ReleaseRequestedByName { get; init; }

    public DateTimeOffset? ReleaseRequestedAt { get; init; }

    public string? ReleaseReason { get; init; }

    public Guid? ReleaseApprovedBy { get; init; }

    public string? ReleaseApprovedByName { get; init; }

    public DateTimeOffset? ReleasedAt { get; init; }

    public required long Version { get; init; }

    public bool IsActive => ReleasedAt is null;

    public bool ReleasePending => ReleasedAt is null && ReleaseRequestedBy is not null;
}

/// <summary>The release columns a release step writes (all of them, so the store can apply any step the same way).</summary>
public sealed record PreservationLockRelease(
    Guid? RequestedBy, DateTimeOffset? RequestedAt, string? Reason, Guid? ApprovedBy, DateTimeOffset? ReleasedAt);

public enum PreservationLockWriteOutcome
{
    Ok,
    NotFound,
    VersionConflict,
}

public sealed record PreservationLockWriteResult(PreservationLockWriteOutcome Outcome, PreservationLock? Lock = null);

/// <summary>
/// PostgreSQL store of preservation locks. Every write locks the workspace row (serializing with the delete guards of
/// V0048), checks the expected version and inserts its audit event in the same transaction (ADR-013 §2.1).
/// </summary>
public interface IPreservationLockStore
{
    /// <summary>Every lock of the workspace: active ones first, newest first.</summary>
    Task<IReadOnlyList<PreservationLock>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<PreservationLock?> GetAsync(Guid workspaceId, Guid lockId, CancellationToken cancellationToken = default);

    Task<PreservationLock> PlaceAsync(PreservationLock newLock, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<PreservationLockWriteResult> UpdateReleaseAsync(
        Guid workspaceId, Guid lockId, long expectedVersion, PreservationLockRelease release, AuditEvent audit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The preservation-lock check for code paths that remove data (E20-T01) and the hook E20-T02's deletion run calls before
/// each destructive step. The database enforces the same rule on every delete of preserved records (SQLSTATE O0423);
/// this port lets background work skip a held workspace instead of failing.
/// </summary>
public interface IPreservationLockGuard
{
    /// <summary>True while the workspace has at least one active preservation lock.</summary>
    Task<bool> IsLockedAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>A delete or purge was refused because the workspace is under a preservation lock (API: 423 Locked).</summary>
public sealed class PreservationLockedException : Exception
{
    public PreservationLockedException()
        : this(Guid.Empty, "data")
    {
    }

    public PreservationLockedException(string message)
        : base(message)
    {
        Target = "data";
    }

    public PreservationLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Target = "data";
    }

    public PreservationLockedException(Guid workspaceId, string target, Exception? innerException = null)
        : base($"Workspace {workspaceId} is under a preservation lock (legal hold); {target} cannot be deleted.", innerException)
    {
        WorkspaceId = workspaceId;
        Target = target;
    }

    public Guid WorkspaceId { get; }

    /// <summary>What was refused: the table or object kind, never a value.</summary>
    public string Target { get; }
}
