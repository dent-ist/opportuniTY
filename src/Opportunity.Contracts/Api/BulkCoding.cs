using System.Text.Json.Nodes;

namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/bulk-coding</c> ("Mass Edit", E10-T04): apply the same coding
/// operations to every document of a Ready frozen set (a snapshot created with purpose <c>BulkCoding</c>). Requires
/// <c>Idempotency-Key</c>. The answer is <c>202 Accepted</c> with the job (<see cref="JobResource"/>); follow progress
/// with <c>GET …/jobs/{jobId}</c> and the per-document outcomes with <c>GET …/bulk-coding/{jobId}/report</c>.
/// </summary>
/// <param name="SnapshotId">The frozen set to code; its membership never changes (ADR-002).</param>
/// <param name="Changes">1–100 changes, at most one per field (the change list of <c>PUT …/documents/{id}/coding</c> plus clear).</param>
public sealed record BulkCodingRequest(Guid SnapshotId, IReadOnlyList<BulkCodingOperationRequest> Changes);

/// <param name="Operation">
/// set (replace the value; null clears), addChoices / removeChoices (multiple choice only: ensure the given choices are
/// present / absent, keeping the others), clear (remove the value; no <paramref name="Value"/>).
/// </param>
/// <param name="Value">Canonical value for set; an array of choice ids for addChoices/removeChoices; omitted for clear.</param>
public sealed record BulkCodingOperationRequest(int FieldId, BulkCodingOperationKind Operation, JsonNode? Value = null);

public enum BulkCodingOperationKind
{
    Set,
    AddChoices,
    RemoveChoices,
    Clear,
}

/// <summary>What happened to one document of a bulk coding job.</summary>
public enum BulkCodingOutcomeResource
{
    /// <summary>At least one value changed.</summary>
    Applied,

    /// <summary>
    /// Left unchanged because someone else changed a requested field after the set was frozen (Q-07), or the document
    /// was deleted since. <c>fieldIds</c> lists the fields that were skipped.
    /// </summary>
    SkippedChanged,

    /// <summary>Excluded because the job's owner can no longer access the document (reason <c>AccessChanged</c>).</summary>
    SkippedHidden,

    /// <summary>Could not be applied.</summary>
    Failed,
}

/// <summary>
/// One document of a bulk coding report: <c>GET …/bulk-coding/{jobId}/report?outcome=…</c>, cursor-paged.
/// </summary>
/// <param name="ReasonCode">Changed, ConcurrentEdit, DocumentDeleted, AccessChanged or an error code.</param>
/// <param name="FieldIds">The skipped fields of a SkippedChanged document; empty otherwise.</param>
public sealed record BulkCodingReportItemResource(
    Guid DocumentId, BulkCodingOutcomeResource Outcome, string ReasonCode, IReadOnlyList<int> FieldIds);
