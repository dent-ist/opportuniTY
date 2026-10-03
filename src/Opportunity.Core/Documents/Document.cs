namespace Opportunity.Core.Documents;

/// <summary>
/// A reviewable document: the structural columns of ADR-003 §1. Imported custom metadata lives in
/// <see cref="Metadata"/> keyed by <c>"f" + FieldId</c>; coding never lives here (ADR-003 R2). DocumentVersion is kept
/// separately (ADR-001 §2) and bumped by the document repository.
/// </summary>
public sealed class Document
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Platform-generated UUIDv7, never shown as a business identifier (ADR-009 R1).</summary>
    public Guid DocumentId { get; set; }

    /// <summary>First-seen spelling; immutable.</summary>
    public string ControlNumber { get; set; } = string.Empty;

    /// <summary>Unique per workspace (see <see cref="Documents.ControlNumber.Normalize"/>); immutable.</summary>
    public string ControlNumberNorm { get; set; } = string.Empty;

    /// <summary>Natural sort key, computed by the database from <see cref="ControlNumberNorm"/>; never written.</summary>
    public string ControlNumberSortKey { get; set; } = string.Empty;

    public string? BegBates { get; set; }

    public string? EndBates { get; set; }

    public string? BegAttach { get; set; }

    public string? EndAttach { get; set; }

    /// <summary>DocumentId of the top-level parent; equals <see cref="DocumentId"/> for a standalone document.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>Immediate parent; null exactly for the family root.</summary>
    public Guid? ParentDocumentId { get; set; }

    /// <summary>0 for the family root, 1..n for the other members.</summary>
    public int FamilySequence { get; set; }

    public FamilyStatus FamilyStatus { get; set; }

    public Guid? DuplicateGroupId { get; set; }

    public bool IsDuplicatePrimary { get; set; }

    public Guid? EmailThreadId { get; set; }

    public EmailThreadSource? EmailThreadSource { get; set; }

    public byte[]? Md5 { get; set; }

    public byte[]? Sha1 { get; set; }

    public byte[]? Sha256 { get; set; }

    /// <summary>Upstream dedupe/email hash as lower-case hex.</summary>
    public string? UpstreamDedupeHash { get; set; }

    /// <summary>Which upstream hash <see cref="UpstreamDedupeHash"/> holds; set exactly when it is.</summary>
    public DuplicateHashKind? UpstreamDedupeHashKind { get; set; }

    public string? FileName { get; set; }

    public string? FileExtension { get; set; }

    public string? FileType { get; set; }

    public string? MimeType { get; set; }

    public long? FileSize { get; set; }

    public int? PageCount { get; set; }

    public DateTimeOffset? DateSent { get; set; }

    public DateTimeOffset? DateReceived { get; set; }

    public DateTimeOffset? DateCreated { get; set; }

    public DateTimeOffset? DateLastModified { get; set; }

    public DateTimeOffset? DocumentDate { get; set; }

    public DocumentDateSource? DocumentDateSource { get; set; }

    public DateTimeOffset? FamilyDate { get; set; }

    /// <summary>StoredObject (ADR-011) holding the native; must be registered for this document.</summary>
    public Guid? NativeObjectId { get; set; }

    /// <summary>StoredObject (ADR-011) holding the full extracted text; must be registered for this document.</summary>
    public Guid? TextObjectId { get; set; }

    public long? TextLength { get; set; }

    /// <summary>PageSet used by the viewer, redaction and production (ADR-012 §1.2).</summary>
    public Guid? ActivePageSetId { get; set; }

    public bool TextTruncated { get; set; }

    public bool TextMissing { get; set; }

    public bool NativeMissing { get; set; }

    public bool ImagesIncomplete { get; set; }

    public bool TextEncodingWarning { get; set; }

    /// <summary>Canonical imported metadata as a JSON object (ADR-003 §3).</summary>
    public string Metadata { get; set; } = "{}";

    /// <summary>Original strings of coerced values as a JSON object (ADR-003 R9); never searched or projected.</summary>
    public string? MetadataRaw { get; set; }

    public Guid? FirstImportBatchId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Creates a standalone document (a family of one) with a new UUIDv7 id and a normalized control number.</summary>
    public static Document Create(Guid workspaceId, string controlNumber, bool caseSensitive, string? importPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(controlNumber);
        var norm = Documents.ControlNumber.Normalize(controlNumber, caseSensitive, importPrefix);
        var id = Guid.CreateVersion7();
        return new Document
        {
            WorkspaceId = workspaceId,
            DocumentId = id,
            ControlNumber = (importPrefix + controlNumber).Trim(),
            ControlNumberNorm = norm,
            ControlNumberSortKey = Documents.ControlNumber.SortKey(norm),
            FamilyId = id,
        };
    }
}
