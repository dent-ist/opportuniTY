using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Exports;
using Opportunity.Application.Jobs;
using Opportunity.Application.Productions;
using Opportunity.Application.Storage;
using Opportunity.Core.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Production.Exports;
using Opportunity.Production.Productions;

namespace Opportunity.Production.Volumes;

public enum ProductionVolumeStep
{
    None,

    /// <summary>The run's chunks were planned from the production's members and the job started.</summary>
    Started,

    /// <summary>The DAT, OPT, verification file and manifest were written and the run completed.</summary>
    Completed,

    /// <summary>The burn-in verification found a leak (E12-T06): the run failed with its QC report and is never delivered.</summary>
    Rejected,

    /// <summary>The run ended as Failed or Cancelled with its job.</summary>
    Ended,
}

/// <summary>
/// Plans and finalizes production volume runs (E12-T05) in the rendering worker. Planning splits the finalized
/// production's members (production order) into ranges of at most <see cref="ProductionVolumeOptions.DocumentsPerChunk"/>
/// documents and <see cref="ProductionVolumeOptions.PagesPerChunk"/> pages. Once every chunk committed, finalization
/// assembles the DAT (byte-order mark per the encoding and header, then every chunk's rows in order), the OPT, the
/// produced-page verification file (kept with the run for E12-T06, never delivered) and MANIFEST.json/.csv (every
/// delivered file with its size and SHA-256; no run identifiers or times), then completes the run with
/// <c>Production.VolumeCompleted</c>. Before any of that it assembles the chunks' burn-in verification rows into the QC
/// report (E12-T06, <see cref="BurnInVerification"/>): a run with a failed check ends as Failed with the report and
/// <c>Production.VerificationFailed</c>, without load files or manifest, so it can never be delivered; a passed
/// verification is recorded in the manifest. Both steps are restartable under a claim and depend only on frozen state and
/// committed parts, so any worker, and any later run of the same production, writes the same bytes. A run with a
/// failed chunk fails as a whole: a production volume is never delivered incomplete.
/// </summary>
public sealed partial class ProductionVolumeCoordinator(
    IExportStore exports,
    IProductionStore productions,
    IJobRepository jobs,
    IObjectStore store,
    IProducedPageImager imager,
    ProductionVolumeOptions options,
    ILogger<ProductionVolumeCoordinator> logger)
{
    /// <summary>Actor of worker-side volume audit events, on behalf of the initiator.</summary>
    public const string WorkerActor = "service:production-volume";

    private static readonly JsonWriterOptions JsonWriting = new() { Indented = true };

    private static readonly ExportFileKind[] Delivered = [ExportFileKind.Native, ExportFileKind.Text, ExportFileKind.Image];

    public async Task<ProductionVolumeStep> ProcessAsync(ActiveExport active, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(active);
        var volume = active.Export;
        switch (active.JobStatus)
        {
            case JobStatus.Created or JobStatus.Preparing:
                return await StartAsync(volume, cancellationToken).ConfigureAwait(false);
            case JobStatus.Completed:
                return await FinalizeAsync(volume, cancellationToken).ConfigureAwait(false);
            case JobStatus.CompletedWithErrors:
                return await EndAsync(volume, ExportStatus.Failed, string.Create(CultureInfo.InvariantCulture,
                    $"{active.ChunksFailed} chunk(s) failed; the volume is incomplete. Fix the cause and run the volume again."), cancellationToken).ConfigureAwait(false);
            case JobStatus.Failed:
                var job = await jobs.GetAsync(volume.WorkspaceId, volume.JobId, cancellationToken).ConfigureAwait(false);
                return await EndAsync(volume, ExportStatus.Failed, job?.StatusReason ?? "The volume job failed.", cancellationToken).ConfigureAwait(false);
            case JobStatus.Cancelled:
                return await EndAsync(volume, ExportStatus.Cancelled, "The volume job was cancelled.", cancellationToken).ConfigureAwait(false);
            default:
                return ProductionVolumeStep.None;
        }
    }

    private async Task<ProductionVolumeStep> EndAsync(ExportRecord volume, ExportStatus status, string reason, CancellationToken cancellationToken) =>
        await exports.EndAsync(volume.WorkspaceId, volume.ExportId, status, reason, cancellationToken).ConfigureAwait(false)
            ? ProductionVolumeStep.Ended
            : ProductionVolumeStep.None;

    private async Task<ProductionVolumeStep> StartAsync(ExportRecord volume, CancellationToken cancellationToken)
    {
        var ws = volume.WorkspaceId;
        if (!await exports.TryClaimAsync(ws, volume.ExportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false)
            || await jobs.GetAsync(ws, volume.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return ProductionVolumeStep.None;
        }

        if (job.Status == JobStatus.Created && !(await jobs.BeginPreparingAsync(ws, job.JobId, cancellationToken).ConfigureAwait(false)).Applied)
        {
            return ProductionVolumeStep.None;
        }

        var production = volume.ProductionId is { } id ? await productions.GetAsync(ws, id, cancellationToken).ConfigureAwait(false) : null;
        if (production is not { Status: ProductionStatus.Finalized })
        {
            const string Reason = "The production is no longer finalized; its volume is not written.";
            await jobs.FailAsync(ws, job.JobId, Reason, cancellationToken).ConfigureAwait(false);
            await exports.EndAsync(ws, volume.ExportId, ExportStatus.Failed, Reason, cancellationToken).ConfigureAwait(false);
            return ProductionVolumeStep.Ended;
        }

        // Ranges of production sequence numbers; ADR-010 §6: at most 100 documents and about 2,000 pages a chunk.
        var plans = new List<ChunkPlan>();
        long first = 0, last = 0, units = 0, after = 0;
        void Close()
        {
            if (first > 0)
            {
                plans.Add(new ChunkPlan(ChunkMembership.SnapshotRange(production.SnapshotId, first, last), checked((int)(last - first + 1))));
            }

            first = 0;
            units = 0;
        }

        while (true)
        {
            var page = await productions.ReadDocumentsAsync(ws, production.ProductionId, after, 5_000, cancellationToken).ConfigureAwait(false);
            foreach (var member in page)
            {
                var pages = Math.Max(1, member.Units);
                if (first > 0 && (last - first + 1 >= options.DocumentsPerChunk || units + pages > options.PagesPerChunk))
                {
                    Close();
                }

                first = first == 0 ? member.Sequence : first;
                last = member.Sequence;
                units += pages;
            }

            if (page.Count < 5_000)
            {
                break;
            }

            after = page[^1].Sequence;
        }

        Close();
        var started = await jobs.StartAsync(new JobStartRequest(ws, job.JobId, ChunkOperationKind.ProductionVolumeChunk, plans), cancellationToken)
            .ConfigureAwait(false);
        await exports.ReleaseClaimAsync(ws, volume.ExportId, options.WorkerId, cancellationToken).ConfigureAwait(false);
        LogStarted(logger, production.ProductionId, volume.ExportId, plans.Count);
        return started.Applied ? ProductionVolumeStep.Started : ProductionVolumeStep.None;
    }

    private async Task<ProductionVolumeStep> FinalizeAsync(ExportRecord volume, CancellationToken cancellationToken)
    {
        var ws = volume.WorkspaceId;
        if (!await exports.TryClaimAsync(ws, volume.ExportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false)
            || volume.ProductionId is not { } productionId
            || await productions.GetAsync(ws, productionId, cancellationToken).ConfigureAwait(false) is not { } production)
        {
            return ProductionVolumeStep.None;
        }

        var settings = ExportSettings.Deserialize(volume.SettingsJson);
        var layout = new ProductionVolumeLayout(settings);
        var runId = volume.JobId;
        var finals = new List<NewExportFile>();

        var pageParts = await ExportCoordinator.AllFilesAsync(exports, ws, volume.ExportId, [ExportFileKind.PagePart], cancellationToken).ConfigureAwait(false);
        var verification = await ExportCoordinator.PutPartsAsync(store, ws, volume.ExportId, runId, "verification.csv", ExportFileKind.Verification,
            ProductionVolumeLayout.VerificationPath, Encoding.UTF8.GetBytes(LoadFileText.CsvRow(ProductionVolumeChunkExecutor.PageRecordHeader)), pageParts,
            cancellationToken).ConfigureAwait(false);
        var checkParts = await ExportCoordinator.AllFilesAsync(exports, ws, volume.ExportId, [ExportFileKind.VerificationPart], cancellationToken)
            .ConfigureAwait(false);
        var burnIn = await ExportCoordinator.PutPartsAsync(store, ws, volume.ExportId, runId, "burn-in-report.csv", ExportFileKind.Verification,
            ProductionVolumeLayout.BurnInReportPath, Encoding.UTF8.GetBytes(LoadFileText.CsvRow(BurnInVerification.ReportHeader)), checkParts,
            cancellationToken).ConfigureAwait(false);
        var summary = await SummarizeAsync(burnIn, cancellationToken).ConfigureAwait(false);
        var checkedRun = new ExportVerification(summary.Passed, summary.Documents, summary.Pages, summary.Boxes, summary.Failures, burnIn.Sha256);
        if (!summary.Passed)
        {
            return await RejectAsync(volume, productionId, settings, [verification, burnIn], summary, checkedRun, cancellationToken).ConfigureAwait(false);
        }

        var header = new List<byte>(LoadFileEncodings.Preamble(settings.DatEncoding).ToArray());
        header.AddRange(LoadFileText.Encode(settings.DatEncoding, LoadFileText.DatRow(settings.Profile, [.. settings.Columns.Select(c => c.Header)])));
        var datParts = await ExportCoordinator.AllFilesAsync(exports, ws, volume.ExportId, [ExportFileKind.DatPart], cancellationToken).ConfigureAwait(false);
        finals.Add(await ExportCoordinator.PutPartsAsync(store, ws, volume.ExportId, runId, "volume.dat", ExportFileKind.Dat, layout.DatPath, [.. header],
            datParts, cancellationToken).ConfigureAwait(false));
        var optParts = await ExportCoordinator.AllFilesAsync(exports, ws, volume.ExportId, [ExportFileKind.OptPart], cancellationToken).ConfigureAwait(false);
        finals.Add(await ExportCoordinator.PutPartsAsync(store, ws, volume.ExportId, runId, "volume.opt", ExportFileKind.Opt, layout.OptPath, [],
            optParts, cancellationToken).ConfigureAwait(false));
        var totals = await exports.GetDocumentTotalsAsync(ws, volume.ExportId, cancellationToken).ConfigureAwait(false);

        // E12-T07: the load files read back must reconcile with what the run registered before anything is delivered.
        var reconciliation = await ReconcileAsync(production, settings, finals[0], finals[1], totals, cancellationToken).ConfigureAwait(false);
        if (!reconciliation.Passed)
        {
            return await RejectReconciliationAsync(volume, productionId, settings, [verification, burnIn], checkedRun, reconciliation, cancellationToken)
                .ConfigureAwait(false);
        }

        var (manifestJson, manifestCsv, files, bytes) = await WriteManifestsAsync(volume, production, settings, totals, finals, verification, checkedRun,
            reconciliation, cancellationToken).ConfigureAwait(false);
        finals.Add(manifestJson);
        finals.Add(manifestCsv);
        finals.Add(verification);
        finals.Add(burnIn);

        var report = new ExportReport(
            totals.Exported, totals.Excluded, totals.Natives, totals.Texts, totals.Images, totals.Pages,
            files + 2, bytes + manifestJson.SizeBytes + manifestCsv.SizeBytes, manifestJson.Sha256, checkedRun);
        var completed = new AuditEvent
        {
            WorkspaceId = ws,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Production.Category,
            Action = AuditTaxonomy.Production.VolumeCompleted,
            ActorType = AuditActorType.Service,
            ActorId = WorkerActor,
            ActorDisplay = "Production volume writer",
            OnBehalfOf = volume.CreatedBy,
            ResourceType = AuditTaxonomy.Production.ResourceType,
            ResourceId = productionId.ToString(),
            Outcome = AuditOutcome.Success,
            JobId = volume.JobId,
            SnapshotId = volume.SnapshotId,
            Details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ProductionId"] = productionId.ToString(),
                ["VolumeId"] = volume.ExportId.ToString(),
                ["Volume"] = settings.VolumeName,
                ["VolumeManifestSha256"] = Convert.ToHexStringLower(manifestJson.Sha256),
                ["Documents"] = Invariant(report.DocumentsExported),
                ["Images"] = Invariant(report.Images),
                ["Files"] = Invariant(report.Files),
                ["TotalBytes"] = Invariant(report.TotalBytes),
                ["BurnInVerification"] = BurnInVerification.Passed,
                ["BurnInDocuments"] = Invariant(summary.Documents),
                ["BurnInPages"] = Invariant(summary.Pages),
                ["BurnInBoxes"] = Invariant(summary.Boxes),
                ["BurnInReportSha256"] = Convert.ToHexStringLower(burnIn.Sha256),
                ["Reconciliation"] = "Passed",
            },
        };
        if (!await exports.CompleteAsync(ws, volume.ExportId, finals, report, completed, cancellationToken).ConfigureAwait(false))
        {
            return ProductionVolumeStep.None;
        }

        LogCompleted(logger, productionId, volume.ExportId, report.DocumentsExported, report.Images);
        return ProductionVolumeStep.Completed;
    }

    /// <summary>Totals of the assembled burn-in report (read back; its bytes must still hash to what was registered).</summary>
    private async Task<BurnInSummary> SummarizeAsync(NewExportFile report, CancellationToken cancellationToken)
    {
        var key = ObjectKey.Parse(report.ObjectKey);
        var stream = await store.OpenReadAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(new VerifyingReadStream(stream, key, Sha256Digest.FromBytes(report.Sha256), report.SizeBytes), Encoding.UTF8);
            var rows = new List<string>();
            _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                rows.Add(line);
            }

            return BurnInVerification.Summarize(rows);
        }
    }

    /// <summary>
    /// Reads the stored DAT and OPT back (their bytes must still hash to what was registered) and reconciles them with
    /// the run's registered files and the production's members and Bates span (E12-T07).
    /// </summary>
    private async Task<VolumeReconciliationResult> ReconcileAsync(
        ProductionRecord production, ExportSettings settings, NewExportFile dat, NewExportFile opt, ExportDocumentTotals totals,
        CancellationToken cancellationToken)
    {
        var datKey = ObjectKey.Parse(dat.ObjectKey);
        var optKey = ObjectKey.Parse(opt.ObjectKey);
        var datStream = await store.OpenReadAsync(datKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (datStream.ConfigureAwait(false))
        {
            var optStream = await store.OpenReadAsync(optKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (optStream.ConfigureAwait(false))
            {
                await using var datRead = new VerifyingReadStream(datStream, datKey, Sha256Digest.FromBytes(dat.Sha256), dat.SizeBytes);
                await using var optRead = new VerifyingReadStream(optStream, optKey, Sha256Digest.FromBytes(opt.Sha256), opt.SizeBytes);
                var format = ProductionSpecificationRules.FormatOf(ProductionSpecificationRules.Deserialize(production.SpecificationJson));
                return await VolumeReconciliation.ReconcileAsync(datRead, optRead, settings, format,
                    new VolumeRegistration(production.BatesDocuments ?? totals.Exported, totals.Images, totals.Natives, totals.Texts, production.BatesUnits ?? 0),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A volume whose load files do not reconcile (E12-T07): the run ends as Failed without load files or manifest, with
    /// <c>Production.VerificationFailed</c> (<c>Check = Reconciliation</c>), so it is never delivered.
    /// </summary>
    private async Task<ProductionVolumeStep> RejectReconciliationAsync(
        ExportRecord volume, Guid productionId, ExportSettings settings, IReadOnlyList<NewExportFile> files, ExportVerification verification,
        VolumeReconciliationResult reconciliation, CancellationToken cancellationToken)
    {
        var reason = "The volume's load files do not reconcile with its files: " + string.Join(" ", reconciliation.Problems) + " The volume is not delivered.";
        var rejected = new AuditEvent
        {
            WorkspaceId = volume.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Production.Category,
            Action = AuditTaxonomy.Production.VerificationFailed,
            ActorType = AuditActorType.Service,
            ActorId = WorkerActor,
            ActorDisplay = "Production volume writer",
            OnBehalfOf = volume.CreatedBy,
            ResourceType = AuditTaxonomy.Production.ResourceType,
            ResourceId = productionId.ToString(),
            Outcome = AuditOutcome.Failure,
            ReasonCode = "ReconciliationFailed",
            JobId = volume.JobId,
            SnapshotId = volume.SnapshotId,
            Details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Check"] = "Reconciliation",
                ["ProductionId"] = productionId.ToString(),
                ["VolumeId"] = volume.ExportId.ToString(),
                ["Volume"] = settings.VolumeName,
                ["DatRows"] = Invariant(reconciliation.DatRows),
                ["OptRows"] = Invariant(reconciliation.OptRows),
                ["Images"] = Invariant(reconciliation.ImageFiles),
                ["BatesSpan"] = Invariant(reconciliation.BatesSpan),
                ["Problems"] = string.Join(" ", reconciliation.Problems),
            },
        };
        if (!await exports.RejectAsync(volume.WorkspaceId, volume.ExportId, files, verification, reason, rejected, cancellationToken).ConfigureAwait(false))
        {
            return ProductionVolumeStep.None;
        }

        LogNotReconciled(logger, productionId, volume.ExportId, string.Join(" ", reconciliation.Problems));
        return ProductionVolumeStep.Rejected;
    }

    /// <summary>
    /// A failed burn-in verification (E12-T06): the run ends as Failed with its verification files and the totals, and
    /// <c>Production.VerificationFailed</c> is audited in the same transaction. No load file or manifest is written, and
    /// a failed run's files are never listed or downloaded, so the volume cannot be delivered.
    /// </summary>
    private async Task<ProductionVolumeStep> RejectAsync(
        ExportRecord volume, Guid productionId, ExportSettings settings, IReadOnlyList<NewExportFile> files, BurnInSummary summary,
        ExportVerification verification, CancellationToken cancellationToken)
    {
        var reason = string.Create(CultureInfo.InvariantCulture,
            $"Burn-in verification failed: {summary.Failures} check(s) failed for {summary.FailedDocuments} document(s). The volume is not delivered; see the burn-in report.");
        var rejected = new AuditEvent
        {
            WorkspaceId = volume.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Production.Category,
            Action = AuditTaxonomy.Production.VerificationFailed,
            ActorType = AuditActorType.Service,
            ActorId = WorkerActor,
            ActorDisplay = "Production volume writer",
            OnBehalfOf = volume.CreatedBy,
            ResourceType = AuditTaxonomy.Production.ResourceType,
            ResourceId = productionId.ToString(),
            Outcome = AuditOutcome.Failure,
            ReasonCode = "BurnInVerificationFailed",
            JobId = volume.JobId,
            SnapshotId = volume.SnapshotId,
            Details = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Check"] = "BurnIn",
                ["ProductionId"] = productionId.ToString(),
                ["VolumeId"] = volume.ExportId.ToString(),
                ["Volume"] = settings.VolumeName,
                ["Documents"] = Invariant(summary.Documents),
                ["Pages"] = Invariant(summary.Pages),
                ["Boxes"] = Invariant(summary.Boxes),
                ["Failures"] = Invariant(summary.Failures),
                ["FailedDocuments"] = Invariant(summary.FailedDocuments),
                ["BurnInReportSha256"] = Convert.ToHexStringLower(verification.ReportSha256),
            },
        };
        if (!await exports.RejectAsync(volume.WorkspaceId, volume.ExportId, files, verification, reason, rejected, cancellationToken).ConfigureAwait(false))
        {
            return ProductionVolumeStep.None;
        }

        LogRejected(logger, productionId, volume.ExportId, summary.Failures, summary.FailedDocuments);
        return ProductionVolumeStep.Rejected;
    }

    /// <summary>
    /// MANIFEST.json (the production's identity, frozen hashes and Bates range, the software that wrote the volume,
    /// counts and every delivered file in path order with size and SHA-256) and MANIFEST.csv (Path, Bytes, SHA256). They
    /// hold no run identifiers or times, so every run of the production on the same software yields the same bytes.
    /// </summary>
    private async Task<(NewExportFile Json, NewExportFile Csv, long Files, long Bytes)> WriteManifestsAsync(
        ExportRecord volume, ProductionRecord production, ExportSettings settings, ExportDocumentTotals totals, List<NewExportFile> finals,
        NewExportFile verification, ExportVerification burnIn, VolumeReconciliationResult reconciliation, CancellationToken cancellationToken)
    {
        var directory = options.TempDirectory ?? Path.GetTempPath();
        var jsonPath = Path.Combine(directory, "opp-volume-" + Guid.NewGuid().ToString("N") + ".json");
        var csvPath = Path.Combine(directory, "opp-volume-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            long count = 0, bytes = 0;
            var format = ProductionSpecificationRules.FormatOf(ProductionSpecificationRules.Deserialize(production.SpecificationJson));
            await using (var jsonFile = File.Create(jsonPath))
            await using (var csvFile = File.Create(csvPath))
            await using (var json = new Utf8JsonWriter(jsonFile, JsonWriting))
            await using (var csv = new StreamWriter(csvFile, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            using (var listHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                // E12-T07: the volume-level hash is the SHA-256 of MANIFEST.csv (byte-order mark and every row), so it
                // covers every delivered file's path, size and SHA-256.
                listHash.AppendData(Encoding.UTF8.GetPreamble());
                async Task CsvAsync(string row)
                {
                    listHash.AppendData(Encoding.UTF8.GetBytes(row));
                    await csv.WriteAsync(row).ConfigureAwait(false);
                }

                await CsvAsync(LoadFileText.CsvRow("Path", "Bytes", "SHA256")).ConfigureAwait(false);
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", 1);
                json.WriteString("kind", "productionVolume");
                json.WriteStartObject("production");
                json.WriteString("productionId", production.ProductionId.ToString("D"));
                json.WriteString("lineageId", production.LineageId.ToString("D"));
                json.WriteNumber("version", production.Version);
                json.WriteString("name", production.Name);
                json.WriteString("specificationSha256", Convert.ToHexStringLower(production.SpecificationSha256));
                json.WriteString("manifestSha256", production.ManifestSha256 is { } sha ? Convert.ToHexStringLower(sha) : null);
                json.WriteString("batesFirst", production.BatesFirst is { } f ? format.Format(f) : null);
                json.WriteString("batesLast", production.BatesLast is { } l ? format.Format(l) : null);
                json.WriteEndObject();
                json.WriteString("volume", settings.VolumeName);
                json.WriteStartObject("software");
                json.WriteString("volumeWriter", ProductionVolumeSettings.WriterVersion);
                json.WriteString("pageImager", imager.Version);
                json.WriteEndObject();
                json.WriteStartObject("documents");
                json.WriteNumber("produced", totals.Exported);
                json.WriteNumber("natives", totals.Natives);
                json.WriteNumber("texts", totals.Texts);
                json.WriteNumber("images", totals.Images);
                json.WriteEndObject();
                json.WriteString("verificationSha256", Convert.ToHexStringLower(verification.Sha256));
                json.WriteStartObject("burnInVerification");
                json.WriteString("status", burnIn.Passed ? "passed" : "failed");
                json.WriteString("verifier", imager.VerifierVersion);
                json.WriteNumber("documents", burnIn.Documents);
                json.WriteNumber("pages", burnIn.Pages);
                json.WriteNumber("boxes", burnIn.Boxes);
                json.WriteNumber("failures", burnIn.Failures);
                json.WriteString("reportSha256", Convert.ToHexStringLower(burnIn.ReportSha256));
                json.WriteEndObject();
                json.WriteStartObject("reconciliation");
                json.WriteString("status", reconciliation.Passed ? "passed" : "failed");
                json.WriteNumber("datRows", reconciliation.DatRows);
                json.WriteNumber("optRows", reconciliation.OptRows);
                json.WriteNumber("images", reconciliation.ImageFiles);
                json.WriteNumber("batesSpan", reconciliation.BatesSpan);
                json.WriteNumber("natives", reconciliation.NativeFiles);
                if (reconciliation.NativeLinks is { } nativeLinks)
                {
                    json.WriteNumber("nativeLinks", nativeLinks);
                }
                else
                {
                    json.WriteNull("nativeLinks");
                }

                json.WriteNumber("texts", reconciliation.TextFiles);
                if (reconciliation.TextLinks is { } textLinks)
                {
                    json.WriteNumber("textLinks", textLinks);
                }
                else
                {
                    json.WriteNull("textLinks");
                }

                json.WriteEndObject();
                json.WriteStartArray("files");

                var pending = new Queue<NewExportFile>(finals.OrderBy(x => x.Path, StringComparer.Ordinal));
                async Task EmitAsync(string path, ExportFileKind kind, long size, byte[] sha)
                {
                    json.WriteStartObject();
                    json.WriteString("path", path);
                    json.WriteString("kind", kind.ToString());
                    json.WriteNumber("bytes", size);
                    json.WriteString("sha256", Convert.ToHexStringLower(sha));
                    json.WriteEndObject();
                    await CsvAsync(LoadFileText.CsvRow(path, Invariant(size), Convert.ToHexStringLower(sha))).ConfigureAwait(false);
                    count++;
                    bytes += size;
                }

                string? afterPath = null;
                while (true)
                {
                    var page = await exports.GetFilesAsync(volume.WorkspaceId, volume.ExportId, Delivered, afterPath, 5_000, cancellationToken).ConfigureAwait(false);
                    foreach (var file in page)
                    {
                        while (pending.Count > 0 && string.CompareOrdinal(pending.Peek().Path, file.Path) < 0)
                        {
                            var p = pending.Dequeue();
                            await EmitAsync(p.Path, p.Kind, p.SizeBytes, p.Sha256).ConfigureAwait(false);
                        }

                        await EmitAsync(file.Path, file.Kind, file.SizeBytes, file.Sha256).ConfigureAwait(false);
                    }

                    if (page.Count < 5_000)
                    {
                        break;
                    }

                    afterPath = page[^1].Path;
                }

                while (pending.Count > 0)
                {
                    var p = pending.Dequeue();
                    await EmitAsync(p.Path, p.Kind, p.SizeBytes, p.Sha256).ConfigureAwait(false);
                }

                json.WriteEndArray();
                json.WriteStartObject("totals");
                json.WriteNumber("files", count);
                json.WriteNumber("bytes", bytes);
                json.WriteString("fileListSha256", Convert.ToHexStringLower(listHash.GetHashAndReset()));
                json.WriteEndObject();
                json.WriteEndObject();
            }

            var manifestJson = await ExportCoordinator.PutLocalFileAsync(store, volume.WorkspaceId, volume.ExportId, volume.JobId, "manifest.json",
                ExportFileKind.Manifest, ExportLayout.ManifestJson, jsonPath, cancellationToken).ConfigureAwait(false);
            var manifestCsv = await ExportCoordinator.PutLocalFileAsync(store, volume.WorkspaceId, volume.ExportId, volume.JobId, "manifest.csv",
                ExportFileKind.Manifest, ExportLayout.ManifestCsv, csvPath, cancellationToken).ConfigureAwait(false);
            return (manifestJson, manifestCsv, count, bytes);
        }
        finally
        {
            File.Delete(jsonPath);
            File.Delete(csvPath);
        }
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Production {ProductionId}: volume run {VolumeId} started with {Chunks} chunk(s)")]
    private static partial void LogStarted(ILogger logger, Guid productionId, Guid volumeId, int chunks);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Production {ProductionId}: volume run {VolumeId} failed its burn-in verification: {Failures} check(s) for {Documents} document(s)")]
    private static partial void LogRejected(ILogger logger, Guid productionId, Guid volumeId, long failures, long documents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Production {ProductionId}: volume run {VolumeId} does not reconcile: {Problems}")]
    private static partial void LogNotReconciled(ILogger logger, Guid productionId, Guid volumeId, string problems);

    [LoggerMessage(Level = LogLevel.Information, Message = "Production {ProductionId}: volume run {VolumeId} completed: {Documents} document(s), {Images} image(s)")]
    private static partial void LogCompleted(ILogger logger, Guid productionId, Guid volumeId, long documents, long images);
}

/// <summary>Polls every workspace for production volume runs to plan or finalize (restart-safe: claims expire).</summary>
public sealed partial class ProductionVolumeCoordinatorService(
    IServiceScopeFactory scopes, ProductionVolumeOptions options, ILogger<ProductionVolumeCoordinatorService> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var chunks = scope.ServiceProvider.GetRequiredService<IJobChunkRepository>();
        var exports = scope.ServiceProvider.GetRequiredService<IExportStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<ProductionVolumeCoordinator>();
        var steps = 0;
        foreach (var workspaceId in await chunks.GetWorkspacesToSweepAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var active in await exports.GetActiveVolumesAsync(workspaceId, 20, cancellationToken).ConfigureAwait(false))
            {
                if (await coordinator.ProcessAsync(active, cancellationToken).ConfigureAwait(false) != ProductionVolumeStep.None)
                {
                    steps++;
                }
            }
        }

        return steps;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Production volume pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}

public static class ProductionVolumeRegistration
{
    /// <summary>
    /// The volume writer of the rendering worker (E12-T05): the chunk executor, the coordinator and (with
    /// <paramref name="runCoordinator"/>) its polling service. The host registers the stores, the PDP, object storage
    /// and the page imager (the rendering module's, over the render sandbox).
    /// </summary>
    public static IServiceCollection AddProductionVolumeJobs(this IServiceCollection services, ProductionVolumeOptions? options = null, bool runCoordinator = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new ProductionVolumeOptions());
        services.TryAddScoped<ProductionVolumeCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IJobChunkExecutor, ProductionVolumeChunkExecutor>());
        if (runCoordinator)
        {
            services.AddHostedService<ProductionVolumeCoordinatorService>();
        }

        return services;
    }

    /// <summary>The API side: <see cref="ProductionVolumeService"/> (the host registers the stores).</summary>
    public static IServiceCollection AddProductionVolumeService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ProductionVolumeService>();
        return services;
    }
}
