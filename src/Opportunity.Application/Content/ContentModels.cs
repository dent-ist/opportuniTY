using Opportunity.Application.Authorization;
using Opportunity.Application.Storage;

namespace Opportunity.Application.Content;

/// <summary>What the protected-content gateway can deliver for a document (ADR-015 D12, ADR-012 page model).</summary>
public enum ContentRendition
{
    /// <summary>The original native file; download only, never inline (Q-36, D12.6).</summary>
    Native,

    /// <summary>Extracted or load-file text, as <c>text/plain; charset=utf-8</c>.</summary>
    Text,

    /// <summary>A review page image of the active page set (PNG, JPEG or WebP).</summary>
    PageImage,

    /// <summary>A page thumbnail of the active page set.</summary>
    Thumbnail,

    /// <summary>The document's field values (JSON; read from PostgreSQL, never from object storage).</summary>
    Metadata,

    /// <summary>The page list of the active page set (JSON; ADR-012 page model).</summary>
    PageList,
}

/// <summary>Why the content is fetched; decides the permission and the audit action (ADR-013 §5).</summary>
public enum ContentPurpose
{
    /// <summary>The viewer shows it now (<c>Document.Retrieved</c>, purpose Display).</summary>
    Display,

    /// <summary>The viewer loads it ahead of time; never counts as viewed (<c>Document.Retrieved</c>, purpose Prefetch).</summary>
    Prefetch,

    /// <summary>Print or save rendered pages: <c>Document.Print</c> (<c>Document.Printed</c>).</summary>
    Print,

    /// <summary>Download the native: <c>Document.DownloadNative</c> (<c>Document.NativeDownloaded</c>).</summary>
    Download,
}

/// <summary>A byte range as the client asked for it (one HTTP range): <c>start-end</c>, <c>start-</c> or <c>-suffix</c>.</summary>
public readonly record struct ContentRangeRequest(long? Start, long? End)
{
    /// <summary>The inclusive range within an object of <paramref name="length"/> bytes, or null when unsatisfiable.</summary>
    public ByteRange? Resolve(long length)
    {
        if (length <= 0)
        {
            return null;
        }

        if (Start is not { } start)
        {
            // Suffix range: the last N bytes.
            return End is { } suffix and > 0 ? new ByteRange(Math.Max(0, length - suffix), Math.Min(suffix, length)) : null;
        }

        if (start < 0 || start >= length || (End is { } e && e < start))
        {
            return null;
        }

        var last = Math.Min(End ?? (length - 1), length - 1);
        return new ByteRange(start, last - start + 1);
    }

    public override string ToString() => $"bytes={Start?.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{End?.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

/// <summary>One gateway request: a document, a rendition (and page) and a purpose.</summary>
/// <param name="PageNumber">1-based page of the active page set, for <see cref="ContentRendition.PageImage"/> and <see cref="ContentRendition.Thumbnail"/>.</param>
/// <param name="Range">Streamed delivery only; ignored when a presigned URL is issued.</param>
/// <param name="TextChunk">
/// <see cref="ContentRendition.Text"/> only: the 0-based chunk of <see cref="TextChunks.ChunkBytes"/> bytes to read
/// (the access service resolves its byte range; <paramref name="Range"/> is then ignored).
/// </param>
public sealed record ContentRequest(
    Guid WorkspaceId,
    Guid DocumentId,
    ContentRendition Rendition,
    ContentPurpose Purpose,
    int? PageNumber = null,
    ContentRangeRequest? Range = null,
    int? TextChunk = null);

/// <summary>
/// Where a document's rendition is stored, read from the PostgreSQL object registry (ADR-011 §2.3). The logical key
/// stays inside the server: it is never returned to a client, logged or audited (audit records the object ID).
/// </summary>
public sealed record ContentLocation(
    Guid ObjectId,
    string LogicalKey,
    byte[] Sha256,
    long Length,
    string ContentType,
    bool Quarantined);

/// <summary>The document facts the gateway needs, or a null <see cref="Location"/> when the rendition does not exist.</summary>
/// <param name="TextTruncated">Only the first part of the text is searchable (Q-29); the stored text is complete.</param>
/// <param name="TextMissing">The load file or processing reported no extracted text.</param>
/// <param name="TextEncodingWarning">The text was decoded with replacement characters at import.</param>
public sealed record DocumentContent(
    string ControlNumber,
    string? FileExtension,
    ContentLocation? Location,
    bool TextTruncated = false,
    bool TextMissing = false,
    bool TextEncodingWarning = false);

/// <summary>
/// Reads document content locations from PostgreSQL inside the workspace's RLS context (implemented by
/// <c>Opportunity.Data</c>). Only the protected-content gateway calls it, after the PDP allowed the request.
/// </summary>
public interface IDocumentContentCatalog
{
    /// <summary>Null when the document does not exist (or is deleted) in the workspace.</summary>
    Task<DocumentContent?> FindAsync(
        Guid workspaceId, Guid documentId, ContentRendition rendition, int? pageNumber, CancellationToken cancellationToken = default);
}

public enum ContentAccessOutcome
{
    /// <summary>Authorized and audited: deliver <see cref="ContentAccessResult.Grant"/>.</summary>
    Granted,

    /// <summary>The PDP did not allow it: 404 or 403 per <see cref="ContentAccessResult.Decision"/>.</summary>
    Denied,

    /// <summary>The document is visible but this rendition (or page) does not exist or may not be served.</summary>
    Unavailable,

    /// <summary>The requested byte range lies outside the object.</summary>
    RangeNotSatisfiable,
}

/// <summary>
/// Permission to deliver exactly one stored object, issued after the PDP check and the durable audit write (ADR-011
/// §5.1). <see cref="Key"/> is a server-side secret in the sense of ADR-015 D12.5: never serialize it.
/// </summary>
/// <param name="AuditEventId">The retrieval's audit event; the viewer's <c>Document.Viewed</c> beacon references it.</param>
/// <param name="Range">The resolved range for a partial streamed response, or null for the whole object.</param>
/// <param name="DownloadFileName">Attachment file name (sanitized control number plus extension), natives only.</param>
public sealed record ContentGrant(
    Guid AuditEventId,
    ObjectKey Key,
    Sha256Digest Sha256,
    long Length,
    string ContentType,
    ObjectDeliveryMode DeliveryMode,
    ByteRange? Range,
    string? DownloadFileName,
    bool BreakGlass);

/// <param name="Document">The catalog facts of a visible document (granted or unavailable); null when denied.</param>
/// <param name="Reason">Why the content is <see cref="ContentAccessOutcome.Unavailable"/> (a <c>DocumentAccessService.Reasons</c> code).</param>
public sealed record ContentAccessResult(
    ContentAccessOutcome Outcome,
    AuthorizationDecision Decision,
    ContentGrant? Grant,
    long? Length = null,
    DocumentContent? Document = null,
    string? Reason = null)
{
    public static ContentAccessResult Denied(AuthorizationDecision decision) => new(ContentAccessOutcome.Denied, decision, null);
}

/// <summary>The outcome of <see cref="IDocumentAccessService.ReadAsync{T}"/>: a value with its audit event, or the denial.</summary>
public sealed record DocumentReadResult<T>(AuthorizationDecision Decision, T? Value, Guid? AuditEventId)
    where T : class;
