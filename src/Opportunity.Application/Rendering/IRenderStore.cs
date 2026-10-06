using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;

namespace Opportunity.Application.Rendering;

/// <summary>
/// PostgreSQL side of the render pipeline (E11-T02, ADR-012 §1): which finished imports still need a render job, which
/// documents of an import need page rasters, the page state the render executor works from, and the chunk commit that
/// registers rasters, Rendered page sets and the active-set change together with the chunk (fence F3).
/// </summary>
public interface IRenderStore
{
    /// <summary>Finished import jobs (any terminal status) with no render request yet, oldest first.</summary>
    Task<IReadOnlyList<ImportToRender>> GetImportsToRenderAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Records that <paramref name="sourceJobId"/> was handled; <paramref name="renderJobId"/> is null when it needed no rendering. Idempotent.</summary>
    Task RecordRenderRequestAsync(Guid workspaceId, Guid sourceJobId, Guid? renderJobId, CancellationToken cancellationToken = default);

    /// <summary>Render jobs still in Created or Preparing, oldest first.</summary>
    Task<IReadOnlyList<RenderJobToPlan>> GetJobsToPlanAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Documents of the import that need rasters, in document-id order: an active Imported page set with page images
    /// lacking a thumbnail, or a renderable native (PDF or page image) without a Ready active page set and without a
    /// page set rendered by <paramref name="renderer"/>. Deleted documents are skipped.
    /// </summary>
    Task<IReadOnlyList<RenderCandidate>> GetCandidatesAsync(
        Guid workspaceId, RenderScope scope, RenderRendererKey renderer, CancellationToken cancellationToken = default);

    /// <summary>The render-relevant state of the documents (missing or deleted ones are omitted).</summary>
    Task<IReadOnlyList<RenderDocumentState>> ReadDocumentsAsync(
        Guid workspaceId, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// One transaction: registers the rendition objects, inserts Rendered page sets with their pages, the rasters
    /// (<c>Review</c>/<c>Thumbnail</c> page images), activates Ready Rendered sets per ADR-012 §1.2 / Q-68, writes the audit
    /// events and commits the chunk (fence F3). Every insert ignores rows that already exist, so a redo is a no-op.
    /// </summary>
    Task<ChunkCommitResult> ApplyChunkAsync(
        ClaimedChunk chunk, RenderChunkWrite write, ChunkCompletion completion, CancellationToken cancellationToken = default);
}

public sealed record ImportToRender(Guid ImportJobId, Guid? ImportBatchId, Guid InitiatedBy, string? CorrelationId);

public sealed record RenderJobToPlan(Guid JobId, JobStatus Status, JsonObject Parameters);

/// <summary>The documents a render job covers: those an import created or changed, and those it gave page images.</summary>
public sealed record RenderScope(Guid ImportJobId, Guid? ImportBatchId)
{
    public JsonObject ToParameters() => new()
    {
        ["importJobId"] = ImportJobId.ToString("D"),
        ["importBatchId"] = ImportBatchId?.ToString("D"),
    };

    public static RenderScope? FromParameters(JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Guid.TryParse(parameters["importJobId"]?.GetValue<string>(), out var job)
            ? new RenderScope(job, Guid.TryParse(parameters["importBatchId"]?.GetValue<string>(), out var batch) ? batch : null)
            : null;
    }
}

/// <summary>What identifies a Rendered page set's producer (ADR-012 §1.1).</summary>
public sealed record RenderRendererKey(string Name, string Version, byte[] SettingsHash);

/// <param name="EstimatedPages">Pages of the active Imported set (0 when unknown, e.g. a PDF native).</param>
/// <param name="SourceBytes">Size of the native, for the chunk's byte bound.</param>
public sealed record RenderCandidate(Guid DocumentId, int EstimatedPages, long SourceBytes);

/// <summary>A registered object the renderer reads (a native or an imported page image).</summary>
public sealed record RenderSourceObject(string LogicalKey, byte[] Sha256, long SizeBytes, string? ContentType);

public sealed record RenderDocumentState
{
    public required Guid DocumentId { get; init; }

    /// <summary>The committed (not quarantined) native, if any.</summary>
    public RenderSourceObject? Native { get; init; }

    public Guid? ActivePageSetId { get; init; }

    public PageSetSource? ActiveSource { get; init; }

    public PageSetStatus? ActiveStatus { get; init; }

    /// <summary>The pages of the active page set when it is an Imported set; empty otherwise.</summary>
    public IReadOnlyList<ImportedPageState> ImportedPages { get; init; } = [];

    /// <summary>Every Rendered page set the document has.</summary>
    public IReadOnlyCollection<Guid> RenderedPageSetIds { get; init; } = [];
}

/// <summary>One page of an Imported set with its original raster and which derived rasters it already has.</summary>
public sealed record ImportedPageState(
    int Ordinal,
    int SourceFrame,
    decimal WidthPt,
    decimal HeightPt,
    RenderSourceObject? Original,
    PageImageFormat? OriginalFormat,
    bool HasReview,
    bool HasThumbnail);

/// <summary>A rendition object written by the chunk (ADR-011 area Rendition), to register in <c>stored_object</c>.</summary>
public sealed record RenditionObject(
    Guid DocumentId, string LogicalKey, byte[] Sha256, long SizeBytes, string ContentType, string KeyId, EncryptionScheme EncryptionScheme);

/// <summary>A Rendered page set (ADR-012 §1.1) with its pages.</summary>
public sealed record NewRenderedPageSet(
    Guid PageSetId,
    Guid DocumentId,
    RenderRendererKey Renderer,
    PageSetStatus Status,
    IReadOnlyList<NewRenderedPage> Pages);

public sealed record NewRenderedPage(int Ordinal, decimal WidthPt, decimal HeightPt, PageColorMode ColorMode, bool ImageMissing);

/// <summary>A derived raster (<c>Review</c> or <c>Thumbnail</c>) of a page of any page set.</summary>
public sealed record NewPageRaster(
    Guid PageSetId, int Ordinal, PageImagePurpose Purpose, string LogicalKey, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format);

public sealed record RenderChunkWrite(
    IReadOnlyList<RenditionObject> Objects,
    IReadOnlyList<NewRenderedPageSet> PageSets,
    IReadOnlyList<NewPageRaster> Rasters,
    IReadOnlyList<AuditEvent> Audit);
