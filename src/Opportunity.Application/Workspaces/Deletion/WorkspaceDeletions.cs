using System.Text.Json.Nodes;

using Opportunity.Application.Audit;

namespace Opportunity.Application.Workspaces.Deletion;

/// <summary>What a deletion keeps (ADR-014 §5, Q-23). Stored as smallint; values are fixed.</summary>
public enum DeletionRetentionProfile : short
{
    /// <summary>The default: productions with their members, Bates ledger, volume outputs and source snapshots are kept; audit always is.</summary>
    RetainRecords = 1,

    /// <summary>Everything of the workspace except the audit trail, the certificate and the tombstone.</summary>
    PurgeAll = 2,
}

/// <summary>Lifecycle of a deletion request and its run (ADR-014 §3-§4). Stored by name.</summary>
public enum WorkspaceDeletionStatus
{
    Requested,
    Approved,
    Running,

    /// <summary>A legal hold placed during the run stopped it; it resumes once every hold is released.</summary>
    Halted,
    Completed,

    /// <summary>Finished and certified, but verification still found data it lists as residual.</summary>
    CompletedWithResiduals,
    Cancelled,
    Expired,
}

/// <summary>The steps of a run in order (ADR-014 §4). Stored by name.</summary>
public enum DeletionStep
{
    /// <summary>Workspace to Deleting, epoch incremented, in the transaction that checks approval, wait and holds.</summary>
    Fence,

    /// <summary>Every unfinished job cancelled; waits until no lease or claim of the workspace is live.</summary>
    Drain,

    /// <summary>Counts per store before anything is removed (the certificate's "before").</summary>
    Inventory,

    /// <summary>First search pass: dedicated indexes deleted, shared indexes lose the workspace's documents.</summary>
    SearchPurge,

    DatabasePurge,
    StoragePurge,

    /// <summary>Crypto-shredding of the workspace's data keys (PurgeAll only; retained records still need them).</summary>
    KeyDestruction,

    /// <summary>After the resurrection guard delay: the second search pass, then every store counted again.</summary>
    Verification,

    Certification,
}

public static class WorkspaceDeletionRules
{
    public const int MaxReasonLength = 2000;
    public const int MaxExternalReferenceLength = 200;
    public const int MaxNoteLength = 2000;

    public static bool IsOpen(this WorkspaceDeletionStatus status) =>
        status is WorkspaceDeletionStatus.Requested or WorkspaceDeletionStatus.Approved or WorkspaceDeletionStatus.Running
            or WorkspaceDeletionStatus.Halted;

    public static bool IsFinished(this WorkspaceDeletionStatus status) =>
        status is WorkspaceDeletionStatus.Completed or WorkspaceDeletionStatus.CompletedWithResiduals;

    /// <summary>Requested and Approved deletions can still be cancelled; a started run cannot be undone.</summary>
    public static bool IsCancellable(this WorkspaceDeletionStatus status) =>
        status is WorkspaceDeletionStatus.Requested or WorkspaceDeletionStatus.Approved;
}

/// <summary>A deletion request and its run, as PostgreSQL records it (installation-level, survives the deletion).</summary>
public sealed record WorkspaceDeletion
{
    public required Guid DeletionId { get; init; }

    public required Guid WorkspaceId { get; init; }

    public required string WorkspaceName { get; init; }

    public string? MatterNumber { get; init; }

    public required DeletionRetentionProfile RetentionProfile { get; init; }

    /// <summary>Why the workspace is deleted (protective order, end of matter). Stored on the record, never audited.</summary>
    public required string Reason { get; init; }

    /// <summary>Protective-order paragraph, court order or other reference.</summary>
    public string? ExternalReference { get; init; }

    public required WorkspaceDeletionStatus Status { get; init; }

    public DeletionStep? Step { get; init; }

    public required Guid RequestedBy { get; init; }

    public string? RequestedByName { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public Guid? ApprovedBy { get; init; }

    public string? ApprovedByName { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public string? ApprovalNote { get; init; }

    /// <summary>Approval plus the waiting period: the run starts no earlier.</summary>
    public DateTimeOffset? RunNotBefore { get; init; }

    public Guid? CancelledBy { get; init; }

    public string? CancelledByName { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public long? FenceEpoch { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public DateTimeOffset? NextStepAt { get; init; }

    public DateTimeOffset? HaltedAt { get; init; }

    public string? Error { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public bool HasCertificate { get; init; }

    public required long Version { get; init; }
}

/// <summary>One step of a run with its counts (names and numbers only).</summary>
public sealed record WorkspaceDeletionStepRecord(
    DeletionStep Step,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Outcome,
    JsonObject? Counts,
    JsonObject? Detail);

public static class DeletionStepOutcomes
{
    public const string Success = "Success";
    public const string Residuals = "Residuals";
    public const string Halted = "Halted";
    public const string Failed = "Failed";
}

/// <summary>The stored certificate: canonical JSON bytes, their SHA-256 and the optional signature.</summary>
public sealed record StoredDestructionCertificate(
    Guid DeletionId,
    Guid WorkspaceId,
    DateTimeOffset IssuedAt,
    byte[] Sha256,
    string CertificateJson,
    string? ObjectKey,
    string? SignatureKeyId,
    byte[]? Signature);

public enum DeletionWriteOutcome
{
    Ok,
    NotFound,
    VersionConflict,

    /// <summary>The deletion is not in a state the step accepts.</summary>
    Conflict,
}

public sealed record DeletionWriteResult(DeletionWriteOutcome Outcome, WorkspaceDeletion? Deletion = null);

/// <summary>How the run's fence transaction ended.</summary>
public enum DeletionStartOutcome
{
    Started,

    /// <summary>Not approved, cancelled, or the waiting period has not passed.</summary>
    NotReady,

    /// <summary>The workspace is under a legal hold: the run does not start (ADR-014 §2.3).</summary>
    Held,

    LeaseLost,
}

/// <summary>A tenant table and the tables its rows reference (foreign keys), for the purge order.</summary>
public sealed record PurgeTableReference(string Table, string ReferencedTable, bool Deferrable);

/// <summary>Rows of one tenant table: all of them and those the retention profile keeps.</summary>
public sealed record PurgeTableCount(string Table, long Total, long Retained)
{
    public long Purgeable => Total - Retained;
}

/// <summary>Leases and claims of a workspace's work that are still live (the drain waits for zero).</summary>
public sealed record WorkspaceWorkInFlight(long JobChunks, long IndexTasks, long OutboxClaims, long CoordinatorClaims)
{
    public long Total => JobChunks + IndexTasks + OutboxClaims + CoordinatorClaims;
}

/// <summary>Data key versions of a workspace by state.</summary>
public sealed record WorkspaceKeyCounts(int Active, int Retired, int Destroyed)
{
    public int Total => Active + Retired + Destroyed;
}

/// <summary>Filter of the installation-level deletion list.</summary>
/// <param name="RequestedBy">Only this user's requests (callers who cannot approve).</param>
/// <param name="OpenOnly">Only Requested, Approved, Running and Halted deletions.</param>
public sealed record WorkspaceDeletionQuery(Guid? RequestedBy = null, bool OpenOnly = false, int Limit = 200);

/// <summary>
/// PostgreSQL store of deletion requests and runs (E20-T02, V0057). Rows are installation-level and never deleted.
/// Every request-side write takes its audit event in the same transaction; every run-side write requires the caller's
/// lease. Writes that start or continue destruction re-check the legal hold in their transaction and throw
/// <see cref="PreservationLockedException"/> when one is active.
/// </summary>
public interface IWorkspaceDeletionStore
{
    /// <summary>Records a request. Throws <see cref="PreservationLockedException"/> under a legal hold.</summary>
    /// <returns>Conflict (with the open deletion) when the workspace already has one open.</returns>
    Task<DeletionWriteResult> CreateAsync(WorkspaceDeletion request, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<WorkspaceDeletion?> GetAsync(Guid deletionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceDeletion>> ListAsync(WorkspaceDeletionQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceDeletion>> ListForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Requested → Approved. Throws <see cref="PreservationLockedException"/> under a legal hold.</summary>
    Task<DeletionWriteResult> ApproveAsync(
        Guid deletionId, long expectedVersion, Guid approvedBy, string? note, DateTimeOffset approvedAt, DateTimeOffset runNotBefore,
        AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Requested or Approved → Cancelled.</summary>
    Task<DeletionWriteResult> CancelAsync(
        Guid deletionId, long expectedVersion, Guid cancelledBy, DateTimeOffset cancelledAt, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceDeletionStepRecord>> GetStepsAsync(Guid deletionId, CancellationToken cancellationToken = default);

    Task<StoredDestructionCertificate?> GetCertificateAsync(Guid deletionId, CancellationToken cancellationToken = default);

    // ---- The run (coordinator) ----------------------------------------------------------------------------------------

    /// <summary>Deletions the coordinator drives now: Approved and due, Running, Halted, and Requested past expiry.</summary>
    Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Takes or renews the run lease; null when another coordinator holds it.</summary>
    Task<WorkspaceDeletion?> TryLeaseAsync(Guid deletionId, string owner, TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Gives the lease back at the end of a pass, so any coordinator may drive the run at the next one.</summary>
    Task ReleaseLeaseAsync(Guid deletionId, string owner, CancellationToken cancellationToken = default);

    /// <summary>Requested past its expiry → Expired (audited).</summary>
    Task<bool> ExpireAsync(Guid deletionId, string owner, DateTimeOffset now, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Step 0, the fence (ADR-014 §4): in one transaction with the workspace row FOR UPDATE, checks approval, waiting
    /// period and holds, sets the workspace to Deleting with a new epoch, the deletion to Running and records the step.
    /// </summary>
    Task<DeletionStartOutcome> StartAsync(Guid deletionId, string owner, DateTimeOffset now, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Records (or returns the unfinished record of) a step's start after re-checking the legal hold in the same
    /// transaction. Throws <see cref="PreservationLockedException"/> under a hold.</summary>
    Task<WorkspaceDeletionStepRecord> BeginStepAsync(Guid deletionId, string owner, DeletionStep deletionStep, CancellationToken cancellationToken = default);

    /// <summary>Finishes a step and moves the run to <paramref name="nextStep"/> (null keeps the current step).</summary>
    Task CompleteStepAsync(
        Guid deletionId, string owner, DeletionStep deletionStep, string outcome, JsonObject? counts, JsonObject? detail, DeletionStep? nextStep,
        DateTimeOffset? nextStepAt, AuditEvent? audit, CancellationToken cancellationToken = default);

    /// <summary>Running → Halted (a legal hold), audited.</summary>
    Task HaltAsync(Guid deletionId, string owner, DateTimeOffset now, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Halted → Running once no hold is active (checked in the transaction); false while still held.</summary>
    Task<bool> ResumeAsync(Guid deletionId, string owner, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Records a transient error on the run (shown in status; the step is retried at the next pass).</summary>
    Task RecordErrorAsync(Guid deletionId, string owner, string? message, CancellationToken cancellationToken = default);

    /// <summary>Every unfinished job of the workspace → Cancelled (reason: workspace deletion). Returns how many.</summary>
    Task<int> CancelJobsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<WorkspaceWorkInFlight> CountWorkInFlightAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>The tenant tables the purge removes rows from and their foreign keys between each other.</summary>
    Task<(IReadOnlyList<string> Tables, IReadOnlyList<PurgeTableReference> References)> GetPurgeSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes one batch of one table; returns the rows deleted (0 when the table is done).</summary>
    Task<long> PurgeBatchAsync(Guid deletionId, string table, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Rows per tenant table of the deletion's workspace (counted as the owner).</summary>
    Task<IReadOnlyList<PurgeTableCount>> CountRowsAsync(Guid deletionId, CancellationToken cancellationToken = default);

    Task<WorkspaceKeyCounts> CountKeysAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The last step: stores the certificate, sets the workspace Purged and the deletion Completed (or
    /// CompletedWithResiduals), writes <paramref name="workspaceAudit"/> — one transaction, idempotent.
    /// </summary>
    Task CertifyAsync(
        Guid deletionId, string owner, StoredDestructionCertificate certificate, bool residuals, DateTimeOffset finishedAt,
        AuditEvent workspaceAudit, CancellationToken cancellationToken = default);

    /// <summary>Writes an installation-level audit event (no workspace) exactly once per event id.</summary>
    Task WriteInstallationAuditAsync(AuditEvent audit, CancellationToken cancellationToken = default);
}

/// <summary>A workspace (or its search placement) is being deleted: nothing new may be written for it (SQLSTATE O0410).</summary>
public sealed class WorkspaceFencedException : Exception
{
    public WorkspaceFencedException()
        : this(Guid.Empty, "data")
    {
    }

    public WorkspaceFencedException(string message)
        : base(message)
    {
        Target = "data";
    }

    public WorkspaceFencedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Target = "data";
    }

    public WorkspaceFencedException(Guid workspaceId, string target, Exception? innerException = null)
        : base($"Workspace {workspaceId} is being deleted; {target} accepts no new data.", innerException)
    {
        WorkspaceId = workspaceId;
        Target = target;
    }

    public Guid WorkspaceId { get; }

    public string Target { get; }
}
