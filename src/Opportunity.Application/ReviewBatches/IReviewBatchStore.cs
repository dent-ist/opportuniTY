using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.ReviewBatches;

namespace Opportunity.Application.ReviewBatches;

/// <summary>A stored Batch Set with its batch counts by status.</summary>
public sealed record ReviewBatchSetRecord
{
    public required Guid BatchSetId { get; init; }

    public required string Name { get; init; }

    public required string BatchPrefix { get; init; }

    public required int MaxBatchSize { get; init; }

    public required bool KeepFamiliesTogether { get; init; }

    public required bool KeepThreadsTogether { get; init; }

    public required ReviewPass Pass { get; init; }

    public Guid? QcOfBatchSetId { get; init; }

    public string? ReviewerGroup { get; init; }

    public required Guid SnapshotId { get; init; }

    public required int BatchCount { get; init; }

    /// <summary>Frozen member count (every member, whoever may see it).</summary>
    public required long DocumentCount { get; init; }

    public int Available { get; init; }

    public int CheckedOut { get; init; }

    public int Completed { get; init; }

    public required Guid CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A stored batch with the settings of its set that decide who may hold it.</summary>
public sealed record ReviewBatchRecord
{
    public required Guid BatchId { get; init; }

    public required Guid BatchSetId { get; init; }

    public required string BatchSetName { get; init; }

    public required int Ordinal { get; init; }

    public required string Name { get; init; }

    /// <summary>Frozen member count (every member, whoever may see it).</summary>
    public required int DocumentCount { get; init; }

    public required ReviewBatchStatus Status { get; init; }

    public Guid? AssigneeId { get; init; }

    public required DateTimeOffset StatusChangedAt { get; init; }

    public Guid? StatusChangedBy { get; init; }

    public required long Version { get; init; }

    public required ReviewPass Pass { get; init; }

    public string? ReviewerGroup { get; init; }
}

/// <summary>What <see cref="IReviewBatchStore.CreateSetAsync"/> cuts and stores.</summary>
public sealed record NewReviewBatchSet
{
    public required Guid WorkspaceId { get; init; }

    public Guid BatchSetId { get; init; } = Guid.CreateVersion7();

    public required string Name { get; init; }

    public required string BatchPrefix { get; init; }

    public required int MaxBatchSize { get; init; }

    public required bool KeepFamiliesTogether { get; init; }

    public required bool KeepThreadsTogether { get; init; }

    public required ReviewPass Pass { get; init; }

    public Guid? QcOfBatchSetId { get; init; }

    public string? ReviewerGroup { get; init; }

    public required Guid SnapshotId { get; init; }

    public required Guid CreatedBy { get; init; }

    /// <summary>The audit event of the creation, built from the stored set and written in its transaction.</summary>
    public required Func<ReviewBatchSetRecord, AuditEvent> Audit { get; init; }
}

public enum ReviewBatchSetCreateStatus
{
    Created,

    /// <summary>The snapshot is not a Ready ReviewBatch snapshot (any more): 409.</summary>
    SnapshotNotReady,

    /// <summary>Another Batch Set uses the prefix: 409.</summary>
    PrefixTaken,

    /// <summary>The first-pass set named by a QC set does not exist or is not a first-pass set.</summary>
    QcSourceNotFound,
}

public sealed record ReviewBatchSetCreation(ReviewBatchSetCreateStatus Status, ReviewBatchSetRecord? Set = null);

/// <summary>Filters of the batch list; every filter is optional.</summary>
public sealed record ReviewBatchFilter(Guid? BatchSetId = null, ReviewBatchStatus? Status = null, Guid? AssigneeId = null);

/// <summary>Keyset position of the batch list: (set, ordinal), sets oldest first.</summary>
public readonly record struct ReviewBatchCursor(Guid BatchSetId, int Ordinal);

/// <summary>A member of a batch.</summary>
public sealed record ReviewBatchMember(Guid DocumentId, string ControlNumber, int Position);

public enum ReviewBatchTransitionKind
{
    /// <summary>The actor takes an Available batch.</summary>
    CheckOut,

    /// <summary>A Checked-out batch goes back to Available.</summary>
    Return,

    /// <summary>A Checked-out batch is done.</summary>
    Complete,

    /// <summary>A manager checks the batch out to <see cref="ReviewBatchTransition.AssigneeId"/>, or makes it Available (null).</summary>
    Assign,
}

/// <summary>A status change of one batch, applied only if the batch is still at <see cref="ExpectedVersion"/>.</summary>
public sealed record ReviewBatchTransition
{
    public required Guid WorkspaceId { get; init; }

    public required Guid BatchId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required ReviewBatchTransitionKind Kind { get; init; }

    public required Guid ActorId { get; init; }

    public Guid? AssigneeId { get; init; }

    /// <summary>The audit event, built from the batch before and after and written in the transaction of the change.</summary>
    public required Func<ReviewBatchRecord, ReviewBatchRecord, AuditEvent> Audit { get; init; }
}

public enum ReviewBatchTransitionStatus
{
    Ok,
    NotFound,

    /// <summary>The batch changed since it was read: 412 with the current batch.</summary>
    VersionConflict,

    /// <summary>The batch is not in a status the change starts from (e.g. checking out a checked-out batch): 409.</summary>
    InvalidState,
}

public sealed record ReviewBatchTransitionResult(ReviewBatchTransitionStatus Status, ReviewBatchRecord? Batch);

/// <summary>A reviewer's last call on a field of a document while holding a batch of the set.</summary>
public sealed record ReviewCall(JsonNode? Value, Guid ReviewerId, DateTimeOffset At, Guid BatchId, string BatchName, Guid EventId);

/// <summary>A first-pass call and a different QC call on the same document and field.</summary>
public sealed record ReviewConflict(Guid DocumentId, string ControlNumber, int FieldId, ReviewCall FirstPass, ReviewCall Qc);

/// <summary>Keyset position of the conflict list: (document, field).</summary>
public readonly record struct ReviewConflictCursor(Guid DocumentId, int FieldId);

/// <summary>
/// PostgreSQL storage of review batches (V0048). Every call runs in the workspace's RLS context; authorization and the
/// audit events' content belong to <see cref="ReviewBatchService"/>.
/// </summary>
public interface IReviewBatchStore
{
    /// <summary>
    /// In one transaction: checks the snapshot (Ready, purpose ReviewBatch) and the QC source, streams the snapshot's
    /// members in order grouped by family (or thread), cuts them into batches with <see cref="ReviewBatchPacker"/>,
    /// stores the set, its batches and their frozen membership, and writes the audit event.
    /// </summary>
    Task<ReviewBatchSetCreation> CreateSetAsync(NewReviewBatchSet batchSet, CancellationToken cancellationToken = default);

    Task<ReviewBatchSetRecord?> GetSetAsync(Guid workspaceId, Guid batchSetId, CancellationToken cancellationToken = default);

    /// <summary>Sets newest first, after <paramref name="after"/> (a set ID; IDs are time-ordered).</summary>
    Task<IReadOnlyList<ReviewBatchSetRecord>> ListSetsAsync(Guid workspaceId, Guid? after, int limit, CancellationToken cancellationToken = default);

    Task<ReviewBatchRecord?> GetBatchAsync(Guid workspaceId, Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>Batches by (set, ordinal), sets oldest first.</summary>
    Task<IReadOnlyList<ReviewBatchRecord>> ListBatchesAsync(
        Guid workspaceId, ReviewBatchFilter filter, ReviewBatchCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Members of each batch (or set, with <paramref name="bySet"/>) that <paramref name="visibility"/> leaves visible
    /// and that are not deleted: the counts a caller may see (Q-52).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, long>> CountVisibleAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> ids, bool bySet, VisibilityFilter visibility, CancellationToken cancellationToken = default);

    /// <summary>Members of a batch in position order, after <paramref name="afterPosition"/>.</summary>
    Task<IReadOnlyList<ReviewBatchMember>> ListMembersAsync(
        Guid workspaceId, Guid batchId, int afterPosition, int limit, CancellationToken cancellationToken = default);

    Task<ReviewBatchTransitionResult> TransitionAsync(ReviewBatchTransition transition, CancellationToken cancellationToken = default);
}
