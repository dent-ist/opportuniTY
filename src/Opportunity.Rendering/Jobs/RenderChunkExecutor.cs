using System.Globalization;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Rendering;
using Opportunity.Application.Storage;
using Opportunity.Core.Jobs;
using Opportunity.Core.Pages;
using Opportunity.Rendering.Renderers;

using CoreScheme = Opportunity.Core.Storage.EncryptionScheme;

namespace Opportunity.Rendering.Jobs;

/// <summary>
/// The <see cref="ChunkOperationKind.RenderChunk"/> executor (E11-T02), run by the idempotent chunk consumer for an
/// explicit list of documents. It is state-based (ADR-010 §5.4): for each document it reads what exists and renders only
/// what is missing.
/// <list type="number">
/// <item><b>Imported page sets</b> (OPT): every page with an original image gets a thumbnail, and a review PNG when the
/// original is not browser-displayable (TIFF). The rasters belong to the Imported set's own pages, so a multi-page TIFF
/// is split exactly along the Page rows (frame = <c>SourceFrame</c>) and redactions keep one page identity.</item>
/// <item><b>Natives</b> (PDF or page image) of documents without a Ready active page set become a Rendered page set
/// (review PNG and thumbnail per page, page geometry for redaction coordinates); a Ready one becomes active unless a
/// Ready Imported set is (ADR-012 §1.2, Q-68). A source that cannot be rendered yields a Failed set and a per-item
/// failure; the chunk still commits.</item>
/// </list>
/// Sources are copied to a per-document temp directory through a hashing reader (ADR-011 §2.5) and rendered page by
/// page; each page's files are uploaded and deleted before the next page, so memory and disk stay at about one page.
/// Output keys are deterministic (<see cref="RenderIds"/>), so a retried chunk re-puts identical objects and its inserts
/// find the rows already there. Transient failures (storage, database) propagate and the chunk is retried, then parked
/// as Failed per ADR-010 §7; the lease is extended every <see cref="RenderJobOptions.PagesPerHeartbeat"/> pages.
/// </summary>
public sealed partial class RenderChunkExecutor(
    IRenderStore store,
    IObjectStore objects,
    IRenderer renderer,
    RenderJobOptions options,
    ILogger<RenderChunkExecutor> logger) : IJobChunkExecutor
{
    /// <summary>Actor of worker-side render audit events.</summary>
    public const string RenderWorkerActor = "service:render";

    public ChunkOperationKind OperationKind => ChunkOperationKind.RenderChunk;

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Chunk;
        if (chunk.Membership.Kind != ChunkMembershipKind.ExplicitIds)
        {
            throw new PermanentChunkException("NotARenderChunk", "A render chunk lists its documents explicitly.");
        }

        var ws = context.WorkspaceId;
        var ids = chunk.Membership.DocumentIds!;
        var states = await store.ReadDocumentsAsync(ws, ids, cancellationToken).ConfigureAwait(false);
        var write = new Accumulator();
        long applied = 0;
        var unchanged = (long)(ids.Count - states.Count);
        var root = Directory.CreateDirectory(Path.Combine(options.TempDirectory ?? Path.GetTempPath(), "opp-render-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var state in states)
            {
                // Fence F2 before this document's object writes (also extends the lease).
                await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
                var directory = Directory.CreateDirectory(Path.Combine(root, state.DocumentId.ToString("N"))).FullName;
                try
                {
                    // One render session (with the sandbox: one process) per document, torn down before its files go.
                    var session = renderer.BeginDocument(directory);
                    bool changed;
                    await using (session.ConfigureAwait(false))
                    {
                        changed = await RenderImportedPagesAsync(context, state, session, directory, write, cancellationToken).ConfigureAwait(false);
                        changed |= await RenderNativeAsync(context, state, session, directory, write, cancellationToken).ConfigureAwait(false);
                    }

                    if (changed)
                    {
                        applied++;
                    }
                    else
                    {
                        unchanged++;
                    }
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        var completion = new ChunkCompletion
        {
            ItemsApplied = applied,
            ItemsUnchanged = unchanged,
            ItemResults = write.Failures,
        };

        // Fence F2 again before the PostgreSQL batch; F3 runs inside the store's transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var commit = await store.ApplyChunkAsync(
            chunk, new RenderChunkWrite(write.Objects, write.PageSets, write.Rasters, write.Audit), completion, cancellationToken).ConfigureAwait(false);
        return ChunkExecutionResult.Committed(commit);
    }

    /// <summary>Thumbnails (and review PNGs of TIFF originals) for the pages of the document's active Imported set.</summary>
    private async Task<bool> RenderImportedPagesAsync(
        ChunkExecutionContext context, RenderDocumentState state, IRenderSession session, string directory, Accumulator write, CancellationToken cancellationToken)
    {
        if (state.ActiveSource != PageSetSource.Imported || state.ActivePageSetId is not { } pageSetId)
        {
            return false;
        }

        var needed = state.ImportedPages
            .Where(p => p.Original is not null && (!p.HasThumbnail || (NeedsReview(p) && !p.HasReview)))
            .ToList();
        if (needed.Count == 0)
        {
            return false;
        }

        var rendition = RenderIds.ImportedRendition(pageSetId, renderer.Identity);
        var failedPages = 0;
        string? failure = null;
        var done = 0;
        foreach (var group in needed.GroupBy(p => p.Original!.LogicalKey, StringComparer.Ordinal))
        {
            var pages = group.ToList();
            var input = Path.Combine(directory, "source");
            var output = Directory.CreateDirectory(Path.Combine(directory, "out")).FullName;
            try
            {
                var source = await DownloadAsync(context, state.DocumentId, pages[0].Original!, input, write, cancellationToken).ConfigureAwait(false);
                if (source is not null)
                {
                    failedPages += pages.Count;
                    failure ??= source;
                    continue;
                }

                var frames = pages.Select(p => p.SourceFrame).Distinct().Order().ToList();
                var request = new RenderRequest(input, output, frames, Review: pages.Any(p => NeedsReview(p) && !p.HasReview));
                try
                {
                    await foreach (var page in session.RenderAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        foreach (var target in pages.Where(p => p.SourceFrame == page.Index))
                        {
                            if (page.Error is not null || page.Thumbnail is null)
                            {
                                failedPages++;
                                failure ??= page.Error ?? RenderErrorCodes.PageFailed;
                                continue;
                            }

                            if (NeedsReview(target) && !target.HasReview && page.Review is { } review)
                            {
                                await PutRasterAsync(state.DocumentId, pageSetId, target.Ordinal, PageImagePurpose.Review,
                                    ObjectKeys.Rendition(context.WorkspaceId, state.DocumentId, rendition, PageName(target.Ordinal, "png")), review, write, cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            if (!target.HasThumbnail)
                            {
                                await PutRasterAsync(state.DocumentId, pageSetId, target.Ordinal, PageImagePurpose.Thumbnail,
                                    ObjectKeys.Rendition(context.WorkspaceId, state.DocumentId, rendition, PageName(target.Ordinal, "thumb.png")), page.Thumbnail, write, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }

                        DeleteFiles(page);
                        if (++done % Math.Max(1, options.PagesPerHeartbeat) == 0)
                        {
                            await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (RenderLimitException ex) when (HasAttemptsLeft(context))
                {
                    throw RetryChunk(state.DocumentId, ex);
                }
                catch (RenderException ex)
                {
                    failedPages += pages.Count;
                    failure ??= ex.Code;
                }
            }
            finally
            {
                File.Delete(input);
                Directory.Delete(output, recursive: true);
            }
        }

        if (failure is not null)
        {
            write.Fail(state.DocumentId, failure, string.Create(CultureInfo.InvariantCulture, $"{failedPages} imported page image(s) could not be rendered."));
        }

        return true;
    }

    /// <summary>A Rendered page set from the native when the document has no Ready active page set.</summary>
    private async Task<bool> RenderNativeAsync(
        ChunkExecutionContext context, RenderDocumentState state, IRenderSession session, string directory, Accumulator write, CancellationToken cancellationToken)
    {
        if (state.Native is not { } native
            || !Array.Exists(RenderableTypes, t => string.Equals(t, native.ContentType, StringComparison.Ordinal))
            || state.ActiveStatus == PageSetStatus.Ready)
        {
            return false;
        }

        var identity = renderer.Identity;
        var pageSetId = RenderIds.NativePageSet(state.DocumentId, native.Sha256, identity);
        if (state.RenderedPageSetIds.Contains(pageSetId))
        {
            return false;
        }

        var key = new RenderRendererKey(identity.Name, identity.Version, identity.SettingsHash);
        var input = Path.Combine(directory, "native");
        var output = Directory.CreateDirectory(Path.Combine(directory, "native-out")).FullName;
        try
        {
            if (await DownloadAsync(context, state.DocumentId, native, input, write, cancellationToken).ConfigureAwait(false) is { } sourceError)
            {
                write.Fail(state.DocumentId, sourceError, "The native could not be read for rendering.");
                return true;
            }

            var pages = new List<NewRenderedPage>();
            string? failure = null;
            try
            {
                await foreach (var page in session.RenderAsync(new RenderRequest(input, output), cancellationToken).ConfigureAwait(false))
                {
                    var ordinal = page.Index + 1;
                    var missing = page.Error is not null || page.Review is null;
                    if (!missing)
                    {
                        await PutRasterAsync(state.DocumentId, pageSetId, ordinal, PageImagePurpose.Review,
                            ObjectKeys.Rendition(context.WorkspaceId, state.DocumentId, pageSetId, PageName(ordinal, "png")), page.Review!, write, cancellationToken)
                            .ConfigureAwait(false);
                        if (page.Thumbnail is { } thumbnail)
                        {
                            await PutRasterAsync(state.DocumentId, pageSetId, ordinal, PageImagePurpose.Thumbnail,
                                ObjectKeys.Rendition(context.WorkspaceId, state.DocumentId, pageSetId, PageName(ordinal, "thumb.png")), thumbnail, write, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        failure ??= page.Error ?? RenderErrorCodes.PageFailed;
                    }

                    DeleteFiles(page);
                    pages.Add(new NewRenderedPage(ordinal, page.WidthPt, page.HeightPt, page.ColorMode, missing));
                    if (pages.Count % Math.Max(1, options.PagesPerHeartbeat) == 0)
                    {
                        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (RenderLimitException ex) when (HasAttemptsLeft(context))
            {
                throw RetryChunk(state.DocumentId, ex);
            }
            catch (RenderException ex)
            {
                // On the chunk's last attempt this includes a sandbox limit (time, memory, CPU, crash).
                // The whole source is unusable: a Failed set records it, so a re-run does not try again (a new renderer version will).
                write.PageSets.Add(new NewRenderedPageSet(pageSetId, state.DocumentId, key, PageSetStatus.Failed, []));
                write.RemoveRasters(pageSetId);
                write.Fail(state.DocumentId, ex.Code, "The native could not be rendered.");
                LogRenderFailed(logger, state.DocumentId, ex.Code);
                return true;
            }

            var status = failure is null ? PageSetStatus.Ready : PageSetStatus.Incomplete;
            write.PageSets.Add(new NewRenderedPageSet(pageSetId, state.DocumentId, key, status, pages));
            if (failure is not null)
            {
                write.Fail(state.DocumentId, failure, string.Create(CultureInfo.InvariantCulture,
                    $"{pages.Count(p => p.ImageMissing)} of {pages.Count} page(s) could not be rendered."));
            }

            return true;
        }
        finally
        {
            File.Delete(input);
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>Copies a source object to a local file, verifying its recorded SHA-256; returns an error code instead of throwing for permanent problems.</summary>
    private async Task<string?> DownloadAsync(
        ChunkExecutionContext context, Guid documentId, RenderSourceObject source, string path, Accumulator write, CancellationToken cancellationToken)
    {
        var key = ObjectKey.Parse(source.LogicalKey);
        try
        {
            var stream = await objects.OpenReadAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using var verifying = new VerifyingReadStream(stream, key, Sha256Digest.FromBytes(source.Sha256), source.SizeBytes);
            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                await verifying.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (ObjectNotFoundException)
        {
            return RenderErrorCodes.SourceMissing;
        }
        catch (ObjectIntegrityException ex)
        {
            write.Audit.Add(new AuditEvent
            {
                WorkspaceId = context.WorkspaceId,
                OccurredAt = DateTimeOffset.UtcNow,
                Category = AuditTaxonomy.Integrity.Category,
                Action = "HashMismatch",
                ActorType = AuditActorType.Service,
                ActorId = RenderWorkerActor,
                ActorDisplay = "Render worker",
                OnBehalfOf = context.InitiatedBy,
                ResourceType = "Document",
                ResourceId = documentId.ToString(),
                Outcome = AuditOutcome.Failure,
                ReasonCode = "HashMismatch",
                JobId = context.Chunk.Lease.JobId,
                ChunkSequence = context.Chunk.Sequence,
                CorrelationId = context.Chunk.CorrelationId,
                Details = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Error"] = ex.Message.Length <= 500 ? ex.Message : ex.Message[..500],
                },
            });
            return RenderErrorCodes.SourceIntegrity;
        }
    }

    private async Task PutRasterAsync(
        Guid documentId, Guid pageSetId, int ordinal, PageImagePurpose purpose, ObjectKey key, RasterFile file, Accumulator write,
        CancellationToken cancellationToken)
    {
        var content = File.OpenRead(file.Path);
        RenditionObject registered;
        await using (content.ConfigureAwait(false))
        {
            try
            {
                var put = await objects.PutAsync(key, content, new PutObjectOptions { ContentType = file.ContentType, ExpectedLength = content.Length },
                    cancellationToken).ConfigureAwait(false);
                registered = new RenditionObject(documentId, key.Value, put.Sha256.ToBytes(), put.Length, file.ContentType, put.KeyId, Scheme(put.EncryptionScheme));
            }
            catch (ObjectAlreadyExistsException)
            {
                // An earlier attempt stored this page with other bytes (a library produced different output): keep the
                // stored, write-once object and register what it is.
                registered = await AdoptAsync(documentId, key, file.ContentType, cancellationToken).ConfigureAwait(false);
            }
        }

        write.Objects.Add(registered);
        write.Rasters.Add(new NewPageRaster(pageSetId, ordinal, purpose, key.Value, file.WidthPx, file.HeightPx, file.Dpi, file.Format));
    }

    private async Task<RenditionObject> AdoptAsync(Guid documentId, ObjectKey key, string contentType, CancellationToken cancellationToken)
    {
        var info = await objects.HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw new ObjectNotFoundException(key);
        var sha = info.Sha256;
        if (sha is null)
        {
            var stream = await objects.OpenReadAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                sha = await Sha256Digest.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        return new RenditionObject(documentId, key.Value, sha.Value.ToBytes(), info.Length, contentType, info.KeyId ?? "installation-default", CoreScheme.ProviderSse);
    }

    /// <summary>
    /// A document that hit a sandbox limit retries the chunk while attempts remain (a busy host can cause it); on the
    /// last attempt it fails as a document like any unrenderable source (E11-T03).
    /// </summary>
    private static bool HasAttemptsLeft(ChunkExecutionContext context) => context.Chunk.AttemptCount < context.Chunk.MaxAttempts;

    private TransientChunkException RetryChunk(Guid documentId, RenderLimitException ex)
    {
        LogRenderLimit(logger, documentId, ex.Code);
        return new TransientChunkException(ex.Code, string.Create(CultureInfo.InvariantCulture,
            $"Document {documentId} exceeded a render sandbox limit ({ex.Code}); the chunk is retried."), ex);
    }

    private static readonly string[] RenderableTypes = ["application/pdf", "image/tiff", "image/jpeg", "image/png"];

    /// <summary>Browsers cannot display TIFF (G4 or otherwise), so its pages need a review PNG; JPEG and PNG originals are shown as they are.</summary>
    private static bool NeedsReview(ImportedPageState page) => page.OriginalFormat is not (PageImageFormat.Jpeg or PageImageFormat.Png or PageImageFormat.WebP);

    private static string PageName(int ordinal, string extension) => string.Create(CultureInfo.InvariantCulture, $"p{ordinal:D6}.{extension}");

    private static CoreScheme Scheme(EncryptionScheme scheme) => scheme == EncryptionScheme.Envelope ? CoreScheme.Envelope : CoreScheme.ProviderSse;

    private static void DeleteFiles(RenderedPage page)
    {
        if (page.Review is { } review)
        {
            File.Delete(review.Path);
        }

        if (page.Thumbnail is { } thumbnail)
        {
            File.Delete(thumbnail.Path);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native of document {DocumentId} could not be rendered: {Code}")]
    private static partial void LogRenderFailed(ILogger logger, Guid documentId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} exceeded a render sandbox limit ({Code}); the chunk will be retried")]
    private static partial void LogRenderLimit(ILogger logger, Guid documentId, string code);

    private sealed class Accumulator
    {
        public List<RenditionObject> Objects { get; } = [];

        public List<NewRenderedPageSet> PageSets { get; } = [];

        public List<NewPageRaster> Rasters { get; } = [];

        public List<AuditEvent> Audit { get; } = [];

        public List<JobItemResult> Failures { get; } = [];

        public void Fail(Guid documentId, string code, string detail) =>
            Failures.Add(new JobItemResult(JobItemResultKind.Failed, documentId, null, null, code, detail));

        /// <summary>Drops the rasters of a page set that failed as a whole (its objects stay registered, unreferenced).</summary>
        public void RemoveRasters(Guid pageSetId) => Rasters.RemoveAll(r => r.PageSetId == pageSetId);
    }
}
