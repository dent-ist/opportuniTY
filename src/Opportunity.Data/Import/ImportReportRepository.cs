using System.Globalization;
using System.Text.Json;

using Npgsql;

using Opportunity.Application.Import;

namespace Opportunity.Data.Import;

/// <summary>
/// The import summary report (E08-T06) over the batch, its members, their documents, the import's page sets and its row
/// issues. A finished import serves the copy frozen on <c>import_batch.report</c> by the completing transaction.
/// </summary>
public sealed class ImportReportRepository(NpgsqlDataSource dataSource) : IImportReportStore
{
    public async Task<ImportReportData?> GetAsync(Guid workspaceId, Guid importBatchId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        string? frozen;
        await using (var command = tx.Command(
            "SELECT report::text FROM opportunity.import_batch WHERE workspace_id = @ws AND import_batch_id = @id"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", importBatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            frozen = reader.IsDBNull(0) ? null : reader.GetString(0);
        }

        ImportReportData? report;
        if (frozen is not null)
        {
            report = ImportReportSql.Deserialize(frozen);
        }
        else
        {
            var batch = await new ImportBatchRepository(dataSource).GetAsync(workspaceId, importBatchId, cancellationToken).ConfigureAwait(false);
            report = batch is null ? null : await ImportReportSql.ComputeAsync(tx, batch, final: false, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    public async Task<IReadOnlyList<ImportErroredRow>> GetErroredRowsAsync(
        Guid workspaceId, Guid importBatchId, long afterRow, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT row_no, string_agg(code || ': ' || message, '; ' ORDER BY issue_no)
            FROM opportunity.import_row_issue
            WHERE workspace_id = @ws AND import_batch_id = @id AND severity = 1 AND source = 1 AND row_no > @after
            GROUP BY row_no
            ORDER BY row_no
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", importBatchId);
        command.Parameters.AddWithValue("after", afterRow);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<ImportErroredRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ImportErroredRow(reader.GetInt64(0), reader.GetString(1)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }
}

/// <summary>The report queries, shared by the report reads and the transaction that completes an import.</summary>
internal static class ImportReportSql
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ImportReportData? Deserialize(string json) => JsonSerializer.Deserialize<ImportReportData>(json, Json);

    /// <summary>Computes the final report of a just-completed batch and stores it on the batch row (same transaction).</summary>
    public static async Task<ImportReportData> FreezeAsync(WorkspaceTransaction tx, ImportBatchRecord batch, CancellationToken cancellationToken)
    {
        var report = await ComputeAsync(tx, batch, final: true, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "UPDATE opportunity.import_batch SET report = @report::jsonb WHERE workspace_id = @ws AND import_batch_id = @id");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", batch.ImportBatchId);
        command.Parameters.AddWithValue("report", JsonSerializer.Serialize(report, Json));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

    public static async Task<ImportReportData> ComputeAsync(
        WorkspaceTransaction tx, ImportBatchRecord batch, bool final, CancellationToken cancellationToken)
    {
        var ws = tx.WorkspaceId;
        long nativesLinked = 0, nativesMissing = 0, textLinked = 0, textMissing = 0, textTruncated = 0, familiesBuilt = 0, orphans = 0;
        await using (var command = tx.Command(
            """
            SELECT count(*) FILTER (WHERE d.native_object_id IS NOT NULL),
                   count(*) FILTER (WHERE d.native_missing),
                   count(*) FILTER (WHERE d.text_object_id IS NOT NULL),
                   count(*) FILTER (WHERE d.text_missing),
                   count(*) FILTER (WHERE d.text_truncated),
                   count(*) FILTER (WHERE d.family_sequence = 0 AND EXISTS (
                       SELECT 1 FROM opportunity.document c
                       WHERE c.workspace_id = d.workspace_id AND c.family_id = d.document_id AND c.document_id <> d.document_id)),
                   count(*) FILTER (WHERE d.family_status = 3)
            FROM opportunity.import_batch_member m
            JOIN opportunity.document d ON d.workspace_id = m.workspace_id AND d.document_id = m.document_id
            WHERE m.workspace_id = @ws AND m.import_batch_id = @id AND m.action IN (1, 2)
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", batch.ImportBatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                (nativesLinked, nativesMissing, textLinked, textMissing, textTruncated, familiesBuilt, orphans) =
                    (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
            }
        }

        long imageDocuments = 0, pagesLinked = 0, pagesMissing = 0;
        await using (var command = tx.Command(
            """
            SELECT count(DISTINCT s.document_id),
                   count(p.ordinal) FILTER (WHERE NOT p.image_missing),
                   count(p.ordinal) FILTER (WHERE p.image_missing)
            FROM opportunity.page_set s
            LEFT JOIN opportunity.page p ON p.workspace_id = s.workspace_id AND p.page_set_id = s.page_set_id
            WHERE s.workspace_id = @ws AND s.import_job_id = @job
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("job", batch.JobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                (imageDocuments, pagesLinked, pagesMissing) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        var counts = new List<ImportReportIssueCount>();
        await using (var command = tx.Command(
            """
            SELECT code, severity, count(*) FROM opportunity.import_row_issue
            WHERE workspace_id = @ws AND import_batch_id = @id
            GROUP BY code, severity
            ORDER BY severity, count(*) DESC, code
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", batch.ImportBatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                counts.Add(new ImportReportIssueCount(reader.GetString(0), (ImportIssueSeverity)reader.GetInt16(1), reader.GetInt64(2)));
            }
        }

        long withWarnings = 0, errorRows = 0;
        await using (var command = tx.Command(
            """
            SELECT count(*) FILTER (WHERE NOT has_error), count(*) FILTER (WHERE has_error)
            FROM (SELECT row_no, bool_or(severity = 1) AS has_error FROM opportunity.import_row_issue
                  WHERE workspace_id = @ws AND import_batch_id = @id AND source = 1
                  GROUP BY row_no) r
            """))
        {
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", batch.ImportBatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                (withWarnings, errorRows) = (reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        var end = batch.CompletedAt ?? DateTimeOffset.UtcNow;
        return new ImportReportData
        {
            Final = final,
            StartedAt = batch.CreatedAt,
            CompletedAt = batch.CompletedAt,
            ElapsedSeconds = Math.Max(0, Math.Round((end - batch.CreatedAt).TotalSeconds, 3)),
            RowsRead = batch.Preparation?.RowsTotal,
            RowsImported = batch.RowsImported,
            RowsOverlaid = batch.RowsOverlaid,
            RowsSkipped = batch.RowsSkipped,
            RowsErrored = batch.RowsErrored,
            RowsWithWarnings = withWarnings,
            NativesLinked = nativesLinked,
            NativesMissing = nativesMissing,
            TextLinked = textLinked,
            TextMissing = textMissing,
            TextTruncated = textTruncated,
            ImageDocumentsLinked = imageDocuments,
            DocumentsWithoutImages = counts.Where(c => c.Code == "no-images").Sum(c => c.Count),
            PagesLinked = pagesLinked,
            PagesMissing = pagesMissing,
            FamiliesBuilt = familiesBuilt,
            FamilyOrphans = orphans,
            FieldsCreated = batch.Preparation?.FieldsCreated ?? 0,
            ChoicesCreated = batch.Preparation?.ChoicesCreated ?? 0,
            ErrorFileRows = batch.ImagesOnly ? 0 : errorRows,
            IssueCounts = counts,
        };
    }

    /// <summary>The headline figures for the <c>Import.Completed</c> audit details.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> AuditDetails(ImportReportData report)
    {
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        yield return new("NativesLinked", N(report.NativesLinked));
        yield return new("NativesMissing", N(report.NativesMissing));
        yield return new("TextLinked", N(report.TextLinked));
        yield return new("TextMissing", N(report.TextMissing));
        yield return new("PagesLinked", N(report.PagesLinked));
        yield return new("PagesMissing", N(report.PagesMissing));
        yield return new("FamiliesBuilt", N(report.FamiliesBuilt));
        yield return new("FamilyOrphans", N(report.FamilyOrphans));
        yield return new("RowsWithWarnings", N(report.RowsWithWarnings));
        yield return new("ElapsedSeconds", report.ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture));
    }
}
