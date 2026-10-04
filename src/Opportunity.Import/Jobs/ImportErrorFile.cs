using Opportunity.Application.Import;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;

namespace Opportunity.Import.Jobs;

/// <summary>
/// The re-loadable error file of an import (E08-T06): the DAT's header plus a trailing <c>ImportError</c> column, then
/// every row the import did not load, byte for byte as in the source, with its error codes and messages — in the source's
/// delimiter profile, encoding and byte-order mark. Fixed in the user's usual tool, it loads again unchanged (the mapping
/// never loads the <c>ImportError</c> column). Built on demand from the retained source and the row errors, streaming:
/// the DAT and the errored rows (both in row order) are merged, so memory stays constant.
/// </summary>
public static class ImportErrorFile
{
    private const int RowPage = 1_000;

    /// <summary>The download name: <c>VOL001.dat</c> → <c>VOL001_errors.dat</c>.</summary>
    public static string FileName(string sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(sourceFileName);
        var extension = Path.GetExtension(sourceFileName);
        return Path.GetFileNameWithoutExtension(sourceFileName) + "_errors" + (extension.Length > 0 ? extension : ".dat");
    }

    /// <param name="source">The import's DAT from its start.</param>
    /// <param name="erroredRows">Errored rows after the given row, in row order, at most the given count.</param>
    /// <returns>Rows written, and errored rows that could not be reproduced (longer than the record limit).</returns>
    public static async Task<(long Written, long Unreproducible)> WriteAsync(
        Stream source,
        ImportBatchRecord batch,
        Func<long, int, CancellationToken, Task<IReadOnlyList<ImportErroredRow>>> erroredRows,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(erroredRows);
        ArgumentNullException.ThrowIfNull(output);
        var profile = ImportProfileRules.Deserialize(batch.ProfileJson);
        var options = ImportSource.ReaderOptions(profile, batch.Preparation, []);
        options = new DatReaderOptions
        {
            Profile = options.Profile,
            EncodingOverride = options.EncodingOverride,
            RowEncodingFallback = options.RowEncodingFallback,
            HasHeader = options.HasHeader,
            ConvertNewlineCharacter = options.ConvertNewlineCharacter,
            MaxRejectedRows = null,
            ReturnRejectedRecords = true,
            CaptureRawRecords = true,
        };

        var reader = await DatReader.OpenAsync(source, options, leaveOpen: true, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (reader.HasPreflightErrors)
            {
                throw new InvalidDataException("The import's DAT no longer parses like the prepared file.");
            }

            using var writer = new DatErrorFileWriter(output, leaveOpen: true);
            writer.Start(new DatFileContext(reader.Profile, reader.Encoding, reader.Header.Names, reader.HeaderRecord));
            var page = await erroredRows(0, RowPage, cancellationToken).ConfigureAwait(false);
            var index = 0;
            while (page.Count > 0)
            {
                var next = page[index];
                DatRecord? record;
                do
                {
                    record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                while (record is not null && record.RowNumber < next.RowNo);

                if (record is null)
                {
                    break;
                }

                if (record.RowNumber == next.RowNo)
                {
                    writer.WriteRecord(record.RawRecord.Span, next.ErrorText);
                }

                if (++index == page.Count)
                {
                    page = page.Count < RowPage ? [] : await erroredRows(next.RowNo, RowPage, cancellationToken).ConfigureAwait(false);
                    index = 0;
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (writer.RowsWritten, writer.UnreproducibleRows);
        }
    }
}
