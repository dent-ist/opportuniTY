using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Authorization;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
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
/// access fails the run instead of being excluded.</item>
/// <item>Reads only what finalization froze: each member's Bates numbers, designation, page set and redaction version
/// (Q-08), so every run of the production writes the same bytes.</item>
/// <item>Images every Bates page in the member's render session (<see cref="IProducedPageImager"/>, the sandbox):
/// the source page with its redactions burned in and its endorsements stamped, in the format its file type calls for
/// (Q-21); a page that cannot be imaged becomes a "Technical Issue" page; a native member gets a slip sheet and a
/// placeholder member a "Withheld" page, each consuming its one Bates number.</item>
/// <item>Copies natives (never of a redacted document, ADR-012 §5.1) and text (for a redacted document a fixed text,
/// never the original, §5.4), writes the DAT, OPT and produced-page rows as parts, runs the designation QC
/// (<see cref="DesignationQc"/>) and checks that images = OPT rows = the Bates span of every member.</item>
/// <item>Verifies the burn-in of every redacted and withheld member (E12-T06, <see cref="BurnInVerification"/>): each
/// page that carries redactions is read back from storage and checked pixel by pixel in the render session; text and
/// native are compared with what may be shipped. The rows go to the chunk's verification part; the coordinator turns
/// them into the QC report and refuses to complete a run with a finding.</item>
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
    ProductionVolumeOptions options
#if OPPORTUNITY_FAILPOINTS
    , IFaultInjector? faults = null
#endif
    ) : IJobChunkExecutor
{
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
        var denied = members.Count(m => !decisions.TryGetValue(m.DocumentId, out var d) || !d.IsAllowed);
        if (denied > 0)
        {
            throw new PermanentChunkException("AccessChanged", string.Create(CultureInfo.InvariantCulture,
                $"{denied} document(s) of this production are no longer accessible to the run's initiator; a production cannot leave members out of its Bates numbering."));
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

        var files = new List<NewExportFile>(results.Sum(r => r.Files.Count) + 4);
        StringBuilder dat = new(), opt = new(), pageRecords = new(), checks = new();
        foreach (var result in results)
        {
            files.AddRange(result.Files);
            dat.Append(result.DatRow);
            opt.Append(result.OptRows);
            pageRecords.Append(result.PageRecords);
            checks.Append(result.Checks);
        }

        files.Add(await writer.WriteTextAsync("part.dat", LoadFileText.Encode(settings.DatEncoding, dat.ToString()), ExportFileKind.DatPart,
            ExportLayout.PartPath(chunk.Sequence, "dat"), cancellationToken).ConfigureAwait(false));
        files.Add(await writer.WriteTextAsync("part.opt", Encoding.UTF8.GetBytes(opt.ToString()), ExportFileKind.OptPart,
            ExportLayout.PartPath(chunk.Sequence, "opt"), cancellationToken).ConfigureAwait(false));
        files.Add(await writer.WriteTextAsync("part.pages", Encoding.UTF8.GetBytes(pageRecords.ToString()), ExportFileKind.PagePart,
            ExportLayout.PartPath(chunk.Sequence, "pages"), cancellationToken).ConfigureAwait(false));
        files.Add(await writer.WriteTextAsync("part.burnin", Encoding.UTF8.GetBytes(checks.ToString()), ExportFileKind.VerificationPart,
            ExportLayout.PartPath(chunk.Sequence, "burnin"), cancellationToken).ConfigureAwait(false));

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
        var checks = new StringBuilder();
        var producedPages = new List<ProducedPage>(labels.Count);
        NewExportFile? textFile = null, nativeFile = null;
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
                            (image, kind) = await SourcePageAsync(context, plan, session, directory, output, downloads, document, candidate,
                                Armed(FaultFlagSkipBurn) ? [] : burned, stamps, label, member, cancellationToken).ConfigureAwait(false);
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
                    var imageFile = await writer.WriteBytesAsync(
                        string.Create(CultureInfo.InvariantCulture, $"{sequence:D10}-p{i + 1:D6}.{ExportLayout.ImageExtension(image.Format)}"),
                        image.Content, ExportFileKind.Image, path, sequence, cancellationToken).ConfigureAwait(false);
                    files.Add(imageFile);
                    producedPages.Add(new ProducedPage(i, label, kind, imageFile, image, page));
                    opt.Append(LoadFileText.OptRow(label, plan.Layout.Volume, plan.Layout.LoadFilePath(path), i == 0, i == 0 ? labels.Count : null));
                    records.Append(LoadFileText.CsvRow(
                        label, prodBeg, path, Convert.ToHexStringLower(SHA256.HashData(image.Content)), Invariant(image.WidthPx), Invariant(image.HeightPx),
                        Invariant(image.PageTopPx), Invariant(image.PageHeightPx), kind, page?.PageSetId.ToString("D") ?? string.Empty,
                        page is null ? string.Empty : Invariant(page.Ordinal), Boxes(burned, image)));
                    stamped.Add(stamps);
                }

                if (redacted)
                {
                    await VerifyPagesAsync(context, session, directory, downloads, member, labels, producedPages, redactions, checks, cancellationToken)
                        .ConfigureAwait(false);
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

        if ((member.Output == ProductionOutputKind.Native || (redacted && Armed(FaultFlagShipNative))) && document.Native is { } native)
        {
            var path = plan.Layout.NativePath(sequence, prodBeg, ExportLayout.NativeExtension(document.Document.FileExtension, native.ContentType));
            nativeFile = await writer.CopyAsync(native, Invariant(sequence, "D10") + "-native", ExportFileKind.Native, path, sequence, null, cancellationToken)
                .ConfigureAwait(false);
            files.Add(nativeFile);
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
            byte[]? bytes = generated is null ? null : utf16 ? [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(generated)] : Encoding.UTF8.GetBytes(generated);
            if (bytes is not null && !(redacted && document.Text is not null && Armed(FaultFlagShipText)))
            {
                textFile = await writer.WriteBytesAsync(Invariant(sequence, "D10") + "-text", bytes, ExportFileKind.Text, path, sequence, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (document.Text is { } text)
            {
                textFile = await writer.CopyAsync(text, Invariant(sequence, "D10") + "-text", ExportFileKind.Text, path, sequence, utf16 ? "utf-16le" : null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (textFile is not null)
            {
                files.Add(textFile);
                textPath = plan.Layout.LoadFilePath(path);
                texts = 1;
            }

            if (bytes is not null)
            {
                // E12-T06: a redacted or withheld member ships the replacement text, never its own.
                checks.Append(BurnInVerification.Row(prodBeg, null, BurnInVerification.CheckText, 0, BurnInVerification.Text(textFile, bytes, document.Text)));
            }
        }

        if (redacted || member.Output == ProductionOutputKind.Placeholder)
        {
            // E12-T06 / Q-22: no native of a redacted or withheld member (no native-redaction method can be recorded yet).
            checks.Append(BurnInVerification.Row(prodBeg, null, BurnInVerification.CheckNative, 0, BurnInVerification.Native(nativeFile)));
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
            records.ToString(),
            checks.ToString());
    }

    /// <summary>
    /// E12-T06: every page of a redacted member that carries redactions is read back from storage (its bytes must still
    /// hash to the registered SHA-256) and verified in the member's render session against its source page and its frozen
    /// redactions; one report row per page. A redacted page that became a "Technical Issue" page ships nothing of the page
    /// and passes; a redaction on a page the member does not have is a finding.
    /// </summary>
    private async Task VerifyPagesAsync(
        ChunkExecutionContext context, IProducedPageSession session, string directory, Dictionary<string, string> downloads, ProductionDocumentRow member,
        IReadOnlyList<string> labels, List<ProducedPage> produced, IReadOnlyList<FrozenRedaction> redactions, StringBuilder checks,
        CancellationToken cancellationToken)
    {
        var prodBeg = member.ProdBegBates!;
        foreach (var ordinal in redactions.Select(r => r.Ordinal).Distinct().Order())
        {
            var expected = redactions.Where(r => r.Ordinal == ordinal).ToList();
            var page = produced.FirstOrDefault(p => p.Index == ordinal - 1);
            if (page is null)
            {
                checks.Append(BurnInVerification.Row(prodBeg, ordinal - 1 < labels.Count ? labels[ordinal - 1] : null, BurnInVerification.CheckImage,
                    expected.Count, [new BurnInFinding(BurnInCodes.PageMissing)]));
                continue;
            }

            if (page.Kind == PageKindTechnicalIssue)
            {
                checks.Append(BurnInVerification.Row(prodBeg, page.Label, BurnInVerification.CheckImage, 0, []));
                continue;
            }

            if (page.Source is not { Image: { } sourceImage } source || page.Kind != PageKindSource
                || !downloads.TryGetValue(sourceImage.ObjectKey, out var input) || source.Ordinal != ordinal)
            {
                checks.Append(BurnInVerification.Row(prodBeg, page.Label, BurnInVerification.CheckImage, expected.Count,
                    [new BurnInFinding(BurnInCodes.PageMissing)]));
                continue;
            }

            var local = Path.Combine(directory, "verify-" + Invariant(page.Index) + "." + ExportLayout.ImageExtension(page.Image.Format));
            IReadOnlyList<BurnInFinding> findings;
            var boxes = 0;
            try
            {
                var key = ObjectKey.Parse(page.File.ObjectKey);
                var stream = await store.OpenReadAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var verified = new VerifyingReadStream(stream, key, Sha256Digest.FromBytes(page.File.Sha256), page.File.SizeBytes);
                    var file = File.Create(local);
                    await using (file.ConfigureAwait(false))
                    {
                        await verified.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                    }
                }

                var verdict = await session.VerifyAsync(new ProducedPageVerification(local, page.Image.Format, input, source.Frame, page.Image.PageTopPx,
                    [.. expected.Select(r => new VerifiedRedaction(r.Rect, r.Type))]), cancellationToken).ConfigureAwait(false);
                boxes = verdict.BoxesChecked;
                findings = verdict.Passed && (verdict.WidthPx != page.Image.WidthPx || verdict.PageHeightPx != page.Image.PageHeightPx)
                    ? [new BurnInFinding(BurnInCodes.PageGeometry)]
                    : verdict.Findings;
            }
            catch (ProducedPageException ex) when (ex.Transient)
            {
                throw Retry(context, ex);
            }
            catch (ProducedPageException)
            {
                findings = [new BurnInFinding(BurnInCodes.NotVerifiable)];
            }
            finally
            {
                File.Delete(local);
            }

            checks.Append(BurnInVerification.Row(prodBeg, page.Label, BurnInVerification.CheckImage, boxes, findings));
        }
    }

#if OPPORTUNITY_FAILPOINTS
    private bool Armed(string flag) => faults?.IsArmed(flag) == true;

    private const string FaultFlagSkipBurn = FaultFlags.VolumeSkipsBurn;
    private const string FaultFlagShipText = FaultFlags.VolumeShipsOriginalText;
    private const string FaultFlagShipNative = FaultFlags.VolumeShipsRedactedNative;
#else
    private static bool Armed(string flag) => flag.Length < 0;

    private const string FaultFlagSkipBurn = "";
    private const string FaultFlagShipText = "";
    private const string FaultFlagShipNative = "";
#endif

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

    private sealed record MemberResult(ExportDocumentOutcome Outcome, List<NewExportFile> Files, string DatRow, string OptRows, string PageRecords, string Checks);

    /// <summary>A produced image of a member: its index, Bates label, kind, registered file, imager report and source page.</summary>
    private sealed record ProducedPage(int Index, string Label, string Kind, NewExportFile File, ProducedPageImage Image, ProducedSourcePage? Source);
}
