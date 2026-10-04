using System.Globalization;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;

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
/// </summary>
public sealed partial class ImportJobPreparer(
    IImportBatchStore batches,
    IJobRepository jobs,
    IFieldCatalogRepository fields,
    IWorkspaceReader workspaces,
    IObjectStore store,
    ImportJobOptions options,
    ILogger<ImportJobPreparer> logger)
{
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
            var keys = new List<ImportKey>(options.KeyBatchSize);
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

                var mapped = mapping.Map(record.RowNumber, record.Values);
                if (mapped.ControlNumberNorm is { } norm)
                {
                    keys.Add(new ImportKey(norm, record.RowNumber));
                    if (keys.Count >= options.KeyBatchSize)
                    {
                        await FlushKeysAsync(batch, keys, cancellationToken).ConfigureAwait(false);
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
            if (!batch.MayCreateFields && missingChoices.Count > 0)
            {
                return ScanResult.Fail(ImportStartScope.FieldCreationNotAuthorized,
                    "The load file needs new choices, which the start of this import did not authorize. Creating choices needs Workspace.ManageFields; start the import again.");
            }

            var (fieldsCreated, choicesCreated) = await CreateFieldsAndChoicesAsync(ws, mapping, missingChoices, cancellationToken).ConfigureAwait(false);
            var encoding = reader.Encoding;
            return new ScanResult(
                new ImportPreparation(
                    reader.Header.Names,
                    encoding.Kind.ToString(),
                    readerOptions.RowEncodingFallback ?? encoding.Source != EncodingSource.Override,
                    dataOffset ?? batch.SourceSize,
                    rows,
                    fieldsCreated,
                    choicesCreated),
                chunks,
                null,
                null);
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
        if (!await batches.TryClaimPreparationAsync(batch.WorkspaceId, batch.ImportBatchId, options.WorkerId, options.PreparationClaim, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new OperationCanceledException("Another worker took over the preparation of this import.");
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import {ImportBatchId} cannot run: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid importBatchId, string reason);
}
