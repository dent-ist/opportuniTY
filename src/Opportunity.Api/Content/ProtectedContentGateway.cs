using System.ComponentModel.DataAnnotations;
using System.Globalization;

using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Opportunity.Api.Conventions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Content;
using Opportunity.Application.Search.HighlightSets;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Security.Authorization;
using Opportunity.Security.Http;

namespace Opportunity.Api.Content;

/// <summary>How natives reach the browser when the provider can sign URLs (ADR-015 D12.3).</summary>
public enum NativeDelivery
{
    /// <summary>The API streams every native (default; always for the filesystem provider).</summary>
    Stream,

    /// <summary>
    /// A single-object, GET-only presigned URL behind a <c>303</c>. Enable only when the object store is reachable by
    /// browsers over TLS (D12.3).
    /// </summary>
    Presign,
}

/// <summary>Configuration section <c>ProtectedContent</c>.</summary>
public sealed class ProtectedContentOptions
{
    public const string SectionName = "ProtectedContent";

    public NativeDelivery NativeDelivery { get; set; } = NativeDelivery.Stream;

    /// <summary>Lifetime of a presigned native URL: the accepted revocation window (ADR-015 D12.3: 60 s default, 30–300 s).</summary>
    [Range(typeof(TimeSpan), "00:00:30", "00:05:00")]
    public TimeSpan PresignTtl { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Marks the endpoints of the protected-content gateway (architecture test: only these return stored content).</summary>
public sealed class ProtectedContentEndpointMetadata
{
    public static ProtectedContentEndpointMetadata Instance { get; } = new();

    private ProtectedContentEndpointMetadata()
    {
    }
}

/// <summary>
/// The protected-content gateway (E05-T04, ADR-015 D5.4 PEP-3 and D12, ADR-011 §5): the only code in the API that
/// reads object storage or signs URLs. Each request is decided and audited by <see cref="IDocumentAccessService"/>
/// (PDP against PostgreSQL → durable <c>Document.*</c> event) before a byte or URL leaves. Object keys never leave the
/// server; presigned URLs appear only in the <c>Location</c> header of a <c>no-store</c> 303 and are never logged,
/// traced or audited.
/// </summary>
public sealed partial class ProtectedContentGateway(
    IDocumentAccessService access,
    IServiceProvider services,
    IOptions<ProtectedContentOptions> options,
    ILogger<ProtectedContentGateway> logger)
{
    /// <summary>The audit event ID of a delivered retrieval, for the viewer's <c>Document.Viewed</c> beacon.</summary>
    public const string RetrievalIdHeader = "X-Opportunity-Retrieval-Id";

    /// <summary>Detail of the 404 for a document that does not exist, is hidden (class, wall) or whose ID is malformed.</summary>
    public const string DocumentNotFoundDetail = "The document does not exist.";

    public static IResult DocumentNotFound() => Problems.NotFound(DocumentNotFoundDetail);

    public async Task<IResult> DeliverAsync(HttpContext context, WorkspaceAccess workspace, ContentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        var store = services.GetService<IObjectStore>();
        if (store is null)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Document content storage is not configured.");
        }

        var signer = services.GetService<IObjectUrlSigner>();
        var mode = request.Rendition == ContentRendition.Native && options.Value.NativeDelivery == NativeDelivery.Presign
            && signer?.DeliveryMode == ObjectDeliveryMode.Presign
                ? ObjectDeliveryMode.Presign
                : ObjectDeliveryMode.Stream;

        var result = await access.OpenAsync(workspace.Principal, request, mode, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ContentAccessOutcome.Denied:
                return result.Decision.Outcome == AuthorizationOutcome.NotFound ? DocumentNotFound() : AuthorizationResults.Problem(result.Decision);
            case ContentAccessOutcome.Unavailable:
                return Problems.Create(StatusCodes.Status404NotFound, ProblemCodes.ContentUnavailable, "This rendition is not available for the document.");
            case ContentAccessOutcome.RangeNotSatisfiable:
                context.Response.Headers.ContentRange = $"bytes */{result.Length?.ToString(CultureInfo.InvariantCulture)}";
                return Problems.Create(StatusCodes.Status416RangeNotSatisfiable, ProblemCodes.RangeNotSatisfiable, "The requested range is outside the content.");
        }

        var grant = result.Grant!;
        if (grant.DeliveryMode == ObjectDeliveryMode.Presign)
        {
            var url = await signer!.PresignGetAsync(
                grant.Key, new PresignGetOptions(grant.DownloadFileName ?? "download", options.Value.PresignTtl), cancellationToken).ConfigureAwait(false);
            return new PresignedRedirectResult(url, grant.AuditEventId);
        }

        return new StreamedContentResult(store, grant, request.Rendition == ContentRendition.Native, logger);
    }

    /// <summary>
    /// One fixed-size chunk of the extracted text as JSON (E11-T01): the access service resolves the chunk's byte range
    /// and audits the retrieval (rendition Text, the chunk and range in the details) before the range is read. A
    /// document without stored text answers chunk 0 with <c>missing: true</c>.
    /// </summary>
    public async Task<IResult> DeliverTextChunkAsync(HttpContext context, WorkspaceAccess workspace, ContentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        var chunk = request.TextChunk ?? throw new ArgumentException("A text chunk request names its chunk.", nameof(request));
        var store = services.GetService<IObjectStore>();
        if (store is null)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Document content storage is not configured.");
        }

        var result = await access.OpenAsync(workspace.Principal, request, ObjectDeliveryMode.Stream, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ContentAccessOutcome.Denied:
                return result.Decision.Outcome == AuthorizationOutcome.NotFound ? DocumentNotFound() : AuthorizationResults.Problem(result.Decision);
            case ContentAccessOutcome.Unavailable
                when chunk == 0 && result.Reason == DocumentAccessService.Reasons.RenditionUnavailable && result.Document is { } document:
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new DocumentTextChunkResource(
                    0, 0, TextChunks.ChunkBytes, 0, 0, 0, IsLast: true, string.Empty, document.TextTruncated, Missing: true, document.TextEncodingWarning));
            case ContentAccessOutcome.Unavailable:
                return Problems.Create(StatusCodes.Status404NotFound, ProblemCodes.ContentUnavailable, "This rendition is not available for the document.");
            case ContentAccessOutcome.RangeNotSatisfiable:
                return Problems.Create(StatusCodes.Status404NotFound, ProblemCodes.ContentUnavailable, "The text has no chunk with this index.");
        }

        var grant = result.Grant!;
        var flags = result.Document!;
        var bytes = Array.Empty<byte>();
        long offset = 0;
        if (grant.Range is { } range)
        {
            offset = range.Offset;
            bytes = new byte[range.Length!.Value];
            var source = await store.OpenReadAsync(grant.Key, range, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }

        var decoded = TextChunks.Decode(bytes, offset);
        var count = TextChunks.Count(grant.Length);
        SetCommonHeaders(context.Response, grant.AuditEventId);
        return TypedResults.Ok(new DocumentTextChunkResource(
            chunk,
            count,
            TextChunks.ChunkBytes,
            decoded.ByteStart,
            decoded.ByteEnd,
            grant.Length,
            IsLast: chunk == count - 1,
            decoded.Text,
            flags.TextTruncated,
            Missing: false,
            flags.TextEncodingWarning));
    }

    /// <summary>
    /// Term hits over the extracted text (E16-T12): the access service decides <c>Document.View</c> and audits the
    /// retrieval (rendition Text, the first chunk of the page, use <c>termHits</c>) before any byte is read; then a page
    /// of chunks is scanned in process (<see cref="TextHitPages"/>). Only offsets leave the server, never text. A
    /// document without stored text answers chunk 0 with <c>missing: true</c> and no hits.
    /// </summary>
    public async Task<IResult> DeliverTextHitsAsync(
        HttpContext context, WorkspaceAccess workspace, ContentRequest request, TermHitUnitSet units, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(units);
        var from = request.TextChunk ?? throw new ArgumentException("A term-hit request names its first chunk.", nameof(request));
        var store = services.GetService<IObjectStore>();
        if (store is null)
        {
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ProblemCodes.ServiceUnavailable, "Document content storage is not configured.");
        }

        var result = await access.OpenAsync(workspace.Principal, request, ObjectDeliveryMode.Stream, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ContentAccessOutcome.Denied:
                return result.Decision.Outcome == AuthorizationOutcome.NotFound ? DocumentNotFound() : AuthorizationResults.Problem(result.Decision);
            case ContentAccessOutcome.Unavailable
                when from == 0 && result.Reason == DocumentAccessService.Reasons.RenditionUnavailable && result.Document is { } document:
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new DocumentTextHitsResource(
                    request.DocumentId, 0, 0, 0, null, document.TextTruncated, Missing: true, ToUnits(units, []), []));
            case ContentAccessOutcome.Unavailable:
                return Problems.Create(StatusCodes.Status404NotFound, ProblemCodes.ContentUnavailable, "This rendition is not available for the document.");
            case ContentAccessOutcome.RangeNotSatisfiable:
                return Problems.Create(StatusCodes.Status404NotFound, ProblemCodes.ContentUnavailable, "The text has no chunk with this index.");
        }

        var grant = result.Grant!;
        var page = await TextHitPages.ScanAsync(store, grant.Key, grant.Length, from, [.. units.Units.Select(u => u.Unit)], cancellationToken)
            .ConfigureAwait(false);
        SetCommonHeaders(context.Response, grant.AuditEventId);
        return TypedResults.Ok(new DocumentTextHitsResource(
            request.DocumentId,
            page.ChunkCount,
            page.FromChunk,
            page.ToChunk,
            page.ToChunk < page.ChunkCount ? page.ToChunk : null,
            result.Document!.TextTruncated,
            Missing: false,
            ToUnits(units, page.Counts),
            page.Hits));
    }

    private static List<TermHitUnitResource> ToUnits(TermHitUnitSet units, long[] counts) =>
    [
        .. units.Units.Select((u, i) => new TermHitUnitResource(
            i,
            u.Source,
            u.HighlightSetId,
            u.TermId,
            u.Unit.Label,
            (TermHitKind)(int)u.Unit.Kind,
            u.Color,
            i < counts.Length ? counts[i] : 0)),
    ];

    /// <summary>
    /// A JSON view of a document read from PostgreSQL (metadata, page list) under the gateway's contract: PDP decision,
    /// durable <c>Document.Retrieved</c> event, then the response with the retrieval ID and <c>no-store</c>.
    /// </summary>
    public async Task<IResult> ReadAsync<T>(
        HttpContext context,
        WorkspaceAccess workspace,
        ContentRequest request,
        Func<CancellationToken, Task<T?>> load,
        Func<T, IResult> respond,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(respond);
        var result = await access.ReadAsync(workspace.Principal, request, load, cancellationToken).ConfigureAwait(false);
        if (result.Value is not { } value)
        {
            return result.Decision.Outcome == AuthorizationOutcome.NotFound || result.Decision.IsAllowed
                ? DocumentNotFound()
                : AuthorizationResults.Problem(result.Decision);
        }

        SetCommonHeaders(context.Response, result.AuditEventId!.Value);
        return respond(value);
    }

    /// <summary>Parses a single <c>bytes</c> range; anything else (multiple ranges, other units, garbage) means the whole object.</summary>
    public static ContentRangeRequest? ParseRange(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.ContainsKey(HeaderNames.Range))
        {
            return null;
        }

        var header = request.GetTypedHeaders().Range;
        if (header is null || !string.Equals(header.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) || header.Ranges.Count != 1)
        {
            return null;
        }

        var range = header.Ranges.First();
        return new ContentRangeRequest(range.From, range.To);
    }

    /// <summary>
    /// Streams one granted object as an attachment with the D12.2 headers and single-range support, a full read
    /// re-verified against the registry hash. For grants issued outside <see cref="IDocumentAccessService"/> after their
    /// own PDP decision and durable audit event (export packages).
    /// </summary>
    internal static IResult StreamAttachment(IObjectStore store, ContentGrant grant, ILogger logger) =>
        new StreamedContentResult(store, grant, attachment: true, logger);

    private static void SetCommonHeaders(HttpResponse response, Guid auditEventId)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers[RetrievalIdHeader] = auditEventId.ToString();
    }

    /// <summary>Streams one object with the D12.2 headers; a full read is re-verified against the registry hash.</summary>
    private sealed class StreamedContentResult(IObjectStore store, ContentGrant grant, bool attachment, ILogger logger) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var cancellationToken = httpContext.RequestAborted;
            var source = await store.OpenReadAsync(grant.Key, grant.Range, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var response = httpContext.Response;
                SetCommonHeaders(response, grant.AuditEventId);
                response.ContentType = grant.ContentType;
                response.Headers.ContentSecurityPolicy = ApiContentSecurityPolicies.ProtectedContent;
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.AcceptRanges = "bytes";
                if (attachment)
                {
                    response.Headers.ContentDisposition = ContentDispositionHeader.Attachment(grant.DownloadFileName);
                }

                Stream body = source;
                if (grant.Range is { } range)
                {
                    response.StatusCode = StatusCodes.Status206PartialContent;
                    response.ContentLength = range.Length;
                    response.Headers.ContentRange =
                        $"bytes {range.Offset.ToString(CultureInfo.InvariantCulture)}-{range.LastByte!.Value.ToString(CultureInfo.InvariantCulture)}/{grant.Length.ToString(CultureInfo.InvariantCulture)}";
                }
                else
                {
                    response.StatusCode = StatusCodes.Status200OK;
                    response.ContentLength = grant.Length;
                    body = new VerifyingReadStream(source, grant.Key, grant.Sha256, grant.Length);
                }

                try
                {
                    await CopyHoldingBackLastBlockAsync(body, response.Body, cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectIntegrityException)
                {
                    // The bytes no longer match the registry (chain of custody): cut the response so the client cannot
                    // mistake a truncated or altered stream for the evidence. The object ID is in the grant's audit event.
                    LogIntegrityFailure(logger, grant.AuditEventId);
                    httpContext.Abort();
                }
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> but writes each block only once the next read succeeded, so the final block is
    /// sent only after the verifying stream has checked the hash at end of stream: altered content is never delivered
    /// complete.
    /// </summary>
    private static async Task CopyHoldingBackLastBlockAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        const int BlockSize = 81_920;
        var pending = new byte[BlockSize];
        var next = new byte[BlockSize];
        var pendingCount = await source.ReadAsync(pending, cancellationToken).ConfigureAwait(false);
        while (pendingCount > 0)
        {
            var nextCount = await source.ReadAsync(next, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(pending.AsMemory(0, pendingCount), cancellationToken).ConfigureAwait(false);
            (pending, next) = (next, pending);
            pendingCount = nextCount;
        }
    }

    private sealed class PresignedRedirectResult(PresignedObjectUrl url, Guid auditEventId) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            SetCommonHeaders(response, auditEventId);
            response.StatusCode = StatusCodes.Status303SeeOther;
            response.Headers.Location = url.Url.AbsoluteUri;
            return Task.CompletedTask;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Stored content failed integrity verification during delivery (retrieval {RetrievalId}); response aborted.")]
    private static partial void LogIntegrityFailure(ILogger logger, Guid retrievalId);
}
