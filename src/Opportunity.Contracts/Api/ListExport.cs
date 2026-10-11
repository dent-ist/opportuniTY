using Opportunity.Contracts.Search;

namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceId}/list-exports</c> (#181): the document list's current view written to CSV.
/// Give exactly one source: <see cref="Query"/> (the list's effective query; empty lists every document) or
/// <see cref="DocumentIds"/> (rows checked one by one). Control Number is always the first column.
/// </summary>
/// <param name="FieldIds">The view's columns in order (field IDs of <c>GET …/fields</c>); Control Number need not be listed.</param>
/// <param name="Expand">"Include: Family / Duplicates / Email thread" of the list, applied before the selection is frozen.</param>
/// <param name="Name">A name for the export (default "Export List" with the date).</param>
public sealed record CreateListExportRequest(
    IReadOnlyList<int>? FieldIds,
    string? Query = null,
    IReadOnlyList<Guid>? DocumentIds = null,
    SearchExpand? Expand = null,
    string? Name = null);

public enum ListExportStatusResource
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>One column of a list export as written in the header row.</summary>
public sealed record ListExportColumnResource(int FieldId, string Header);

/// <summary>
/// A list export (#181). The file (<c>…/list-exports/{exportId}/content</c>, protected-content gateway) holds one row per
/// document of the frozen selection in its order, without the documents you could no longer export when the job ran
/// (<see cref="DocumentsExcluded"/>, Q-15).
/// </summary>
/// <param name="SnapshotId">The frozen selection the rows come from.</param>
/// <param name="DocumentCount">Documents in the frozen selection; null while it is being frozen.</param>
/// <param name="RowCount">Rows written (documents exported); null until completed.</param>
/// <param name="DocumentsExcluded">Documents left out because you could no longer export them when their part ran (Q-15).</param>
/// <param name="ContentSha256">SHA-256 of the file as downloaded (byte-order mark and header included).</param>
public sealed record ListExportResource(
    Guid ExportId,
    Guid JobId,
    string Name,
    ListExportStatusResource Status,
    string? StatusReason,
    Guid SnapshotId,
    long? DocumentCount,
    IReadOnlyList<ListExportColumnResource> Columns,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    long? RowCount,
    long? DocumentsExcluded,
    long? ByteCount,
    string? ContentSha256);
