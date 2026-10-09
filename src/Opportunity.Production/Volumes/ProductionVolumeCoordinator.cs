using System.Globalization;
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
/// <c>Production.VolumeCompleted</c>. Both steps are restartable under a claim and depend only on frozen state and
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

        var header = new List<byte>(LoadFileEncodings.Preamble(settings.DatEncoding).ToArray());
        header.AddRange(LoadFileText.Encode(settings.DatEncoding, LoadFileText.DatRow(settings.Profile, [.. settings.Columns.Select(c => c.Header)])));
        var datParts = await ExportCoordinator.AllFilesAsync(exports, ws, volume.ExportId, [ExportFileKind.DatPart], cancellationToken).ConfigureAwait(false);
        finals.Add(await ExportCoordinator.PutPartsAsync(store, ws, volume.ExportId, runId, "volume.dat", ExportFileKind.Dat, layout.DatPath, [.. header],
            datParts, cancellationToken).ConfigureAwait(false));
        var optParts = await ExportCoordinator.AllFilesAsync(exports, ws, volume.ExportId, [ExportFileKind.OptPart], cancellationToken).ConfigureAwait(false);
        finals.Add(await ExportCoordinator.PutPartsAsync(store, ws, volume.ExportId, runId, "volume.opt", ExportFileKind.Opt, layout.OptPath, [],
            optParts, cancellationToken).ConfigureAwait(false));
        var pageParts = await ExportCoordinator.AllFilesAsync(exports, ws, volume.ExportId, [ExportFileKind.PagePart], cancellationToken).ConfigureAwait(false);
        var verification = await ExportCoordinator.PutPartsAsync(store, ws, volume.ExportId, runId, "verification.csv", ExportFileKind.Verification,
            ProductionVolumeLayout.VerificationPath, Encoding.UTF8.GetBytes(LoadFileText.CsvRow(ProductionVolumeChunkExecutor.PageRecordHeader)), pageParts,
            cancellationToken).ConfigureAwait(false);

        var totals = await exports.GetDocumentTotalsAsync(ws, volume.ExportId, cancellationToken).ConfigureAwait(false);
        var (manifestJson, manifestCsv, files, bytes) = await WriteManifestsAsync(volume, production, settings, totals, finals, verification, cancellationToken)
            .ConfigureAwait(false);
        finals.Add(manifestJson);
        finals.Add(manifestCsv);
        finals.Add(verification);

        var report = new ExportReport(
            totals.Exported, totals.Excluded, totals.Natives, totals.Texts, totals.Images, totals.Pages,
            files + 2, bytes + manifestJson.SizeBytes + manifestCsv.SizeBytes, manifestJson.Sha256);
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
            },
        };
        if (!await exports.CompleteAsync(ws, volume.ExportId, finals, report, completed, cancellationToken).ConfigureAwait(false))
        {
            return ProductionVolumeStep.None;
        }

        LogCompleted(logger, productionId, volume.ExportId, report.DocumentsExported, report.Images);
        return ProductionVolumeStep.Completed;
    }

    /// <summary>
    /// MANIFEST.json (the production's identity, frozen hashes and Bates range, the software that wrote the volume,
    /// counts and every delivered file in path order with size and SHA-256) and MANIFEST.csv (Path, Bytes, SHA256). They
    /// hold no run identifiers or times, so every run of the production on the same software yields the same bytes.
    /// </summary>
    private async Task<(NewExportFile Json, NewExportFile Csv, long Files, long Bytes)> WriteManifestsAsync(
        ExportRecord volume, ProductionRecord production, ExportSettings settings, ExportDocumentTotals totals, List<NewExportFile> finals,
        NewExportFile verification, CancellationToken cancellationToken)
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
            {
                await csv.WriteAsync(LoadFileText.CsvRow("Path", "Bytes", "SHA256")).ConfigureAwait(false);
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
                    await csv.WriteAsync(LoadFileText.CsvRow(path, Invariant(size), Convert.ToHexStringLower(sha))).ConfigureAwait(false);
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
