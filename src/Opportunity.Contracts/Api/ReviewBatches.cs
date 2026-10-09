using System.Text.Json.Nodes;

namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/review-batch-sets</c> (E10-T05): cut a frozen document set into
/// review batches. Freeze the set first with <c>POST …/snapshots</c> (purpose <c>ReviewBatch</c>; from a saved search,
/// query, document IDs or another snapshot, with family/thread expansion as needed).
/// </summary>
/// <param name="Name">Display name of the Batch Set.</param>
/// <param name="SnapshotId">A Ready snapshot with purpose ReviewBatch that you created.</param>
/// <param name="BatchPrefix">Batches are named <c>{prefix}_0001</c>, <c>{prefix}_0002</c>, …; unique in the workspace.</param>
/// <param name="MaxBatchSize">Documents per batch (1–10,000); a batch is larger only when one family or thread alone is.</param>
/// <param name="KeepFamiliesTogether">A family is never split across batches (default true).</param>
/// <param name="KeepThreadsTogether">An email thread (with its families) is never split across batches.</param>
/// <param name="ReviewPass"><c>firstPass</c> (default) or <c>qc</c>; a QC set names the first-pass set it checks.</param>
/// <param name="QcOfBatchSetId">For <c>qc</c>: the first-pass Batch Set whose calls this pass is compared with.</param>
/// <param name="ReviewerGroup">Optional IdP group: only its members may check batches out (managers may still assign anyone).</param>
public sealed record CreateReviewBatchSetRequest(
    string Name,
    Guid SnapshotId,
    string BatchPrefix,
    int MaxBatchSize,
    bool KeepFamiliesTogether = true,
    bool KeepThreadsTogether = false,
    ReviewPassResource ReviewPass = ReviewPassResource.FirstPass,
    Guid? QcOfBatchSetId = null,
    string? ReviewerGroup = null);

/// <summary>A Batch Set: the batches cut from one frozen document set. Membership never changes after creation.</summary>
/// <param name="DocumentCount">Documents of the set you may see (documents hidden from you are not counted, Q-52).</param>
/// <param name="BatchCount">Batches in the set.</param>
/// <param name="StatusCounts">Batches by status: available, checkedOut, completed.</param>
public sealed record ReviewBatchSetResource(
    Guid BatchSetId,
    string Name,
    string BatchPrefix,
    int MaxBatchSize,
    bool KeepFamiliesTogether,
    bool KeepThreadsTogether,
    ReviewPassResource ReviewPass,
    Guid? QcOfBatchSetId,
    string? ReviewerGroup,
    Guid SnapshotId,
    int BatchCount,
    long DocumentCount,
    ReviewBatchStatusCounts StatusCounts,
    ReviewBatchUserResource CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record ReviewBatchStatusCounts(int Available, int CheckedOut, int Completed);

/// <summary>
/// A review batch (<c>GET …/review-batches/{batchId}</c>). The <c>ETag</c> header carries <see cref="Version"/>; send it
/// as <c>If-Match</c> to check out, check in or assign.
/// </summary>
/// <param name="Name"><c>{prefix}_0001</c>…</param>
/// <param name="DocumentCount">Documents of the batch you may see (Q-52).</param>
/// <param name="Assignee">The reviewer holding the batch (checked out) or who completed it; null when available.</param>
/// <param name="StatusChangedBy">Who last checked the batch out, in or assigned it; null when never changed.</param>
public sealed record ReviewBatchResource(
    Guid BatchId,
    Guid BatchSetId,
    string BatchSetName,
    int Ordinal,
    string Name,
    ReviewPassResource ReviewPass,
    ReviewBatchStatusResource Status,
    int DocumentCount,
    ReviewBatchUserResource? Assignee,
    DateTimeOffset StatusChangedAt,
    Guid? StatusChangedBy,
    long Version);

/// <param name="DisplayName">Null when the user has no display name on record.</param>
public sealed record ReviewBatchUserResource(Guid UserId, string? DisplayName);

/// <summary>A member of a batch in review order. Documents hidden from you are omitted (Q-52).</summary>
public sealed record ReviewBatchDocumentResource(Guid DocumentId, string ControlNumber, int Position);

/// <summary>Body of <c>POST …/review-batches/{batchId}/check-in</c>.</summary>
/// <param name="Completed">True: the batch is done (Completed); false: return it unfinished (Available).</param>
public sealed record CheckInReviewBatchRequest(bool Completed);

/// <summary>Body of <c>PUT …/review-batches/{batchId}/assignment</c>.</summary>
/// <param name="AssigneeId">The reviewer to check the batch out to (a member who may code); null makes it Available.</param>
public sealed record ReviewBatchAssignmentRequest(Guid? AssigneeId);

/// <summary>
/// A disagreement between the first pass and QC (<c>GET …/review-batch-sets/{qcBatchSetId}/conflicts</c>): on a
/// document of the QC set, the QC reviewer's last call on a field differs from the first-pass reviewer's last call.
/// A call is a reviewer's own coding change of the document while they held its batch (CodingEvent provenance).
/// </summary>
public sealed record ReviewConflictResource(
    Guid DocumentId,
    string ControlNumber,
    int FieldId,
    string FieldName,
    ReviewCallResource FirstPass,
    ReviewCallResource Qc);

/// <param name="Value">The canonical value the reviewer set (null: cleared).</param>
/// <param name="EventId">The CodingEvent of the call.</param>
public sealed record ReviewCallResource(
    JsonNode? Value,
    ReviewBatchUserResource Reviewer,
    DateTimeOffset At,
    Guid BatchId,
    string BatchName,
    Guid EventId);

public enum ReviewPassResource
{
    FirstPass,
    Qc,
}

public enum ReviewBatchStatusResource
{
    Available,
    CheckedOut,
    Completed,
}
