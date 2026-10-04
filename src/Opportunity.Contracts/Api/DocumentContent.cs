namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/documents/{documentId}/views</c>: the viewer displayed the
/// document as the active document (<c>Document.Viewed</c>). <see cref="RetrievalId"/> is the
/// <c>X-Opportunity-Retrieval-Id</c> header of the gateway response that delivered what was displayed.
/// </summary>
public sealed record DocumentViewRecord(Guid? RetrievalId);

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/documents/{documentId}</c> (E11-T01): what the viewer needs to open a
/// document — its field values with display hints, and which artifacts exist (text chunks, pages, native).
/// </summary>
/// <param name="DocumentVersion">Authoritative DocumentVersion (ADR-001); changes with every write to the document.</param>
/// <param name="DisplayTimeZone">The workspace's display time zone (IANA id, Q-28); <c>displayValue</c>s of instants use it.</param>
/// <param name="FamilyId">Family root (a standalone document is its own family).</param>
/// <param name="ParentDocumentId">Immediate parent within the family; null for the family root.</param>
/// <param name="Fields">Every field the caller may see, in field-id order; fields without a value have a null value.</param>
public sealed record DocumentResource(
    Guid DocumentId,
    string ControlNumber,
    long DocumentVersion,
    string DisplayTimeZone,
    Guid FamilyId,
    Guid? ParentDocumentId,
    DocumentTextInfo Text,
    DocumentNativeInfo Native,
    DocumentImagesInfo Images,
    IReadOnlyList<DocumentFieldValueResource> Fields);

/// <summary>The extracted text and how to page through it with <c>GET …/text/chunks/{chunkIndex}</c>.</summary>
/// <param name="Available">A stored text exists and can be read.</param>
/// <param name="Missing">Import or processing reported no extracted text (show "No text").</param>
/// <param name="Truncated">Only the first part of the text is searchable (Q-29); the viewer still gets the whole text.</param>
/// <param name="EncodingWarning">The text was decoded with replacement characters at import.</param>
/// <param name="SizeBytes">Size of the stored UTF-8 text.</param>
/// <param name="Length">Length in characters, when known.</param>
/// <param name="ChunkSizeBytes">Fixed chunk size; chunk <c>n</c> starts at byte <c>n × ChunkSizeBytes</c>.</param>
/// <param name="ChunkCount">Number of chunks (0 when no text is available).</param>
public sealed record DocumentTextInfo(
    bool Available,
    bool Missing,
    bool Truncated,
    bool EncodingWarning,
    long? SizeBytes,
    long? Length,
    int ChunkSizeBytes,
    int ChunkCount);

/// <param name="Available">A native file is stored (downloading it needs <c>Document.DownloadNative</c>).</param>
/// <param name="Missing">The load file referenced a native that was not delivered.</param>
public sealed record DocumentNativeInfo(bool Available, bool Missing, long? SizeBytes, string? FileExtension);

/// <summary>The active page set (ADR-012); list its pages with <c>GET …/pages</c>.</summary>
/// <param name="Available">The document has an active page set.</param>
/// <param name="Incomplete">Some page images are missing or still being rendered.</param>
public sealed record DocumentImagesInfo(
    bool Available,
    bool Incomplete,
    int PageCount,
    DocumentPageSetSource? Source,
    DocumentPageSetStatus? Status);

public enum DocumentPageSetSource
{
    Imported,
    Rendered,
}

public enum DocumentPageSetStatus
{
    Pending,
    Ready,
    Incomplete,
    Failed,
}

/// <summary>One field value of a document for the metadata view.</summary>
/// <param name="TypeLabel">Practitioner type name (Long Text, Short Text, Whole Number, Yes/No, …).</param>
/// <param name="Format">How to present the value.</param>
/// <param name="Value">Canonical value (ADR-003 §3): string, number, boolean, choice id(s) or an array; null when empty.</param>
/// <param name="DisplayValue">Ready-to-show text (instants in the display time zone, choice names, Yes/No); null when empty.</param>
/// <param name="RawValue">The string as imported, when import normalized it (e.g. a date in another format).</param>
/// <param name="Choices">Choice fields: the selected choices.</param>
public sealed record DocumentFieldValueResource(
    int FieldId,
    string DisplayName,
    FieldResourceType Type,
    string TypeLabel,
    FieldResourceStorage Storage,
    DocumentFieldFormat Format,
    bool MultiValue,
    bool IsSystem,
    bool IsHidden,
    int? DecimalScale,
    System.Text.Json.Nodes.JsonNode? Value,
    string? DisplayValue,
    string? RawValue,
    IReadOnlyList<FieldChoiceResource>? Choices);

/// <summary>Display hint of a field value.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names of the display formats.")]
public enum DocumentFieldFormat
{
    Text,
    LongText,
    Identifier,
    Number,
    Decimal,
    Bytes,
    Date,
    DateTime,
    Boolean,
    Choice,
    User,
}

/// <summary>
/// <c>GET …/documents/{documentId}/text/chunks/{chunkIndex}</c> (E11-T01): one fixed-size chunk of the extracted text.
/// Chunks never split a character and concatenate, in order, to the whole text.
/// </summary>
/// <param name="ByteStart">First byte of the stored UTF-8 text this chunk covers.</param>
/// <param name="ByteEnd">Byte after the last one this chunk covers (the next chunk's <c>ByteStart</c>).</param>
/// <param name="TotalBytes">Size of the stored text.</param>
/// <param name="IsLast">No further chunk exists.</param>
/// <param name="Missing">The document has no stored text: no chunks at all (<c>ChunkCount</c> 0).</param>
public sealed record DocumentTextChunkResource(
    int ChunkIndex,
    int ChunkCount,
    int ChunkSizeBytes,
    long ByteStart,
    long ByteEnd,
    long TotalBytes,
    bool IsLast,
    string Text,
    bool Truncated,
    bool Missing,
    bool EncodingWarning);

/// <summary>One page of the active page set (ADR-012 §1); page numbers are 1-based.</summary>
/// <param name="WidthPt">Page width in points (1/72 in), the coordinate space of redactions.</param>
/// <param name="Rotation">Clockwise display rotation in degrees (0, 90, 180, 270).</param>
/// <param name="ImageMissing">The source image of this page is missing.</param>
/// <param name="HasImage">A review image can be fetched from <c>…/pages/{pageNumber}/image</c>.</param>
/// <param name="ImageContentType">Its type (image/png, image/jpeg or image/webp).</param>
/// <param name="HasThumbnail">A thumbnail can be fetched from <c>…/pages/{pageNumber}/thumbnail</c>.</param>
public sealed record DocumentPageResource(
    int PageNumber,
    decimal WidthPt,
    decimal HeightPt,
    int Rotation,
    DocumentPageColorMode ColorMode,
    bool ImageMissing,
    bool HasImage,
    string? ImageContentType,
    int? ImageWidthPx,
    int? ImageHeightPx,
    bool HasThumbnail);

public enum DocumentPageColorMode
{
    Bitonal,
    Gray,
    Color,
}
