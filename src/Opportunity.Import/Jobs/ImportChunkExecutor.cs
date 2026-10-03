using Opportunity.Application.Fields;
using Opportunity.Application.Import;
using Opportunity.Application.Jobs;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Import;
using Opportunity.Core.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;

namespace Opportunity.Import.Jobs;

/// <summary>
/// The <see cref="ChunkOperationKind.ImportChunk"/> executor run by the idempotent chunk consumer (E06-T05): reads the
/// chunk's rows <c>RowFrom…RowTo</c> from the DAT (its byte range only), maps them with the import's frozen profile and
/// hands them to <see cref="IImportBatchStore.ApplyChunkAsync"/>, which writes documents, members, row outcomes and the
/// chunk's one IndexChunkTask together with fence F3. Re-running a chunk after a crash re-reads the same bytes; the
/// earlier attempt either committed (the claim refuses a second run) or rolled back completely.
/// </summary>
public sealed class ImportChunkExecutor(
    IImportBatchStore batches,
    IFieldCatalogRepository fields,
    IWorkspaceReader workspaces,
    IObjectStore store) : IJobChunkExecutor
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
        var settingsIssues = new List<MappingIssue>();
        var readerOptions = ImportSource.ReaderOptions(profile, preparation, settingsIssues);
        var workspace = await workspaces.GetAsync(ws, cancellationToken).ConfigureAwait(false);
        var catalog = await fields.GetCatalogAsync(ws, cancellationToken: cancellationToken).ConfigureAwait(false);
        var mapping = MappingCompiler.Compile(profile, preparation.Header, catalog, new MappingOptions
        {
            AutoMap = false,
            ControlNumberCaseSensitive = workspace?.ControlNumberCaseSensitive ?? false,
            MultiValueDelimiter = readerOptions.Profile.MultiValue,
        });
        if (mapping.HasErrors || settingsIssues.Any(i => i.Severity == MappingIssueSeverity.Error))
        {
            // E.g. a mapped field was deleted after the import started; retrying cannot fix that.
            var reason = string.Join(" ", settingsIssues.Concat(mapping.Issues).Where(i => i.Severity == MappingIssueSeverity.Error).Select(i => i.Message));
            throw new PermanentChunkException("MappingInvalid", reason.Length <= 1_000 ? reason : reason[..1_000]);
        }

        var codingFields = batch.CodingOverlayFieldIds.ToHashSet();
        var rows = new List<ImportRow>(checked((int)(range.RowTo - range.RowFrom + 1)));
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
            }
        }

        if (rows.Count != range.RowTo - range.RowFrom + 1)
        {
            throw new PermanentChunkException("ImportRangeMismatch", "The chunk's byte range holds fewer rows than were prepared.");
        }

        // Fence F2 before the PostgreSQL batch; F3 runs inside the store's transaction.
        await context.CheckFenceAsync(cancellationToken).ConfigureAwait(false);
        var result = await batches.ApplyChunkAsync(chunk, new ImportChunkWrite(batchId, batch.Mode, rows), cancellationToken).ConfigureAwait(false);
        return ChunkExecutionResult.Committed(result.Commit);
    }
}
