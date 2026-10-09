using System.Text.Json.Nodes;

namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET</c>/<c>PUT /api/v1/workspaces/{workspaceId}/documents/{documentId}/coding</c> (E10-T01): a document's current
/// coding, by layout when one is named. The <c>ETag</c> header carries <see cref="DocumentVersion"/>; send it back as
/// <c>If-Match</c> when saving.
/// </summary>
/// <param name="DocumentVersion">Increases with every change of the document (coding, relationships, deletion).</param>
/// <param name="ProjectedVersion">
/// Highest version known to be searchable. Equal to <see cref="DocumentVersion"/> once the change is indexed, so the
/// panel can clear "saved · indexing".
/// </param>
/// <param name="IndexingState">Whether search has caught up with the document's coding.</param>
/// <param name="LayoutId">The layout the fields were selected by; null when every coding field is listed.</param>
/// <param name="Fields">Coding fields in layout order (field id order without a layout); fields you may not see are omitted.</param>
/// <param name="LastEditor">Who made the latest coding change among the listed fields; null when never coded.</param>
/// <param name="AccessRetained">
/// False only in the answer to a save confirmed with <c>confirmAccessLoss</c> that hid the document from you: the save
/// stands, <see cref="Fields"/> is empty and <see cref="LastEditor"/> null, and the document now answers 404 like any
/// document you may not see (E16-T08).
/// </param>
public sealed record DocumentCodingResource(
    Guid DocumentId,
    long DocumentVersion,
    long ProjectedVersion,
    CodingIndexingStateResource IndexingState,
    Guid? LayoutId,
    IReadOnlyList<CodingFieldValueResource> Fields,
    CodingEditorResource? LastEditor,
    bool AccessRetained = true);

/// <summary>One coding field of a document.</summary>
/// <param name="Value">
/// Canonical value (ADR-003 §3): string, number, boolean, <c>YYYY-MM-DD</c> or UTC instant for dates, a choice id for a
/// single choice, an array of choice ids (or strings) for multi-value fields; null when empty.
/// </param>
/// <param name="Editable">You may change it here: not read-only in the layout, Coding.Write, and Coding.WritePrivilege for security-affecting fields.</param>
/// <param name="IsSecurityAffecting">Changing it can change who may see the document (Q-11).</param>
/// <param name="ChangedAtVersion">The document version its last change produced.</param>
/// <param name="ChangedBy">User who changed it last.</param>
/// <param name="ChangedAt">When it was last changed.</param>
public sealed record CodingFieldValueResource(
    int FieldId,
    JsonNode? Value,
    bool Editable,
    bool IsSecurityAffecting,
    long? ChangedAtVersion,
    Guid? ChangedBy,
    DateTimeOffset? ChangedAt);

/// <param name="DisplayName">Null when the user has no display name on record.</param>
/// <param name="DocumentVersion">The version the change produced.</param>
/// <param name="JobId">Set when the change came from a bulk job.</param>
public sealed record CodingEditorResource(Guid UserId, string? DisplayName, DateTimeOffset ChangedAt, long DocumentVersion, Guid? JobId);

/// <summary>Search freshness of a document's coding (read-your-own-writes).</summary>
public enum CodingIndexingStateResource
{
    /// <summary>Every saved change is searchable.</summary>
    Indexed,

    /// <summary>Saved; search is catching up.</summary>
    Pending,

    /// <summary>Saved, but indexing failed; an administrator must repair the index.</summary>
    Failed,
}

/// <summary>
/// Body of <c>PUT …/documents/{documentId}/coding</c>: the changes of one save, applied together or not at all.
/// Requires <c>If-Match</c> with the ETag last read (<c>*</c> to save regardless); <c>Idempotency-Key</c> is optional
/// and makes a retried save return the original result instead of writing again.
/// </summary>
/// <param name="Changes">1–100 changes, at most one per field.</param>
/// <param name="LayoutId">
/// When set, the save is checked against this layout: only its editable fields may change, and its visible required
/// fields must have a value afterwards.
/// </param>
/// <param name="ConfirmAccessLoss">
/// Required when a change of a security-affecting field would hide the document from you (a restriction class your roles
/// may not see, an ethical wall naming you): without it such a save writes nothing and answers 409
/// <c>confirmation-required</c> with <c>reason: removes-own-access</c> (E16-T08).
/// </param>
public sealed record UpdateDocumentCodingRequest(IReadOnlyList<CodingChangeRequest> Changes, Guid? LayoutId = null, bool ConfirmAccessLoss = false);

/// <param name="Operation">set (a null value clears the field), addChoices or removeChoices (multiple choice only).</param>
/// <param name="Value">Canonical value for set; an array of choice ids for addChoices/removeChoices.</param>
public sealed record CodingChangeRequest(int FieldId, CodingOperationResource Operation, JsonNode? Value = null);

public enum CodingOperationResource
{
    Set,
    AddChoices,
    RemoveChoices,
}
