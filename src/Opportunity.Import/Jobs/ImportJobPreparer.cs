using System.Globalization;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;
using Opportunity.Import.Volumes;

namespace Opportunity.Import.Jobs;

/// <summary>Import job settings (ADR-010 §6 initial values).</summary>
public sealed class ImportJobOptions
{
    /// <summary>Rows per chunk.</summary>
    public int RowsPerChunk { get; init; } = 500;

    /// <summary>DAT bytes per chunk (long extracted text in a DAT column); a chunk closes at whichever bound comes first.</summary>
    public long MaxChunkBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Control numbers recorded per round trip in the preparation pass.</summary>
    public int KeyBatchSize { get; init; } = 5_000;

    /// <summary>A preparation claim; renewed while the pass runs, taken over by another worker once it expires.</summary>
    public TimeSpan PreparationClaim { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the import worker looks for imports to prepare.</summary>
    public TimeSpan PreparationPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Characters of extracted text the projection indexes (Q-29); longer texts are flagged Text Truncated.</summary>
    public int IndexedTextCap { get; init; } = 10_000_000;

    /// <summary>Volume files of one chunk read and stored in parallel.</summary>
    public int FileConcurrency { get; init; } = 4;

    public string WorkerId { get; init; } = string.Create(
        CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
}

public enum ImportPreparationOutcome
{
    /// <summary>Prepared and started: chunks are planned (or the empty import completed).</summary>
    Started,

    /// <summary>The file or the mapping cannot be imported; the job failed with the reason.</summary>
    Failed,

    /// <summary>Another worker holds the claim, or the job is no longer waiting to be prepared.</summary>
    Skipped,
}

/// <summary>
/// The preparation pass of an import job (Created → Preparing → Running): one streaming read of the DAT that records
/// the first row of every control number (duplicates are judged across the WHOLE file), finds chunk boundaries (rows
/// and DAT bytes), creates the fields and choices the load needs (deferred from E08-T02), and plans one ImportRows chunk
/// per range. Memory stays constant: rows are mapped and dropped; keys go to PostgreSQL in batches. Re-running after a
/// crash is safe: keys keep their first row, fields and choices are reused by name, and the start is a single transition.
/// With an OPT (E08-T05) the pass then stages every OPT row, assigns each OPT document to its DAT row and reports the
/// orphans; an OPT-only load plans its chunks over the OPT's documents instead of DAT rows.
/// </summary>
public sealed partial class ImportJobPreparer(
    IImportBatchStore batches,
    IJobRepository jobs,
    IFieldCatalogRepository fields,
    IWorkspaceReader workspaces,
    IObjectStore store,
    ImportJobOptions options,
    ILogger<ImportJobPreparer> logger,
    ImportVolumeOptions? volumes = null)
{
    public const string ImageVolumeUnavailable = "image-volume-unavailable";
    /// <summary>Failure code: the mapping links natives or text files but the volume cannot be read (E08-T04).</summary>
    public const string VolumeUnavailable = "import-volume-unavailable";

    public async Task<ImportPreparationOutcome> PrepareAsync(Guid workspaceId, Guid importBatchId, CancellationToken cancellationToken = default)
    {
        if (!await batches.TryClaimPreparationAsync(workspaceId, importBatchId, options.WorkerId, options.PreparationClaim, cancellationToken)
                .ConfigureAwait(false)
            || await batches.GetAsync(workspaceId, importBatchId, cancellationToken).ConfigureAwait(false) is not { } batch
            || await jobs.GetAsync(workspaceId, batch.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return ImportPreparationOutcome.Skipped;
        }

        if (job.Status == JobStatus.Created)
        {
            if (!(await jobs.BeginPreparingAsync(workspaceId, job.JobId, cancellationToken).ConfigureAwait(false)).Applied)
            {
                return ImportPreparationOutcome.Skipped;
            }
        }
        else if (job.Status != JobStatus.Preparing)
        {
            return ImportPreparationOutcome.Skipped;
        }

        IReadOnlyList<ImportChunkRange> chunks;
        if (batch.Preparation is null)
        {
            var scan = await ScanAsync(batch, cancellationToken).ConfigureAwait(false);
            if (scan.Failure is { } failure)
            {
                LogFailed(logger, importBatchId, failure);
                await jobs.FailAsync(workspaceId, job.JobId, failure, cancellationToken).ConfigureAwait(false);
                await batches.RecordCompletedAsync(workspaceId, importBatchId, scan.FailureCode, cancellationToken).ConfigureAwait(false);
                return ImportPreparationOutcome.Failed;
            }

            await batches.CompletePreparationAsync(workspaceId, importBatchId, scan.Preparation!, scan.Chunks, cancellationToken).ConfigureAwait(false);
            chunks = scan.Chunks;
        }
        else
        {
            // Prepared before a crash, not started yet: the ranges are stored.
            chunks = await ChunkRangesAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        var plans = chunks
            .Select(c => new ChunkPlan(ChunkMembership.ImportRows(importBatchId, c.RowFrom, c.RowTo), checked((int)(c.RowTo - c.RowFrom + 1)), c.ByteTo - c.ByteFrom))
            .ToList();
        var started = await jobs.StartAsync(new JobStartRequest(workspaceId, job.JobId, ChunkOperationKind.ImportChunk, plans), cancellationToken)
            .ConfigureAwait(false);
        if (started.Status is { } status && JobStateMachine.IsFinished(status))
        {
            await batches.RecordCompletedAsync(workspaceId, importBatchId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        LogStarted(logger, importBatchId, plans.Count);
        return started.Applied || started.Status is not null ? ImportPreparationOutcome.Started : ImportPreparationOutcome.Skipped;
    }

    private async Task<IReadOnlyList<ImportChunkRange>> ChunkRangesAsync(ImportBatchRecord batch, CancellationToken cancellationToken)
    {
        var ranges = new List<ImportChunkRange>();
        var next = 1L;
        while (next <= batch.Preparation!.RowsTotal
               && await batches.GetChunkRangeAsync(batch.WorkspaceId, batch.ImportBatchId, next, cancellationToken).ConfigureAwait(false) is { } range)
        {
            ranges.Add(range);
            next = range.RowTo + 1;
        }

        return ranges;
    }

    private async Task<ScanResult> ScanAsync(ImportBatchRecord batch, CancellationToken cancellationToken)
    {
        var ws = batch.WorkspaceId;
        var profile = ImportProfileRules.Deserialize(batch.ProfileJson);
        var settingsIssues = new List<MappingIssue>();
        var readerOptions = ImportSource.ReaderOptions(profile, null, settingsIssues);
        if (settingsIssues.FirstOrDefault(i => i.Severity == MappingIssueSeverity.Error) is { } settingsError)
        {
            return ScanResult.Fail("The load file settings are invalid: " + settingsError.Message);
        }

        var workspace = await workspaces.GetAsync(ws, cancellationToken).ConfigureAwait(false);
        var caseSensitive = workspace?.ControlNumberCaseSensitive ?? false;
        if (batch.Opt is not null && !ImportVolume.TryOpen(volumes ?? new ImportVolumeOptions(), profile.Paths.VolumeRoot, out _, out var volumeError))
        {
            return ScanResult.Fail(ImageVolumeUnavailable, volumeError + " The OPT's images are read from the volume root (paths.volumeRoot).");
        }

        if (batch.ImagesOnly)
        {
            return await ScanImagesOnlyAsync(batch, profile, caseSensitive, cancellationToken).ConfigureAwait(false);
        }

        var matchByBates = batch.Opt is not null && profile.Images.MatchBy == ImageMatchField.BegBates;
        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var stream = await ImportSource.OpenAsync(store, batch, cancellationToken).ConfigureAwait(false);
        var reader = await DatReader.OpenAsync(stream, readerOptions, leaveOpen: false, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (reader.HasPreflightErrors)
            {
                return ScanResult.Fail("The load file cannot be read: "
                    + string.Join(" ", reader.PreflightIssues.Where(i => i.Severity == DatIssueSeverity.Error).Select(i => i.Message)));
            }

            var mapping = MappingCompiler.Compile(profile, reader.Header.Names, catalog, new MappingOptions
            {
                AutoMap = false,
                ControlNumberCaseSensitive = workspace?.ControlNumberCaseSensitive ?? false,
                MultiValueDelimiter = readerOptions.Profile.MultiValue,
            });
            if (mapping.HasErrors)
            {
                return ScanResult.Fail("The field mapping does not fit the load file: "
                    + string.Join(" ", mapping.Issues.Where(i => i.Severity == MappingIssueSeverity.Error).Select(i => i.Message)));
            }

            // The start authorized what ITS compilation asked for; the catalog may have changed since (a field the
            // profile reuses by name was renamed or deleted), so the recompiled mapping must stay within that scope.
            if (!batch.MayCreateFields && ImportStartScope.FieldsToCreate(mapping) is { Count: > 0 } newFields)
            {
                return ScanResult.Fail(ImportStartScope.FieldCreationNotAuthorized,
                    $"The mapping now creates field(s) {string.Join(", ", newFields.Select(n => "'" + n + "'"))}, which the start of this import did not "
                    + "authorize (a field it mapped was renamed or deleted since). Creating fields needs Workspace.ManageFields; start the import again.");
            }

            if (ImportStartScope.CodingFieldsNotEnabled(mapping, batch.CodingOverlayFieldIds) is { Count: > 0 } coding)
            {
                return ScanResult.Fail(ImportStartScope.CodingFieldNotEnabled,
                    $"The mapping now loads coding or privilege field(s) {string.Join(", ", coding)} that this import did not enable (Q-31); start the import again.");
            }

            var chunks = new List<ImportChunkRange>();
            var volumeColumns = ImportArtifactLinker.VolumeColumns(mapping);
            var volumeChecked = false;
            var keys = new List<ImportKey>(options.KeyBatchSize);
            var batesKeys = new List<ImportKey>(matchByBates ? options.KeyBatchSize : 0);
            var missingChoices = new Dictionary<string, (TargetBinding Target, HashSet<string> Names)>(StringComparer.Ordinal);
            long? dataOffset = null;
            long rows = 0;
            (long Row, long Offset, long Line)? start = null;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
            {
                rows = record.RowNumber;
                dataOffset ??= record.ByteOffset;
                if (start is not { } s)
                {
                    start = (record.RowNumber, record.ByteOffset, record.LineNumber);
                }
                else if (record.RowNumber - s.Row >= options.RowsPerChunk || record.ByteOffset - s.Offset >= options.MaxChunkBytes)
                {
                    chunks.Add(new ImportChunkRange(s.Row, record.RowNumber - 1, s.Offset, record.ByteOffset, s.Line));
                    start = (record.RowNumber, record.ByteOffset, record.LineNumber);
                }

                if (record.IsRejected)
                {
                    continue;
                }

                // E08-T04: the first row that names a native or text file checks that the volume can be read.
                if (!volumeChecked && ImportArtifactLinker.LinksFile(volumeColumns, record.Values))
                {
                    volumeChecked = true;
                    if (ImportArtifactLinker.VolumeProblem(mapping, volumes ?? new ImportVolumeOptions()) is { } volumeProblem)
                    {
                        return ScanResult.Fail(VolumeUnavailable, volumeProblem);
                    }
                }

                var mapped = mapping.Map(record.RowNumber, record.Values);
                if (mapped.ControlNumberNorm is { } norm)
                {
                    keys.Add(new ImportKey(norm, record.RowNumber));
                    if (keys.Count >= options.KeyBatchSize)
                    {
                        await FlushKeysAsync(batch, keys, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (matchByBates
                    && mapped.Cells.FirstOrDefault(c => c.Target.FieldId == SystemFields.BegBates && c.Error is null && c.Value is not null) is { } bates
                    && ControlNumber.TryNormalize(bates.Value!.GetValue<string>(), caseSensitive, null, out var batesNorm, out _))
                {
                    batesKeys.Add(new ImportKey(batesNorm, record.RowNumber));
                    if (batesKeys.Count >= options.KeyBatchSize)
                    {
                        await FlushBatesAsync(batch, batesKeys, cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var cell in mapped.Cells.Where(c => c.Status == CoercionStatus.MissingChoices))
                {
                    if (!missingChoices.TryGetValue(cell.Target.Key, out var entry))
                    {
                        entry = (cell.Target, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        missingChoices[cell.Target.Key] = entry;
                    }

                    entry.Names.UnionWith(cell.MissingChoices);
                }
            }

            if (start is { } last)
            {
                chunks.Add(new ImportChunkRange(last.Row, rows, last.Offset, batch.SourceSize, last.Line));
            }

            await FlushKeysAsync(batch, keys, cancellationToken).ConfigureAwait(false);
            await FlushBatesAsync(batch, batesKeys, cancellationToken).ConfigureAwait(false);
            if (!batch.MayCreateFields && missingChoices.Count > 0)
            {
                return ScanResult.Fail(ImportStartScope.FieldCreationNotAuthorized,
                    "The load file needs new choices, which the start of this import did not authorize. Creating choices needs Workspace.ManageFields; start the import again.");
            }

            var (fieldsCreated, choicesCreated) = await CreateFieldsAndChoicesAsync(ws, mapping, missingChoices, cancellationToken).ConfigureAwait(false);
            long? optRows = null;
            if (batch.Opt is not null)
            {
                var mode = matchByBates ? ImportImageMatchMode.BegBates : ImportImageMatchMode.ControlNumber;
                (optRows, _) = await StageOptAsync(batch, profile, caseSensitive, mode, cancellationToken).ConfigureAwait(false);
            }

            var encoding = reader.Encoding;
            return new ScanResult(
                new ImportPreparation(
                    reader.Header.Names,
                    encoding.Kind.ToString(),
                    readerOptions.RowEncodingFallback ?? encoding.Source != EncodingSource.Override,
                    dataOffset ?? batch.SourceSize,
                    rows,
                    fieldsCreated,
                    choicesCreated,
                    optRows),
                chunks,
                null,
                null);
        }
    }

    /// <summary>An OPT-only load: the OPT's documents are the rows, planned in chunks of <see cref="ImportJobOptions.RowsPerChunk"/>.</summary>
    private async Task<ScanResult> ScanImagesOnlyAsync(
        ImportBatchRecord batch, ImportProfileDefinition profile, bool caseSensitive, CancellationToken cancellationToken)
    {
        var (optRows, match) = await StageOptAsync(batch, profile, caseSensitive, ImportImageMatchMode.OptDocuments, cancellationToken)
            .ConfigureAwait(false);
        var chunks = new List<ImportChunkRange>();
        for (var from = 1L; from <= match.Documents; from += options.RowsPerChunk)
        {
            chunks.Add(new ImportChunkRange(from, Math.Min(from + options.RowsPerChunk - 1, match.Documents), 0, 0, 1));
        }

        return new ScanResult(
            new ImportPreparation([], nameof(LoadFileEncodingKind.Utf8), false, 0, match.Documents, 0, 0, optRows), chunks, null, null);
    }

    /// <summary>
    /// Stages the OPT row by row (document numbers from the 'Y' breaks, the normalized key of each break) in batches,
    /// then lets the store assign documents to rows and report the orphans.
    /// </summary>
    private async Task<(long Rows, ImportImageMatch Match)> StageOptAsync(
        ImportBatchRecord batch, ImportProfileDefinition profile, bool caseSensitive, ImportImageMatchMode mode, CancellationToken cancellationToken)
    {
        var prefix = mode == ImportImageMatchMode.BegBates || profile.Images.MatchBy == ImageMatchField.BegBates ? null : profile.ControlNumberPrefix;
        var stream = await store.OpenReadAsync(ObjectKey.Parse(batch.Opt!.ObjectKey), null, cancellationToken).ConfigureAwait(false);
        var reader = OptReader.Open(stream);
        long rows = 0;
        await using (reader.ConfigureAwait(false))
        {
            var buffer = new List<ImportImageRow>(options.KeyBatchSize);
            long document = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
            {
                rows = record.RowNumber;
                if (record.DocumentBreak)
                {
                    document++;
                }

                string? matchKey = null;
                if (record.DocumentBreak && record.ImageKey.Length > 0
                    && ControlNumber.TryNormalize(record.ImageKey, caseSensitive, prefix, out var norm, out _))
                {
                    matchKey = norm;
                }

                buffer.Add(new ImportImageRow(record.RowNumber, record.LineNumber, document, record.DocumentBreak, record.ImageKey, record.Volume,
                    record.Path, record.PageCount, record.Problem, matchKey));
                if (buffer.Count >= options.KeyBatchSize)
                {
                    await FlushImageRowsAsync(batch, buffer, cancellationToken).ConfigureAwait(false);
                }
            }

            await FlushImageRowsAsync(batch, buffer, cancellationToken).ConfigureAwait(false);
        }

        var match = await batches.MatchImageRowsAsync(batch.WorkspaceId, batch.ImportBatchId, mode, cancellationToken).ConfigureAwait(false);
        LogOptStaged(logger, batch.ImportBatchId, rows, match.Documents, match.Matched, match.Unmatched);
        return (rows, match);
    }

    private async Task FlushImageRowsAsync(ImportBatchRecord batch, List<ImportImageRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await batches.AddImageRowsAsync(batch.WorkspaceId, batch.ImportBatchId, rows, cancellationToken).ConfigureAwait(false);
        rows.Clear();
        await RenewClaimAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushBatesAsync(ImportBatchRecord batch, List<ImportKey> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        await batches.AddBatesKeysAsync(batch.WorkspaceId, batch.ImportBatchId, keys, cancellationToken).ConfigureAwait(false);
        keys.Clear();
        await RenewClaimAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task RenewClaimAsync(ImportBatchRecord batch, CancellationToken cancellationToken)
    {
        if (!await batches.TryClaimPreparationAsync(batch.WorkspaceId, batch.ImportBatchId, options.WorkerId, options.PreparationClaim, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new OperationCanceledException("Another worker took over the preparation of this import.");
        }
    }

    private async Task FlushKeysAsync(ImportBatchRecord batch, List<ImportKey> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        await batches.AddKeysAsync(batch.WorkspaceId, batch.ImportBatchId, keys, cancellationToken).ConfigureAwait(false);
        keys.Clear();
        await RenewClaimAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fields and choices the load creates at load time (the mapping's new-field targets and the "create missing
    /// choices" columns). A name that already exists (an earlier, interrupted preparation) is reused.
    /// </summary>
    private async Task<(int Fields, int Choices)> CreateFieldsAndChoicesAsync(
        Guid workspaceId, CompiledMapping mapping, Dictionary<string, (TargetBinding Target, HashSet<string> Names)> missingChoices,
        CancellationToken cancellationToken)
    {
        var createdFields = 0;
        foreach (var target in mapping.Targets.Where(t => t.CreatesField is not null).DistinctBy(t => t.Key))
        {
            var d = ImportTargets.Definition(target.CreatesField!);
            var result = await fields.CreateFieldAsync(
                new NewField(workspaceId, d.Name, d.Type, FieldStorage.Metadata, d.IsMultiValue, d.DatePrecision, d.DecimalPrecision, d.DecimalScale, d.TextAnalysis),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                createdFields++;
            }
            else if (!result.Errors.Any(e => e.Code == "duplicate-name"))
            {
                throw new InvalidOperationException($"Field '{d.Name}' could not be created: {string.Join(" ", result.Errors.Select(e => e.Message))}");
            }
        }

        if (missingChoices.Count == 0)
        {
            return (createdFields, 0);
        }

        var createdChoices = 0;
        var catalog = await fields.GetCatalogAsync(workspaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var (target, names) in missingChoices.Values)
        {
            var field = target.FieldId is { } id
                ? catalog.Find(id)
                : catalog.Fields.FirstOrDefault(f => !f.IsDeleted && string.Equals(f.Name.Trim(), target.Label.Trim(), StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                continue;
            }

            var existing = catalog.ChoicesOf(field.FieldId).Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names.Where(n => !existing.Contains(n.Trim())).Order(StringComparer.OrdinalIgnoreCase))
            {
                // A failed choice (limit, invalid name) surfaces as row errors ("unknown-choice") of the rows that use it.
                if ((await fields.AddChoiceAsync(workspaceId, field.FieldId, name, cancellationToken).ConfigureAwait(false)).Succeeded)
                {
                    createdChoices++;
                }
            }
        }

        return (createdFields, createdChoices);
    }

    private sealed record ScanResult(ImportPreparation? Preparation, IReadOnlyList<ImportChunkRange> Chunks, string? Failure, string? FailureCode)
    {
        public static ScanResult Fail(string reason) => Fail(null, reason);

        /// <summary>A failure with a code: the job's reason starts with it and the Import.Completed audit carries it.</summary>
        public static ScanResult Fail(string? code, string reason)
        {
            var text = code is null ? reason : code + ": " + reason;
            return new(null, [], text.Length <= 2000 ? text : text[..2000], code);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Import {ImportBatchId} prepared: {Chunks} chunks planned")]
    private static partial void LogStarted(ILogger logger, Guid importBatchId, int chunks);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Import {ImportBatchId}: {OptRows} OPT rows, {Documents} OPT documents, {Matched} matched, {Unmatched} without a row")]
    private static partial void LogOptStaged(ILogger logger, Guid importBatchId, long optRows, long documents, long matched, long unmatched);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import {ImportBatchId} cannot run: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid importBatchId, string reason);
}
