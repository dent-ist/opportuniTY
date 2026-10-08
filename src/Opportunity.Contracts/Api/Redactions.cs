namespace Opportunity.Contracts.Api;

/// <summary>Who created or last changed a redaction, a Redaction Set or a reason.</summary>
public sealed record RedactionActorResource(Guid UserId, string DisplayName);

/// <summary>
/// A workspace Redaction Set (E11-T04): a named, versioned layer of redactions. The viewer edits one set at a time and
/// a production chooses one. A set is retired, never deleted; a retired set is read-only.
/// </summary>
/// <param name="Version">Optimistic-concurrency version; also the ETag (<c>If-Match</c> on change).</param>
public sealed record RedactionSetResource(
    Guid RedactionSetId,
    string Name,
    string? Description,
    bool Retired,
    RedactionActorResource? ModifiedBy,
    DateTimeOffset ModifiedAt,
    long Version);

/// <summary><c>GET …/redaction-sets</c>: every Redaction Set of the workspace, active ones first, by name.</summary>
public sealed record RedactionSetList(IReadOnlyList<RedactionSetResource> Items);

/// <summary>Body of <c>POST</c> (create) and <c>PUT</c> (replace) <c>…/redaction-sets</c>.</summary>
/// <param name="Name">1–200 characters, unique in the workspace (case-insensitive).</param>
/// <param name="Retired">Retire (true) or reactivate (false) the set; ignored on create.</param>
public sealed record RedactionSetRequest(string? Name, string? Description = null, bool Retired = false);

public enum RedactionReasonCategoryResource
{
    Privilege,
    Privacy,
    Other,
}

/// <summary>
/// One reason of the workspace picklist (ADR-012 §3.2): privilege reasons (attorney-client, work product …) and privacy
/// reasons (PII, PHI, Personal Data – GDPR …) are separate categories; the privilege log uses the category.
/// </summary>
/// <param name="Code">Immutable key (letters and digits), stored with every redaction.</param>
/// <param name="BoxLabel">The text a labelled box prints at production, e.g. "Redacted – Privileged".</param>
/// <param name="Active">Only active reasons can be chosen for new redactions; existing ones keep theirs.</param>
public sealed record RedactionReasonResource(
    string Code,
    string Name,
    RedactionReasonCategoryResource Category,
    string BoxLabel,
    bool Active,
    int SortOrder,
    long Version);

/// <summary><c>GET …/redaction-reasons</c>: the picklist in display order.</summary>
public sealed record RedactionReasonList(IReadOnlyList<RedactionReasonResource> Items);

/// <summary>Body of <c>POST …/redaction-reasons</c> (with <see cref="Code"/>) and <c>PUT …/redaction-reasons/{reasonCode}</c>.</summary>
public sealed record RedactionReasonRequest(
    string? Name,
    RedactionReasonCategoryResource? Category,
    string? BoxLabel,
    bool Active = true,
    int? SortOrder = null,
    string? Code = null);

public enum RedactionTypeResource
{
    /// <summary>An opaque black box.</summary>
    Black,

    /// <summary>A white box with a black border and the reason's box label printed inside.</summary>
    Labelled,
}

/// <summary>
/// A rectangle in normalized page space (ADR-012 §2): origin top-left of the page at rotation 0, in millionths of the
/// page width (x, w) and height (y, h); <c>0 ≤ x &lt; x + w ≤ 1,000,000</c>, the same for y. It applies unchanged to
/// every raster of the page at any resolution.
/// </summary>
public sealed record RedactionRectResource(int X, int Y, int W, int H);

/// <summary>One active redaction of a document in a Redaction Set.</summary>
/// <param name="PageSetId">The page set the redaction belongs to (ADR-012 §3.6).</param>
/// <param name="OnActivePageSet">False when the document's active page set changed since: review the redaction.</param>
/// <param name="ChangedAtVersion">The redaction version that last changed it.</param>
public sealed record RedactionResource(
    Guid RedactionId,
    Guid PageSetId,
    int PageNumber,
    bool OnActivePageSet,
    RedactionRectResource Rect,
    RedactionTypeResource Type,
    string ReasonCode,
    string ReasonName,
    RedactionReasonCategoryResource ReasonCategory,
    string? Note,
    RedactionActorResource CreatedBy,
    DateTimeOffset CreatedAt,
    RedactionActorResource ModifiedBy,
    DateTimeOffset ModifiedAt,
    long ChangedAtVersion);

/// <summary>
/// <c>GET …/documents/{documentId}/redaction-sets/{redactionSetId}</c>: the document's redactions in the set at the
/// current version (the ETag) or as of an earlier one, and whether new redactions can be drawn.
/// </summary>
/// <param name="Version">The redaction version (0 before the first save); also the ETag.</param>
/// <param name="CurrentVersion">The newest version (equals <see cref="Version"/> unless an earlier one was asked for).</param>
/// <param name="ActivePageSetId">The page set redactions are drawn on.</param>
/// <param name="Redactable">The active page set is rendered (Ready); pages with an image can be redacted.</param>
/// <param name="UnavailableReason">Why redactions cannot be drawn now ("Redaction requires rendered images"); null when they can.</param>
/// <param name="LastChange">Who saved the newest version, and when.</param>
public sealed record DocumentRedactionsResource(
    Guid DocumentId,
    Guid RedactionSetId,
    long Version,
    long CurrentVersion,
    Guid? ActivePageSetId,
    bool Redactable,
    string? UnavailableReason,
    bool SetRetired,
    RedactionChangeResource? LastChange,
    IReadOnlyList<RedactionResource> Redactions);

/// <summary>Who saved a redaction version, and when.</summary>
public sealed record RedactionChangeResource(RedactionActorResource Actor, DateTimeOffset At, long Version);

public enum RedactionOperationResource
{
    Add,
    Modify,
    Remove,
}

/// <summary>
/// One change of a save. <c>add</c> needs page, rect, type and reason (the redaction id is optional, assigned when
/// absent); <c>modify</c> names the redaction and the properties to change; <c>remove</c> names the redaction only.
/// </summary>
/// <param name="Note">Free text, at most 1,000 characters; never copied into audit. An empty string clears it.</param>
public sealed record RedactionChangeRequest(
    RedactionOperationResource Operation,
    Guid? RedactionId = null,
    int? PageNumber = null,
    RedactionRectResource? Rect = null,
    RedactionTypeResource? Type = null,
    string? ReasonCode = null,
    string? Note = null);

/// <summary>
/// Body of <c>POST …/documents/{documentId}/redaction-sets/{redactionSetId}/revisions</c> with <c>If-Match</c>: all
/// changes apply together as one new redaction version, or none does.
/// </summary>
public sealed record SaveRedactionsRequest(IReadOnlyList<RedactionChangeRequest>? Changes);

/// <summary>One row of a redaction's history: what a version did to it (removed redactions stay in the history).</summary>
public sealed record RedactionRevisionResource(
    long Version,
    Guid RedactionId,
    RedactionOperationResource Operation,
    Guid PageSetId,
    int PageNumber,
    RedactionRectResource Rect,
    RedactionTypeResource Type,
    string ReasonCode,
    string? Note,
    RedactionActorResource Actor,
    DateTimeOffset At);

/// <summary>
/// <c>GET …/documents/{documentId}/redaction-sets/{redactionSetId}/history</c>: the revisions, newest first (at most
/// 2,000; <see cref="Truncated"/> when older ones exist).
/// </summary>
public sealed record RedactionHistoryResource(
    Guid DocumentId, Guid RedactionSetId, long CurrentVersion, bool Truncated, IReadOnlyList<RedactionRevisionResource> Items);
