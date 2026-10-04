using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;
using Opportunity.Import.Volumes;

namespace Opportunity.Import.Jobs;

/// <summary>
/// The <see cref="ChunkOperationKind.ImportChunk"/> executor run by the idempotent chunk consumer (E06-T05): reads the
/// chunk's rows <c>RowFrom…RowTo</c> from the DAT (its byte range only), maps them with the import's frozen profile and
/// hands them to <see cref="IImportBatchStore.ApplyChunkAsync"/>, which writes documents, members, row outcomes and the
/// chunk's one IndexChunkTask together with fence F3. Re-running a chunk after a crash re-reads the same bytes; the
/// earlier attempt either committed (the claim refuses a second run) or rolled back completely. Natives and extracted
/// text are stored before the transaction under content-addressed keys of deterministic document ids
/// (<see cref="ImportDocumentIds"/>), so a retry uploads nothing new and registers them with its documents (E08-T04).
/// With an OPT (E08-T05) the chunk also stores the images of its rows' documents (content-addressed, so a re-run puts
/// nothing new) and hands their page sets to the same transaction; an OPT-only load's rows are OPT documents.
/// </summary>
public sealed class ImportChunkExecutor(
    IImportBatchStore batches,
    IFieldCatalogRepository fields,
    IWorkspaceReader workspaces,
    IObjectStore store,
    ImportJobOptions options,
    ImportVolumeOptions? volumes = null) : IJobChunkExecutor
{
    public ChunkOperationKind OperationKind => ChunkOperationKind.ImportChunk;

    public async Task<ChunkExecutionResult> ExecuteAsync(ChunkExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Chunk;
        var membership = chunk.Membership;
        if (membership.Kind != ChunkMembershipKind.ImportRows || membership.ImportBatchId is not { } batchId)
        {
            throw new PermanentChunkException("NotAnImportChunk", "An import chunk references import rows.");
        }

        var ws = context.WorkspaceId;
        var batch = await batches.GetAsync(ws, batchId, cancellationToken).ConfigureAwait(false);
        if (batch?.Preparation is not { } preparation || batch.JobId != chunk.Lease.JobId)
        {
            throw new PermanentChunkException("ImportBatchNotPrepared", "The chunk's import batch is missing or was never prepared.");
        }

        var range = await batches.GetChunkRangeAsync(ws, batchId, membership.RangeFrom!.Value, cancellationToken).ConfigureAwait(false);
        if (range is null || range.RowTo != membership.RangeTo)
        {
            throw new PermanentChunkException("ImportRangeMissing", "The chunk's byte range was not recorded by the preparation pass.");
        }

        var profile = ImportProfileRules.Deserialize(batch.ProfileJson);
        var workspace = await workspaces.GetAsync(ws, cancellationToken).ConfigureAwait(false);
        var caseSensitive = workspace?.ControlNumberCaseSensitive ?? false;
        if (batch.ImagesOnly)
        {
            var imageRows = await ImageOnlyRowsAsync(ws, batch, profile, caseSensitive, range, cancellationToken).ConfigureAwait(false);
            await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
            var written = await batches.ApplyChunkAsync(chunk, new ImportChunkWrite(batchId, ImportMode.Overlay, imageRows), cancellationToken)
                .ConfigureAwait(false);
            return ChunkExecutionResult.Committed(written.Commit);
        }

        var settingsIssues = new List<MappingIssue>();
        var readerOptions = ImportSource.ReaderOptions(profile, preparation, settingsIssues);
        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var mapping = MappingCompiler.Compile(profile, preparation.Header, catalog, new MappingOptions
        {
            AutoMap = false,
            ControlNumberCaseSensitive = caseSensitive,
            MultiValueDelimiter = readerOptions.Profile.MultiValue,
        });
        if (mapping.HasErrors || settingsIssues.Any(i => i.Severity == MappingIssueSeverity.Error))
        {
            // E.g. a mapped field was deleted after the import started; retrying cannot fix that.
            var reason = string.Join(" ", settingsIssues.Concat(mapping.Issues).Where(i => i.Severity == MappingIssueSeverity.Error).Select(i => i.Message));
            throw new PermanentChunkException("MappingInvalid", reason.Length <= 1_000 ? reason : reason[..1_000]);
        }

        var codingFields = batch.CodingOverlayFieldIds.ToHashSet();
        if (ImportStartScope.CodingFieldsNotEnabled(mapping, codingFields) is { Count: > 0 } notEnabled)
        {
            // The catalog changed after the start; only the fields the start enabled may be loaded (Q-31).
            throw new PermanentChunkException("CodingFieldNotEnabled",
                $"The mapping now loads coding or privilege field(s) {string.Join(", ", notEnabled)} that this import did not enable (Q-31).");
        }

        var linker = ImportArtifactLinker.Create(mapping, options, volumes ?? new ImportVolumeOptions(), store);
        var rows = new List<ImportRow>(checked((int)(range.RowTo - range.RowFrom + 1)));
        var values = new List<IReadOnlyList<string>>(linker is null ? 0 : rows.Capacity);
        var stream = ImportSource.OpenChunk(store, batch, range);
        var reader = await DatReader.OpenAsync(stream, readerOptions, leaveOpen: false, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (reader.HasPreflightErrors)
            {
                throw new PermanentChunkException("ImportSourceUnreadable", "The chunk's bytes no longer parse like the prepared file.");
            }

            long? firstLine = null;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
            {
                var rowNo = range.RowFrom + record.RowNumber - 1;
                if (rowNo > range.RowTo)
                {
                    throw new PermanentChunkException("ImportRangeMismatch", "The chunk's byte range holds more rows than were prepared.");
                }

                firstLine ??= record.LineNumber;
                rows.Add(ImportRowBuilder.Build(mapping, record, rowNo, range.LineFrom + record.LineNumber - firstLine.Value, ws, batchId, codingFields));
                if (linker is not null)
                {
                    values.Add(record.Values);
                }
            }
        }

        if (rows.Count != range.RowTo - range.RowFrom + 1)
        {
            throw new PermanentChunkException("ImportRangeMismatch", "The chunk's byte range holds fewer rows than were prepared.");
        }

        if (linker is not null)
        {
            await LinkAsync(linker, ws, batch, rows, values, cancellationToken).ConfigureAwait(false);
        }

        if (batch.Opt is not null)
        {
            rows = await LinkImagesAsync(ws, batch, profile, caseSensitive, range, rows, cancellationToken).ConfigureAwait(false);
        }

        // Fence F2 before the PostgreSQL batch; F3 runs inside the store's transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var result = await batches.ApplyChunkAsync(chunk, new ImportChunkWrite(batchId, batch.Mode, rows), cancellationToken).ConfigureAwait(false);
        return ChunkExecutionResult.Committed(result.Commit);
    }

    /// <summary>The images of the DAT rows' OPT documents, stored for the document each row creates or overlays.</summary>
    private async Task<List<ImportRow>> LinkImagesAsync(
        Guid ws, ImportBatchRecord batch, ImportProfileDefinition profile, bool caseSensitive, ImportChunkRange range, List<ImportRow> rows,
        CancellationToken cancellationToken)
    {
        var staged = (await batches.GetImageRowsAsync(ws, batch.ImportBatchId, range.RowFrom, range.RowTo, cancellationToken).ConfigureAwait(false))
            .GroupBy(r => r.RowNo!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ImportImageRow>)[.. g]);
        var volume = OpenVolume(profile);

        // Overlay modes store an existing document's images under its own id.
        IReadOnlyDictionary<string, ImportExistingDocument> existing = new Dictionary<string, ImportExistingDocument>();
        if (batch.Mode != ImportMode.Append)
        {
            var norms = rows.Where(r => !r.HasErrors && staged.ContainsKey(r.RowNo)).Select(r => r.ControlNumberNorm!).ToList();
            existing = await batches.FindDocumentsAsync(ws, norms, ImportImageMatchMode.ControlNumber, caseSensitive, cancellationToken).ConfigureAwait(false);
        }

        var result = new List<ImportRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!staged.TryGetValue(row.RowNo, out var documentRows))
            {
                result.Add(row.HasErrors
                    ? row
                    : row with
                    {
                        Issues = [.. row.Issues, new ImportRowIssue(ImportIssueSeverity.Warning, ImportImageLinker.Codes.NoImages,
                            $"The OPT has no images for {row.ControlNumber}.")],
                    });
                continue;
            }

            if (row.HasErrors)
            {
                var first = documentRows[0];
                result.Add(row with
                {
                    OptIssues = [new ImportOptIssue(first.OptRow, first.LineNo, first.ImageKey, new ImportRowIssue(ImportIssueSeverity.Warning,
                        "opt-document-not-loaded", $"The DAT row of {row.ControlNumber} was not loaded (see its errors), so its images were not linked.",
                        "ImageKey"))],
                });
                continue;
            }

            var documentId = existing.TryGetValue(row.ControlNumberNorm!, out var found) ? found.DocumentId : row.Document!.DocumentId;
            var (images, issues) = await ImportImageLinker.LinkAsync(
                store, ws, documentId, documentRows, volume, profile.Paths.StripPrefix, cancellationToken).ConfigureAwait(false);
            result.Add(row with { Images = images, OptIssues = issues });
        }

        return result;
    }

    /// <summary>An OPT-only load: each OPT document of the range overlays the pages of the existing document it names.</summary>
    private async Task<List<ImportRow>> ImageOnlyRowsAsync(
        Guid ws, ImportBatchRecord batch, ImportProfileDefinition profile, bool caseSensitive, ImportChunkRange range, CancellationToken cancellationToken)
    {
        var documents = (await batches.GetImageRowsAsync(ws, batch.ImportBatchId, range.RowFrom, range.RowTo, cancellationToken).ConfigureAwait(false))
            .GroupBy(r => r.RowNo!.Value)
            .OrderBy(g => g.Key)
            .Select(g => (RowNo: g.Key, Rows: (IReadOnlyList<ImportImageRow>)[.. g]))
            .ToList();
        var by = profile.Images.MatchBy == ImageMatchField.BegBates ? ImportImageMatchMode.BegBates : ImportImageMatchMode.ControlNumber;
        var keys = documents.Select(d => d.Rows[0].MatchKey).OfType<string>().ToList();
        var existing = await batches.FindDocumentsAsync(ws, keys, by, caseSensitive, cancellationToken).ConfigureAwait(false);
        var volume = OpenVolume(profile);
        var rows = new List<ImportRow>(documents.Count);
        foreach (var (rowNo, documentRows) in documents)
        {
            var first = documentRows[0];
            if (first.MatchKey is not { } key || !existing.TryGetValue(key, out var document))
            {
                var label = by == ImportImageMatchMode.BegBates ? "Beg Bates" : "control number";
                rows.Add(new ImportRow
                {
                    RowNo = rowNo,
                    LineNo = first.LineNo,
                    OptRowNo = first.OptRow,
                    ControlNumber = first.ImageKey,
                    Issues = [new ImportRowIssue(ImportIssueSeverity.Error, ImportImageLinker.Codes.DocumentNotFound,
                        $"No document of the workspace has {label} '{first.ImageKey}' (orphan OPT document); its {documentRows.Count} OPT row(s) were not loaded.",
                        "ImageKey")],
                });
                continue;
            }

            var (images, issues) = await ImportImageLinker.LinkAsync(
                store, ws, document.DocumentId, documentRows, volume, profile.Paths.StripPrefix, cancellationToken).ConfigureAwait(false);
            rows.Add(new ImportRow
            {
                RowNo = rowNo,
                LineNo = first.LineNo,
                OptRowNo = first.OptRow,
                ControlNumber = document.ControlNumber,
                ControlNumberNorm = document.ControlNumberNorm,
                // Only the pages change: no column or metadata value is supplied.
                Document = new Document
                {
                    WorkspaceId = ws,
                    DocumentId = document.DocumentId,
                    FamilyId = document.DocumentId,
                    ControlNumber = document.ControlNumber,
                    ControlNumberNorm = document.ControlNumberNorm,
                },
                Images = images,
                OptIssues = issues,
            });
        }

        return rows;
    }

    private ImportVolume? OpenVolume(ImportProfileDefinition profile)
    {
        // The preparation pass refused imports whose volume cannot be opened; a share removed since leaves pages missing.
        ImportVolume.TryOpen(volumes ?? new ImportVolumeOptions(), profile.Paths.VolumeRoot, out var volume, out _);
        return volume;
    }

    /// <summary>
    /// Stores every row's files, <see cref="ImportJobOptions.FileConcurrency"/> rows at a time. An overlaid document's
    /// files go under its existing id; every other row's under the id the row creates.
    /// </summary>
    private async Task LinkAsync(
        ImportArtifactLinker linker, Guid ws, ImportBatchRecord batch, List<ImportRow> rows, List<IReadOnlyList<string>> values, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, Guid> existing = batch.Mode == ImportMode.Append
            ? new Dictionary<string, Guid>()
            : await batches.FindDocumentIdsAsync(ws, [.. rows.Where(r => r.Document is not null).Select(r => r.ControlNumberNorm!).Distinct(StringComparer.Ordinal)], cancellationToken)
                .ConfigureAwait(false);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, rows.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.FileConcurrency), CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var row = rows[i];
                var overlay = false;
                if (row.Document is { } document && row.ControlNumberNorm is { } norm && existing.TryGetValue(norm, out var id))
                {
                    document.DocumentId = id;
                    document.FamilyId = id;
                    overlay = true;
                }

                rows[i] = await linker.LinkAsync(row, values[i], overlay, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }
}
