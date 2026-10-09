using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.Productions;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;
using Opportunity.Core.Pages;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.Core.Security;
using Opportunity.Import.LoadFiles;
using Opportunity.Production.Exports;
using Opportunity.Production.Productions;

namespace Opportunity.Production.Volumes;

/// <summary>
/// The <see cref="ChunkOperationKind.ProductionVolumeChunk"/> executor (E12-T05), run by the idempotent chunk consumer in
/// the rendering worker for one range of a finalized production's members (production sequence numbers):
/// <list type="number">
/// <item>Re-checks the run's initiator: <c>Production.Create</c> (else the job is cancelled) and every member (Q-15). A
/// production cannot drop a member without breaking its Bates numbering, so a member the initiator may no longer
/// access fails the run instead of being excluded (the Q-15 policy for productions; exports exclude). The delta — each
/// denied member with its precise reason — is audited as <c>Job.Failed</c> before the run fails (E05-T07); the
/// requester sees only the count.</item>
/// <item>Reads only what finalization froze: each member's Bates numbers, designation, page set and redaction version
/// (Q-08), so every run of the production writes the same bytes.</item>
/// <item>Images every Bates page in the member's render session (<see cref="IProducedPageImager"/>, the sandbox):
/// the source page with its redactions burned in and its endorsements stamped, in the format its file type calls for
/// (Q-21); a page that cannot be imaged becomes a "Technical Issue" page; a native member gets a slip sheet and a
/// placeholder member a "Withheld" page, each consuming its one Bates number.</item>
/// <item>Copies natives (never of a redacted document, ADR-012 §5.1) and text (for a redacted document a fixed text,
/// never the original, §5.4), writes the DAT, OPT and produced-page rows as parts, runs the designation QC
/// (<see cref="DesignationQc"/>) and checks that images = OPT rows = the Bates span of every member.</item>
/// <item>Commits outcome rows, file registrations and the chunk (fence F3) in one transaction. Object keys carry the
/// lease token, so a crashed attempt's objects are never registered; the committed attempt's bytes are the same.</item>
/// </list>
/// </summary>
public sealed class ProductionVolumeChunkExecutor(
    IExportStore exports,
    IProductionStore productions,
    IJobRepository jobs,
    IAuthorizationService authorization,
    IFieldAccessFilter fieldAccess,
    IObjectStore store,
    IProducedPageImager imager,
    ProductionVolumeOptions options,
    IAuditEventWriter audit) : IJobChunkExecutor
{
    /// <summary>Reason code of a run failed by the Q-15 re-check (the denied members are in the audit details).</summary>
    public const string AccessChanged = ExportChunkExecutor.AccessChanged;

    private const int DeniedPerAuditEvent = 100;

    public const string PageKindSource = "Page";
    public const string PageKindTechnicalIssue = "TechnicalIssue";
    public const string PageKindSlipSheet = "SlipSheet";
    public const string PageKindPlaceholder = "Placeholder";

    /// <summary>The header of the produced-page records (one row per produced image; boxes in image pixels).</summary>
    public static readonly string[] PageRecordHeader =
        ["PageBates", "ProdBegBates", "Path", "Sha256", "WidthPx", "HeightPx", "PageTopPx", "PageHeightPx", "Kind", "SourcePageSetId", "SourcePage", "Redactions"];

    public ChunkOperationKind OperationKind => ChunkOperationKind.ProductionVolumeChunk;

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Chunk;
        var membership = chunk.Membership;
        var ws = context.WorkspaceId;
        if (membership.Kind != ChunkMembershipKind.SnapshotRange || membership.RangeFrom is not { } from || membership.RangeTo is not { } to)
        {
            throw new PermanentChunkException("NotAVolumeChunk", "A production volume chunk references a range of production sequence numbers.");
        }

        var volume = await exports.GetByJobAsync(ws, chunk.Lease.JobId, cancellationToken).ConfigureAwait(false);
        if (volume?.ProductionId is not { } productionId)
        {
            throw new PermanentChunkException("VolumeMissing", "The chunk's job writes no production volume.");
        }

        var production = await productions.GetAsync(ws, productionId, cancellationToken).ConfigureAwait(false);
        if (production is not { Status: ProductionStatus.Finalized } || production.SnapshotId != membership.SnapshotId)
        {
            throw new PermanentChunkException("ProductionNotFinalized", "The production is no longer finalized (voided?); its volume is not written.");
        }

        var principal = await ExportPrincipal.ResolveAsync(exports, volume, cancellationToken).ConfigureAwait(false);
        if (!(await authorization.AuthorizeAsync(principal, ws, Permission.ProductionCreate, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            await jobs.CancelAsync(ws, chunk.Lease.JobId, volume.CreatedBy, "The initiator no longer holds Production.Create.", cancellationToken)
                .ConfigureAwait(false);
            throw new ChunkFencedException(ChunkFence.JobCancelling);
        }

        var members = await productions.ReadDocumentsAsync(ws, productionId, from - 1, checked((int)(to - from + 1)), cancellationToken).ConfigureAwait(false);
        if (members.Count != membership.KnownCount || members.Any(m => m.Sequence < from || m.Sequence > to))
        {
            throw new PermanentChunkException("ProductionMembersMissing", "The production's members do not cover this chunk.");
        }

        if (members.Any(m => m.PageSetId is null && m.PageCount > 0 || m.DesignationSource is null || m.BegNumber is null))
        {
            throw new PermanentChunkException("ProductionNotFrozenForVolumes",
                "This production was finalized before volume generation froze its pages and redactions; create a new version and finalize it.");
        }

        // Q-15: every member is re-authorized for the initiator against current security state.
        var decisions = await authorization.AuthorizeManyAsync(
            principal, ws, Permission.ProductionCreate, [.. members.Select(m => m.DocumentId)], DenialAudit.Caller, cancellationToken).ConfigureAwait(false);
        var denied = members
            .Select(m => (m.Sequence, m.DocumentId, Reason: decisions.TryGetValue(m.DocumentId, out var d) ? (d.IsAllowed ? null : d.Reason) : AuthorizationReasons.DocumentNotFound))
            .Where(m => m.Reason is not null)
            .ToList();
        if (denied.Count > 0)
        {
            foreach (var part in denied.Chunk(DeniedPerAuditEvent))
            {
                await audit.WriteAsync(ExportChunkExecutor.WorkerEvent(volume, chunk, AuditTaxonomy.Job.Failed, new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ProductionId"] = productionId.ToString(),
                    ["ExportId"] = volume.ExportId.ToString(),
                    ["Policy"] = "FailRun",
                    ["Count"] = part.Length.ToString(CultureInfo.InvariantCulture),
                    ["Documents"] = string.Join(',', part.Select(m => m.DocumentId.ToString("N") + ":" + m.Reason)),
                }) with
                {
                    Category = AuditTaxonomy.Job.Category,
                    Outcome = AuditOutcome.Failure,
                    ReasonCode = AccessChanged,
                }, cancellationToken).ConfigureAwait(false);
            }

            throw new PermanentChunkException(AccessChanged, string.Create(CultureInfo.InvariantCulture,
                $"{denied.Count} document(s) of this production are no longer accessible to the run's initiator; a production cannot leave members out of its Bates numbering."));
        }

        var source = await exports.ReadDocumentsAsync(ws, [.. members.Select(m => m.DocumentId)], cancellationToken).ConfigureAwait(false);
        if (members.FirstOrDefault(m => !source.Documents.ContainsKey(m.DocumentId)) is { } gone)
        {
            throw new PermanentChunkException("DocumentMissing", string.Create(CultureInfo.InvariantCulture,
                $"The member at sequence {gone.Sequence} no longer exists; the volume cannot be written."));
        }

        var restricted = await fieldAccess.RestrictedFieldIdsAsync(ws, principal, source.Catalog, cancellationToken).ConfigureAwait(false);
        var pages = await productions.ReadSourcePagesAsync(ws,
            [.. members.Where(m => m.PageSetId is not null).Select(m => (m.DocumentId, m.PageSetId!.Value))], cancellationToken).ConfigureAwait(false);
        var redactions = new Dictionary<Guid, IReadOnlyList<FrozenRedaction>>();
        foreach (var set in members.Where(m => m.RedactionSetId is not null && m.RedactionCount > 0).GroupBy(m => m.RedactionSetId!.Value))
        {
            foreach (var (id, list) in await productions.ReadFrozenRedactionsAsync(ws, set.Key, [.. set.Select(m => (m.DocumentId, m.RedactionVersion!.Value))],
                cancellationToken).ConfigureAwait(false))
            {
                redactions[id] = list;
            }
        }

        var specification = ProductionSpecificationRules.Deserialize(production.SpecificationJson);
        var settings = ExportSettings.Deserialize(volume.SettingsJson);
        var plan = new VolumePlan(
            production, specification, ProductionSpecificationRules.FormatOf(specification), settings, new ProductionVolumeLayout(settings),
            new ExportValues(source.Catalog, settings.Profile.MultiValue, restricted, specification.LoadFile?.DateFormat,
                TimeZoneInfo.FindSystemTimeZoneById(specification.LoadFile?.TimeZone ?? "UTC")));
        var writer = new ExportChunkFiles(store, ws, volume.ExportId, chunk.Lease.JobId, chunk.Sequence, chunk.Lease.LeaseToken);
        var root = Directory.CreateDirectory(Path.Combine(options.TempDirectory ?? Path.GetTempPath(), "opp-volume-" + Guid.NewGuid().ToString("N"))).FullName;
        var results = new MemberResult[members.Count];
        var fence = new SemaphoreSlim(1, 1);
        try
        {
            // Fence F2 before the object-store writes of this chunk.
            await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, members.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.DocumentConcurrency), CancellationToken = cancellationToken },
                async (i, ct) =>
                {
                    var member = members[i];
                    results[i] = await WriteMemberAsync(context, plan, writer, root, member, source.Documents[member.DocumentId],
                        pages.GetValueOrDefault(member.DocumentId) ?? [], redactions.GetValueOrDefault(member.DocumentId) ?? [], ct).ConfigureAwait(false);
                    await fence.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Fence F2 per document (it also extends the lease).
                        await context.CheckFenceAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        fence.Release();
                    }
                }).ConfigureAwait(false);
        }
        catch (ObjectIntegrityException ex)
        {
            throw new PermanentChunkException("IntegrityHashMismatch", "A stored file no longer matches its recorded SHA-256; the volume cannot include it.", ex);
        }
        catch (ObjectNotFoundException ex)
        {
            throw new PermanentChunkException("StoredObjectMissing", "A registered file is missing from object storage.", ex);
        }
        finally
        {
            fence.Dispose();
            Directory.Delete(root, recursive: true);
        }

        var files = new List<NewExportFile>(results.Sum(r => r.Files.Count) + 3);
        StringBuilder dat = new(), opt = new(), pageRecords = new();
        foreach (var result in results)
        {
            files.AddRange(result.Files);
            dat.Append(result.DatRow);
            opt.Append(result.OptRows);
            pageRecords.Append(result.PageRecords);
        }

        files.Add(await writer.WriteTextAsync("part.dat", LoadFileText.Encode(settings.DatEncoding, dat.ToString()), ExportFileKind.DatPart,
            ExportLayout.PartPath(chunk.Sequence, "dat"), cancellationToken).ConfigureAwait(false));
        files.Add(await writer.WriteTextAsync("part.opt", Encoding.UTF8.GetBytes(opt.ToString()), ExportFileKind.OptPart,
            ExportLayout.PartPath(chunk.Sequence, "opt"), cancellationToken).ConfigureAwait(false));
        files.Add(await writer.WriteTextAsync("part.pages", Encoding.UTF8.GetBytes(pageRecords.ToString()), ExportFileKind.PagePart,
            ExportLayout.PartPath(chunk.Sequence, "pages"), cancellationToken).ConfigureAwait(false));

        // Fence F2 again before the PostgreSQL batch; F3 runs inside the store's transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var commit = await exports.ApplyChunkAsync(
            chunk, new ExportChunkWrite(volume.ExportId, [.. results.Select(r => r.Outcome)], files, []),
            new ChunkCompletion { ItemsApplied = results.Length }, cancellationToken).ConfigureAwait(false);
        return ChunkExecutionResult.Committed(commit);
    }

    private async Task<MemberResult> WriteMemberAsync(
        ChunkExecutionContext context, VolumePlan plan, ExportChunkFiles writer, string root, ProductionDocumentRow member, ExportSourceDocument document,
        IReadOnlyList<ProducedSourcePage> sourcePages, IReadOnlyList<FrozenRedaction> redactions, CancellationToken cancellationToken)
    {
        var prodBeg = member.ProdBegBates!;
        var sequence = member.Sequence;
        var redacted = redactions.Count > 0;
        if (redacted && redactions.Any(r => r.PageSetId != member.PageSetId))
        {
            throw new PermanentChunkException("RedactionsOnAnotherPageSet", string.Create(CultureInfo.InvariantCulture,
                $"{prodBeg}: its redactions were drawn on another page set than the one frozen with the production; review them and produce a new version."));
        }

        if (redacted && member.Output == ProductionOutputKind.Native)
        {
            throw new PermanentChunkException("RedactedNative", string.Create(CultureInfo.InvariantCulture,
                $"{prodBeg}: a redacted document is never produced natively (Q-22); produce it as images in a new version."));
        }

        var labels = EndorsementPlanner.PageLabels(plan.Format, member);
        var files = new List<NewExportFile>();
        var opt = new StringBuilder();
        var records = new StringBuilder();
        var stamped = new List<IReadOnlyList<PageStamp>>(labels.Count);
        string? nativePath = null, textPath = null;
        int natives = 0, texts = 0;
        var nativeMissing = member.Output == ProductionOutputKind.Native && document.Native is null;
        var directory = Directory.CreateDirectory(Path.Combine(root, member.DocumentId.ToString("N"))).FullName;
        try
        {
            var session = imager.BeginDocument(directory);
            await using (session.ConfigureAwait(false))
            {
                var downloads = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < labels.Count; i++)
                {
                    var label = labels[i];
                    var stamps = EndorsementPlanner.ForPage(plan.Specification, plan.Production.Name, label, member.Designation);
                    var output = Directory.CreateDirectory(Path.Combine(directory, "out")).FullName;
                    ProducedPageImage image;
                    string kind;
                    ProducedSourcePage? page = null;
                    IReadOnlyList<FrozenRedaction> burned = [];
                    try
                    {
                        if (member.Output == ProductionOutputKind.Image
                            && sourcePages.FirstOrDefault(p => p.Ordinal == i + 1) is { Image: not null } candidate)
                        {
                            page = candidate;
                            burned = [.. redactions.Where(r => r.Ordinal == candidate.Ordinal)];
                            (image, kind) = await SourcePageAsync(context, plan, session, directory, output, downloads, document, candidate, burned, stamps, label,
                                member, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var (text, generated) = member.Output switch
                            {
                                ProductionOutputKind.Native when !nativeMissing => (plan.Specification.Placeholders?.NativeSlipSheet, PageKindSlipSheet),
                                ProductionOutputKind.Placeholder => (plan.Specification.Placeholders?.Withheld, PageKindPlaceholder),
                                _ => (plan.Specification.Placeholders?.TechnicalIssue, PageKindTechnicalIssue),
                            };
                            image = await GeneratedPageAsync(context, plan, session, output, text, stamps, label, member, cancellationToken).ConfigureAwait(false);
                            kind = generated;
                        }
                    }
                    finally
                    {
                        Directory.Delete(output, recursive: true);
                    }

                    var offset = member.FirstOffset + (plan.Format.Level == BatesNumberingLevel.Page ? i : 0);
                    var path = plan.Layout.ImagePath(offset, label, image.Format);
                    files.Add(await writer.WriteBytesAsync(
                        string.Create(CultureInfo.InvariantCulture, $"{sequence:D10}-p{i + 1:D6}.{ExportLayout.ImageExtension(image.Format)}"),
                        image.Content, ExportFileKind.Image, path, sequence, cancellationToken).ConfigureAwait(false));
                    opt.Append(LoadFileText.OptRow(label, plan.Layout.Volume, plan.Layout.LoadFilePath(path), i == 0, i == 0 ? labels.Count : null));
                    records.Append(LoadFileText.CsvRow(
                        label, prodBeg, path, Convert.ToHexStringLower(SHA256.HashData(image.Content)), Invariant(image.WidthPx), Invariant(image.HeightPx),
                        Invariant(image.PageTopPx), Invariant(image.PageHeightPx), kind, page?.PageSetId.ToString("D") ?? string.Empty,
                        page is null ? string.Empty : Invariant(page.Ordinal), Boxes(burned, image)));
                    stamped.Add(stamps);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        // Images = OPT rows = Bates span (E12-T05 AC 3): at page level one number per image.
        var span = member.EndNumber!.Value - member.BegNumber!.Value + 1;
        if (stamped.Count != labels.Count || (plan.Format.Level == BatesNumberingLevel.Page && stamped.Count != span) || labels.Count == 0)
        {
            throw new PermanentChunkException("PageCountMismatch", string.Create(CultureInfo.InvariantCulture,
                $"{prodBeg}: {stamped.Count} images for a Bates span of {span}."));
        }

        if (member.Output == ProductionOutputKind.Native && document.Native is { } native)
        {
            var path = plan.Layout.NativePath(sequence, prodBeg, ExportLayout.NativeExtension(document.Document.FileExtension, native.ContentType));
            files.Add(await writer.CopyAsync(native, Invariant(sequence, "D10") + "-native", ExportFileKind.Native, path, sequence, null, cancellationToken)
                .ConfigureAwait(false));
            nativePath = plan.Layout.LoadFilePath(path);
            natives = 1;
        }

        if (plan.Settings.IncludeText)
        {
            var path = plan.Layout.TextPath(sequence, prodBeg);
            var utf16 = plan.Settings.TextFileEncoding == LoadFileEncodingKind.Utf16LE;
            string? generated = member.Output == ProductionOutputKind.Placeholder
                ? ProductionVolumeSettings.Fill(plan.Specification.Placeholders?.Withheld ?? ProductionSpecificationRules.DefaultWithheldText, prodBeg,
                    member.Designation, plan.Production.Name)
                : redacted ? ProductionVolumeSettings.RedactedText : null;
            if (generated is not null)
            {
                byte[] bytes = utf16 ? [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(generated)] : Encoding.UTF8.GetBytes(generated);
                files.Add(await writer.WriteBytesAsync(Invariant(sequence, "D10") + "-text", bytes, ExportFileKind.Text, path, sequence, cancellationToken)
                    .ConfigureAwait(false));
                textPath = plan.Layout.LoadFilePath(path);
                texts = 1;
            }
            else if (document.Text is { } text)
            {
                files.Add(await writer.CopyAsync(text, Invariant(sequence, "D10") + "-text", ExportFileKind.Text, path, sequence, utf16 ? "utf-16le" : null,
                    cancellationToken).ConfigureAwait(false));
                textPath = plan.Layout.LoadFilePath(path);
                texts = 1;
            }
        }

        var designation = member.Designation ?? string.Empty;
        var (row, neutralize) = plan.Values.Row(plan.Settings.Columns, document, nativePath, textPath, new ProducedValues(
            prodBeg, member.ProdEndBates!, member.ProdBegAttach!, member.ProdEndAttach!, designation, redacted && member.Output == ProductionOutputKind.Image,
            stamped.Count));

        // E12-T04 AC 1: every page carries the legend, and the load file's value is the same legend.
        var problems = DesignationQc.CheckMember(plan.Specification, member, stamped, designation);
        if (problems.Count > 0)
        {
            throw new PermanentChunkException("DesignationQcFailed", string.Join(" ", problems));
        }

        return new MemberResult(
            new ExportDocumentOutcome(sequence, member.DocumentId, false, document.Document.ControlNumber, null, natives, texts, stamped.Count, stamped.Count),
            files,
            LoadFileText.DatRow(plan.Settings.Profile, row, neutralize),
            opt.ToString(),
            records.ToString());
    }

    private async Task<(ProducedPageImage Image, string Kind)> SourcePageAsync(
        ChunkExecutionContext context, VolumePlan plan, IProducedPageSession session, string directory, string output, Dictionary<string, string> downloads,
        ExportSourceDocument document, ProducedSourcePage page, IReadOnlyList<FrozenRedaction> redactions, IReadOnlyList<PageStamp> stamps, string label,
        ProductionDocumentRow member, CancellationToken cancellationToken)
    {
        var source = page.Image!;
        if (!downloads.TryGetValue(source.ObjectKey, out var input))
        {
            input = Path.Combine(directory, "source-" + downloads.Count.ToString(CultureInfo.InvariantCulture));
            var key = ObjectKey.Parse(source.ObjectKey);
            var stream = await store.OpenReadAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                // Chain of custody (ADR-011 §2.5): the bytes must still hash to the registered SHA-256.
                var verified = new VerifyingReadStream(stream, key, Sha256Digest.FromBytes(source.Sha256), source.SizeBytes);
                var file = File.Create(input);
                await using (file.ConfigureAwait(false))
                {
                    await verified.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            downloads[source.ObjectKey] = input;
        }

        var color = plan.ColorTypes.Contains(ProductionSpecificationRules.NormalizeExtension(document.Document.FileExtension) ?? string.Empty);
        var format = Format(color ? plan.Specification.Images?.ColorFormat : plan.Specification.Images?.Format);
        try
        {
            var image = await session.ImageAsync(Request(plan, input, page.Frame, output, format, stamps,
                [.. redactions.Select(r => new ProducedRedaction(r.Rect, r.Type, r.Label))]), cancellationToken).ConfigureAwait(false);
            return (image, PageKindSource);
        }
        catch (ProducedPageException ex) when (ex.Transient)
        {
            throw Retry(context, ex);
        }
        catch (ProducedPageException)
        {
            // The page cannot be decoded: a "Technical Issue" page takes its Bates number (nothing of the page is produced).
            var text = plan.Specification.Placeholders?.TechnicalIssue;
            return (await GeneratedPageAsync(context, plan, session, output, text, stamps, label, member, cancellationToken).ConfigureAwait(false),
                PageKindTechnicalIssue);
        }
    }

    private static async Task<ProducedPageImage> GeneratedPageAsync(
        ChunkExecutionContext context, VolumePlan plan, IProducedPageSession session, string output, string? template, IReadOnlyList<PageStamp> stamps,
        string label, ProductionDocumentRow member, CancellationToken cancellationToken)
    {
        var dpi = plan.Specification.Images?.Dpi ?? ProductionSpecificationRules.DefaultDpi;
        var text = ProductionVolumeSettings.Fill(template ?? ProductionSpecificationRules.DefaultTechnicalIssueText, label, member.Designation,
            plan.Production.Name);
        List<string> body = [text.Length > 0 ? text : ProductionSpecificationRules.DefaultTechnicalIssueText, label];
        if (!string.IsNullOrEmpty(member.Designation))
        {
            body.Add(member.Designation);
        }

        try
        {
            return await session.ImageAsync(Request(plan, null, 0, output, Format(plan.Specification.Images?.Format), stamps, [], body, dpi,
                (int)(ProductionVolumeSettings.GeneratedWidthInches * dpi), (int)(ProductionVolumeSettings.GeneratedHeightInches * dpi)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProducedPageException ex) when (ex.Transient)
        {
            throw Retry(context, ex);
        }
        catch (ProducedPageException ex)
        {
            throw new PermanentChunkException(ex.Code, string.Create(CultureInfo.InvariantCulture, $"{label}: the generated page could not be written."), ex);
        }
    }

    private static ProducedPageRequest Request(
        VolumePlan plan, string? input, int frame, string output, PageImageFormat format, IReadOnlyList<PageStamp> stamps,
        IReadOnlyList<ProducedRedaction> redactions, IReadOnlyList<string>? body = null, int? dpi = null, int width = 0, int height = 0)
    {
        var endorsements = plan.Specification.Endorsements;
        return new ProducedPageRequest(
            input,
            frame,
            output,
            format,
            [.. stamps.Select(s => new ProducedStamp(s.Position, s.Text))],
            endorsements?.FontSize ?? 10,
            endorsements?.Margin ?? ProductionSpecificationRules.DefaultMargin,
            endorsements?.ExpandCanvas ?? true,
            redactions,
            body,
            dpi,
            width,
            height);
    }

    /// <summary>A sandbox limit retries the chunk while attempts remain; on the last attempt the run fails (never a silent technical issue).</summary>
    private static Exception Retry(ChunkExecutionContext context, ProducedPageException ex) =>
        context.Chunk.AttemptCount < context.Chunk.MaxAttempts
            ? new TransientChunkException(ex.Code, "A page hit a render sandbox limit; the chunk is retried.", ex)
            : new PermanentChunkException(ex.Code, "A page kept hitting a render sandbox limit; the volume cannot be written.", ex);

    private static PageImageFormat Format(ProductionImageFormatResource? format) =>
        format == ProductionImageFormatResource.Jpeg ? PageImageFormat.Jpeg : PageImageFormat.TiffG4;

    /// <summary>The burned boxes in produced-image pixels (<c>x y w h type</c>, separated by <c>;</c>).</summary>
    private static string Boxes(IReadOnlyList<FrozenRedaction> redactions, ProducedPageImage image) =>
        string.Join(';', redactions.Select(r =>
        {
            var box = RedactionGeometry.BurnedPixels(r.Rect, image.WidthPx, image.PageHeightPx);
            return string.Create(CultureInfo.InvariantCulture, $"{box.X} {box.Y + image.PageTopPx} {box.Width} {box.Height} {r.Type}");
        }));

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(long value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private sealed record VolumePlan(
        ProductionRecord Production, ProductionSpecification Specification, BatesFormat Format, ExportSettings Settings, ProductionVolumeLayout Layout,
        ExportValues Values)
    {
        public HashSet<string> ColorTypes { get; } = [.. Specification.Images?.ColorFileTypes ?? []];
    }

    private sealed record MemberResult(ExportDocumentOutcome Outcome, List<NewExportFile> Files, string DatRow, string OptRows, string PageRecords);
}
