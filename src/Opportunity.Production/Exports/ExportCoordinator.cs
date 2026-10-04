using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Exports;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Application.Storage;
using Opportunity.Core.Jobs;
using Opportunity.Core.Snapshots;
using Opportunity.Import.LoadFiles;

using CoreScheme = Opportunity.Core.Storage.EncryptionScheme;

namespace Opportunity.Production.Exports;

public enum ExportStep
{
    /// <summary>Nothing to do now (chunks still running, or another worker holds the claim).</summary>
    None,

    /// <summary>The job's chunks were planned from the frozen set and the job started.</summary>
    Started,

    /// <summary>The DAT, OPT, manifest and reports were written and the export completed.</summary>
    Completed,

    /// <summary>The export ended as Failed or Cancelled with its job.</summary>
    Ended,
}

/// <summary>
/// Plans and finalizes exports in the export worker (E12-T01). Planning (Created/Preparing → Running) splits the Ready
/// frozen set into dense ordinal ranges of <see cref="ExportJobOptions.DocumentsPerChunk"/> (ADR-010 §4/§6). Once every
/// chunk has settled, finalization assembles the DAT (byte-order mark and header, then each chunk's part in chunk order)
/// and the OPT, writes the exclusion report and the manifest (every delivered file with its size and SHA-256, and the
/// counts), registers them and completes the export with <c>Export.Completed</c>. Both steps are restartable under a
/// claim: plans come from the frozen set alone, and the finalization's output is a pure function of the committed
/// parts, so a worker that takes over writes the same bytes (write-once objects accept identical re-puts).
/// </summary>
public sealed partial class ExportCoordinator(
    IExportStore exports,
    IDocumentSetSnapshotStore snapshots,
    IJobRepository jobs,
    IObjectStore store,
    ExportJobOptions options,
    ILogger<ExportCoordinator> logger)
{
    private static readonly ExportFileKind[] Delivered =
        [ExportFileKind.Native, ExportFileKind.Text, ExportFileKind.Image, ExportFileKind.Dat, ExportFileKind.Opt, ExportFileKind.Report];

    private static readonly JsonWriterOptions JsonWriting = new() { Indented = true };

    public async Task<ExportStep> ProcessAsync(ActiveExport active, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(active);
        var export = active.Export;
        switch (active.JobStatus)
        {
            case JobStatus.Created or JobStatus.Preparing:
                return await StartAsync(export, cancellationToken).ConfigureAwait(false);
            case JobStatus.Completed:
                return await FinalizeAsync(export, cancellationToken).ConfigureAwait(false);
            case JobStatus.CompletedWithErrors when active.ChunksFailed > 0:
                return await EndAsync(export, ExportStatus.Failed, string.Create(CultureInfo.InvariantCulture,
                    $"{active.ChunksFailed} chunk(s) failed; the volume is incomplete. Replay the failed chunks or export again."), cancellationToken).ConfigureAwait(false);
            case JobStatus.CompletedWithErrors:
                return await FinalizeAsync(export, cancellationToken).ConfigureAwait(false);
            case JobStatus.Failed:
                var job = await jobs.GetAsync(export.WorkspaceId, export.JobId, cancellationToken).ConfigureAwait(false);
                return await EndAsync(export, ExportStatus.Failed, job?.StatusReason ?? "The export job failed.", cancellationToken).ConfigureAwait(false);
            case JobStatus.Cancelled:
                return await EndAsync(export, ExportStatus.Cancelled, "The export job was cancelled.", cancellationToken).ConfigureAwait(false);
            default:
                return ExportStep.None;
        }
    }

    private async Task<ExportStep> EndAsync(ExportRecord export, ExportStatus status, string reason, CancellationToken cancellationToken) =>
        await exports.EndAsync(export.WorkspaceId, export.ExportId, status, reason, cancellationToken).ConfigureAwait(false)
            ? ExportStep.Ended
            : ExportStep.None;

    private async Task<ExportStep> StartAsync(ExportRecord export, CancellationToken cancellationToken)
    {
        var ws = export.WorkspaceId;
        if (!await exports.TryClaimAsync(ws, export.ExportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false)
            || await jobs.GetAsync(ws, export.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return ExportStep.None;
        }

        if (job.Status == JobStatus.Created && !(await jobs.BeginPreparingAsync(ws, job.JobId, cancellationToken).ConfigureAwait(false)).Applied)
        {
            return ExportStep.None;
        }

        var snapshot = await snapshots.GetAsync(ws, export.SnapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is not { Status: SnapshotStatus.Ready })
        {
            const string Reason = "The frozen set is not Ready (it expired or failed); create a new export.";
            await jobs.FailAsync(ws, job.JobId, Reason, cancellationToken).ConfigureAwait(false);
            await exports.EndAsync(ws, export.ExportId, ExportStatus.Failed, Reason, cancellationToken).ConfigureAwait(false);
            return ExportStep.Ended;
        }

        var plans = DocumentSetSnapshotService.PlanChunks(snapshot, options.DocumentsPerChunk)
            .Select(m => new ChunkPlan(m, checked((int)m.KnownCount!.Value)))
            .ToList();
        var started = await jobs.StartAsync(new JobStartRequest(ws, job.JobId, ChunkOperationKind.ExportChunk, plans), cancellationToken)
            .ConfigureAwait(false);
        await exports.ReleaseClaimAsync(ws, export.ExportId, options.WorkerId, cancellationToken).ConfigureAwait(false);
        LogStarted(logger, export.ExportId, plans.Count);
        return started.Applied ? ExportStep.Started : ExportStep.None;
    }

    private async Task<ExportStep> FinalizeAsync(ExportRecord export, CancellationToken cancellationToken)
    {
        var ws = export.WorkspaceId;
        if (!await exports.TryClaimAsync(ws, export.ExportId, options.WorkerId, options.ClaimLease, cancellationToken).ConfigureAwait(false))
        {
            return ExportStep.None;
        }

        var settings = ExportSettings.Deserialize(export.SettingsJson);
        var layout = new ExportLayout(settings);
        var runId = export.JobId;
        var finals = new List<NewExportFile>();

        // DAT: byte-order mark and header, then each chunk's rows in chunk order.
        var header = new List<byte>(LoadFileEncodings.Preamble(settings.DatEncoding).ToArray());
        header.AddRange(LoadFileText.Encode(settings.DatEncoding, LoadFileText.DatRow(settings.Profile, [.. settings.Columns.Select(c => c.Header)])));
        var datParts = await AllFilesAsync(ws, export.ExportId, [ExportFileKind.DatPart], cancellationToken).ConfigureAwait(false);
        finals.Add(await PutAsync(ws, export.ExportId, runId, "volume.dat", ExportFileKind.Dat, layout.DatPath, [.. header], datParts, cancellationToken)
            .ConfigureAwait(false));
        if (settings.IncludeImages)
        {
            var optParts = await AllFilesAsync(ws, export.ExportId, [ExportFileKind.OptPart], cancellationToken).ConfigureAwait(false);
            finals.Add(await PutAsync(ws, export.ExportId, runId, "volume.opt", ExportFileKind.Opt, layout.OptPath, [], optParts, cancellationToken)
                .ConfigureAwait(false));
        }

        // Q-15 exclusion report (ticket review E12-T01: a downloadable CSV of Control Number and reason).
        var exclusions = new StringBuilder(LoadFileText.CsvRow("Control Number", "Document ID", "Reason"));
        long after = 0;
        while (true)
        {
            var page = await exports.GetExclusionsAsync(ws, export.ExportId, after, 5_000, cancellationToken).ConfigureAwait(false);
            foreach (var row in page)
            {
                exclusions.Append(LoadFileText.CsvRow(row.ControlNumber ?? string.Empty, row.DocumentId.ToString("D"), row.Reason ?? ExportChunkExecutor.AccessChanged));
            }

            if (page.Count < 5_000)
            {
                break;
            }

            after = page[^1].Ordinal;
        }

        finals.Add(await PutAsync(ws, export.ExportId, runId, "exclusions.csv", ExportFileKind.Report, ExportLayout.ExclusionsCsv,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(exclusions.ToString())], [], cancellationToken).ConfigureAwait(false));

        var totals = await exports.GetDocumentTotalsAsync(ws, export.ExportId, cancellationToken).ConfigureAwait(false);
        var (manifestJson, manifestCsv, files, bytes) = await WriteManifestsAsync(export, settings, totals, finals, cancellationToken).ConfigureAwait(false);
        finals.Add(manifestJson);
        finals.Add(manifestCsv);

        var report = new ExportReport(
            totals.Exported, totals.Excluded, totals.Natives, totals.Texts, totals.Images, totals.Pages,
            files + 2, bytes + manifestJson.SizeBytes + manifestCsv.SizeBytes, manifestJson.Sha256);
        var completed = ExportChunkExecutor.WorkerEvent(export, null, AuditTaxonomy.Export.Completed, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ExportId"] = export.ExportId.ToString(),
            ["ManifestSha256"] = Convert.ToHexStringLower(manifestJson.Sha256),
            ["DocumentsExported"] = Invariant(report.DocumentsExported),
            ["DocumentsExcluded"] = Invariant(report.DocumentsExcluded),
            ["Files"] = Invariant(report.Files),
            ["TotalBytes"] = Invariant(report.TotalBytes),
        });
        if (!await exports.CompleteAsync(ws, export.ExportId, finals, report, completed, cancellationToken).ConfigureAwait(false))
        {
            return ExportStep.None;
        }

        LogCompleted(logger, export.ExportId, report.DocumentsExported, report.DocumentsExcluded);
        return ExportStep.Completed;
    }

    /// <summary>
    /// MANIFEST.json (counts, settings and every delivered file in path order with size and SHA-256) and MANIFEST.csv
    /// (Path, Bytes, SHA256). Neither contains identifiers of the run or times, so a re-run of the same frozen set with
    /// the same settings over unchanged documents yields byte-identical manifests. Written to temporary files, so a
    /// volume with millions of files is never held in memory.
    /// </summary>
    private async Task<(NewExportFile Json, NewExportFile Csv, long Files, long Bytes)> WriteManifestsAsync(
        ExportRecord export, ExportSettings settings, ExportDocumentTotals totals, List<NewExportFile> finals, CancellationToken cancellationToken)
    {
        var directory = options.TempDirectory ?? Path.GetTempPath();
        var jsonPath = Path.Combine(directory, "opp-export-" + Guid.NewGuid().ToString("N") + ".json");
        var csvPath = Path.Combine(directory, "opp-export-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            long count = 0, bytes = 0;
            await using (var jsonFile = File.Create(jsonPath))
            await using (var csvFile = File.Create(csvPath))
            await using (var json = new Utf8JsonWriter(jsonFile, JsonWriting))
            await using (var csv = new StreamWriter(csvFile, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                await csv.WriteAsync(LoadFileText.CsvRow("Path", "Bytes", "SHA256")).ConfigureAwait(false);
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", 1);
                json.WriteString("snapshotId", export.SnapshotId.ToString("D"));
                json.WriteString("volume", settings.VolumeName);
                json.WritePropertyName("settings");
                using (var document = JsonDocument.Parse(settings.Serialize()))
                {
                    document.WriteTo(json);
                }

                json.WritePropertyName("documents");
                json.WriteStartObject();
                json.WriteNumber("exported", totals.Exported);
                json.WriteNumber("excluded", totals.Excluded);
                json.WriteNumber("natives", totals.Natives);
                json.WriteNumber("texts", totals.Texts);
                json.WriteNumber("images", totals.Images);
                json.WriteNumber("pages", totals.Pages);
                json.WriteEndObject();
                json.WriteStartArray("files");

                // Registered chunk files (keyset by path) merged with the finalization's own files, all in ordinal path order.
                var pending = new Queue<NewExportFile>(finals.OrderBy(f => f.Path, StringComparer.Ordinal));
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
                    var page = await exports.GetFilesAsync(export.WorkspaceId, export.ExportId, [ExportFileKind.Native, ExportFileKind.Text, ExportFileKind.Image],
                        afterPath, 5_000, cancellationToken).ConfigureAwait(false);
                    foreach (var file in page)
                    {
                        while (pending.Count > 0 && string.CompareOrdinal(pending.Peek().Path, file.Path) < 0)
                        {
                            var f = pending.Dequeue();
                            await EmitAsync(f.Path, f.Kind, f.SizeBytes, f.Sha256).ConfigureAwait(false);
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
                    var f = pending.Dequeue();
                    await EmitAsync(f.Path, f.Kind, f.SizeBytes, f.Sha256).ConfigureAwait(false);
                }

                json.WriteEndArray();
                json.WritePropertyName("totals");
                json.WriteStartObject();
                json.WriteNumber("files", count);
                json.WriteNumber("bytes", bytes);
                json.WriteEndObject();
                json.WriteEndObject();
            }

            var runId = export.JobId;
            var manifestJson = await PutFileAsync(export.WorkspaceId, export.ExportId, runId, "manifest.json", ExportFileKind.Manifest, ExportLayout.ManifestJson,
                jsonPath, cancellationToken).ConfigureAwait(false);
            var manifestCsv = await PutFileAsync(export.WorkspaceId, export.ExportId, runId, "manifest.csv", ExportFileKind.Manifest, ExportLayout.ManifestCsv,
                csvPath, cancellationToken).ConfigureAwait(false);
            return (manifestJson, manifestCsv, count, bytes);
        }
        finally
        {
            File.Delete(jsonPath);
            File.Delete(csvPath);
        }
    }

    private async Task<List<ExportFileRecord>> AllFilesAsync(Guid ws, Guid exportId, ExportFileKind[] kinds, CancellationToken cancellationToken)
    {
        var all = new List<ExportFileRecord>();
        string? after = null;
        while (true)
        {
            var page = await exports.GetFilesAsync(ws, exportId, kinds, after, 1_000, cancellationToken).ConfigureAwait(false);
            all.AddRange(page);
            if (page.Count < 1_000)
            {
                return all;
            }

            after = page[^1].Path;
        }
    }

    /// <summary>Writes <paramref name="prefix"/> followed by the parts (each verified against its registered hash).</summary>
    private async Task<NewExportFile> PutAsync(
        Guid ws, Guid exportId, Guid runId, string name, ExportFileKind kind, string path, byte[] prefix, List<ExportFileRecord> parts,
        CancellationToken cancellationToken)
    {
        // Part paths are parts/{sequence:D6}.ext, so path order is chunk order.
        var sources = new List<Func<Task<Stream>>> { () => Task.FromResult<Stream>(new MemoryStream(prefix, writable: false)) };
        foreach (var part in parts.OrderBy(p => p.ChunkSequence).ThenBy(p => p.Path, StringComparer.Ordinal))
        {
            sources.Add(async () =>
            {
                var key = ObjectKey.Parse(part.ObjectKey);
                var stream = await store.OpenReadAsync(key, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new VerifyingReadStream(stream, key, Sha256Digest.FromBytes(part.Sha256), part.SizeBytes);
            });
        }

        var content = new ConcatenatedReadStream(sources);
        await using (content.ConfigureAwait(false))
        {
            var result = await store.PutAsync(ExportLayout.FinalObjectKey(ws, exportId, runId, name), content,
                new PutObjectOptions { ContentType = "application/octet-stream" }, cancellationToken).ConfigureAwait(false);
            return Registered(result, kind, path);
        }
    }

    private async Task<NewExportFile> PutFileAsync(
        Guid ws, Guid exportId, Guid runId, string name, ExportFileKind kind, string path, string localPath, CancellationToken cancellationToken)
    {
        var content = File.OpenRead(localPath);
        await using (content.ConfigureAwait(false))
        {
            var result = await store.PutAsync(ExportLayout.FinalObjectKey(ws, exportId, runId, name), content,
                new PutObjectOptions { ContentType = "application/octet-stream", ExpectedLength = content.Length }, cancellationToken).ConfigureAwait(false);
            return Registered(result, kind, path);
        }
    }

    private static NewExportFile Registered(PutObjectResult result, ExportFileKind kind, string path) => new(
        path,
        kind,
        result.Key.Value,
        result.Sha256.ToBytes(),
        result.Length,
        "application/octet-stream",
        result.KeyId,
        result.EncryptionScheme == EncryptionScheme.Envelope ? CoreScheme.Envelope : CoreScheme.ProviderSse);

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Export {ExportId} started with {Chunks} chunk(s)")]
    private static partial void LogStarted(ILogger logger, Guid exportId, int chunks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Export {ExportId} completed: {Exported} document(s) exported, {Excluded} excluded")]
    private static partial void LogCompleted(ILogger logger, Guid exportId, long exported, long excluded);
}

/// <summary>Polls every workspace for Running exports to plan or finalize (restart-safe: claims expire).</summary>
public sealed partial class ExportCoordinatorService(
    IServiceScopeFactory scopes, ExportJobOptions options, ILogger<ExportCoordinatorService> logger) : BackgroundService
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var chunks = scope.ServiceProvider.GetRequiredService<IJobChunkRepository>();
        var exports = scope.ServiceProvider.GetRequiredService<IExportStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<ExportCoordinator>();
        var steps = 0;
        foreach (var workspaceId in await chunks.GetWorkspacesToSweepAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var active in await exports.GetActiveAsync(workspaceId, 20, cancellationToken).ConfigureAwait(false))
            {
                if (await coordinator.ProcessAsync(active, cancellationToken).ConfigureAwait(false) != ExportStep.None)
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
                // Claims expire; the next pass (here or on another worker) resumes the planning or finalization.
                LogPassFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Export coordination pass failed; retrying on the next poll")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
