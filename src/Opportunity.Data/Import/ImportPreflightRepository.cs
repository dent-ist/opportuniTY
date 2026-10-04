using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Import;
using Opportunity.Contracts.Import;

namespace Opportunity.Data.Import;

/// <summary>
/// Pre-flight results (E08-T06, V0026) and the read-only key lookups a pre-flight needs. Saving a pre-flight writes only
/// its own rows and purges the workspace's expired ones.
/// </summary>
public sealed class ImportPreflightRepository(NpgsqlDataSource dataSource) : IImportPreflightStore
{
    private const int InsertBatch = 5_000;

    public async Task<IReadOnlyDictionary<string, ImportKeyState>> FindKeysAsync(
        Guid workspaceId, IReadOnlyCollection<string> controlNumberNorms, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlNumberNorms);
        var states = new Dictionary<string, ImportKeyState>(StringComparer.Ordinal);
        if (controlNumberNorms.Count == 0)
        {
            return states;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT u.n, CASE WHEN r.control_number_norm IS NOT NULL THEN 3 WHEN s.is_deleted THEN 2 ELSE 1 END
            FROM unnest(@norms) AS u(n)
            LEFT JOIN opportunity.document d ON d.workspace_id = @ws AND d.control_number_norm = normalize(u.n, NFC)
            LEFT JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
            LEFT JOIN opportunity.retired_control_number r ON r.workspace_id = @ws AND r.control_number_norm = normalize(u.n, NFC)
            WHERE d.document_id IS NOT NULL OR r.control_number_norm IS NOT NULL
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("norms", controlNumberNorms.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                states[reader.GetString(0)] = reader.GetInt32(1) switch
                {
                    3 => ImportKeyState.Retired,
                    2 => ImportKeyState.Deleted,
                    _ => ImportKeyState.Exists,
                };
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return states;
    }

    public async Task SaveAsync(ImportPreflightRecord preflight, IReadOnlyList<ImportPreflightIssue> issues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(issues);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, preflight.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using (var purge = tx.Command("DELETE FROM opportunity.import_preflight WHERE workspace_id = @ws AND expires_at < now()"))
        {
            purge.Parameters.AddWithValue("ws", preflight.WorkspaceId);
            await purge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.import_preflight (workspace_id, preflight_id, created_by, created_at, expires_at, mode, source_file_name,
                rows_read, error_count, warning_count, issues_dropped)
            VALUES (@ws, @id, @by, @at, @expires, @mode, @file, @rows, @errors, @warnings, @dropped)
            """))
        {
            insert.Parameters.AddWithValue("ws", preflight.WorkspaceId);
            insert.Parameters.AddWithValue("id", preflight.PreflightId);
            insert.Parameters.AddWithValue("by", preflight.CreatedBy);
            insert.Parameters.AddWithValue("at", preflight.CreatedAt);
            insert.Parameters.AddWithValue("expires", preflight.ExpiresAt);
            insert.Parameters.AddWithValue("mode", (short)((int)preflight.Mode + 1));
            insert.Parameters.AddWithValue("file", preflight.SourceFileName);
            insert.Parameters.AddWithValue("rows", preflight.RowsRead);
            insert.Parameters.AddWithValue("errors", preflight.ErrorCount);
            insert.Parameters.AddWithValue("warnings", preflight.WarningCount);
            insert.Parameters.AddWithValue("dropped", preflight.IssuesDropped);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var start = 0; start < issues.Count; start += InsertBatch)
        {
            var part = issues.Skip(start).Take(InsertBatch).ToList();
            await using var command = tx.Command(
                """
                INSERT INTO opportunity.import_preflight_issue (workspace_id, preflight_id, issue_no, row_no, severity, control_number, column_name, code, message)
                SELECT @ws, @id, u.no, u.row_no, u.severity, u.control_number, u.column_name, u.code, u.message
                FROM unnest(@nos, @rows, @severities, @controls, @columns, @codes, @messages)
                    AS u(no, row_no, severity, control_number, column_name, code, message)
                """);
            command.Parameters.AddWithValue("ws", preflight.WorkspaceId);
            command.Parameters.AddWithValue("id", preflight.PreflightId);
            command.Parameters.AddWithValue("nos", Enumerable.Range(start + 1, part.Count).ToArray());
            command.Parameters.AddWithValue("rows", part.Select(i => i.Row).ToArray());
            command.Parameters.AddWithValue("severities", part.Select(i => (short)(i.Severity == ImportRowIssueSeverity.Error ? 1 : 2)).ToArray());
            command.Parameters.Add(new NpgsqlParameter("controls", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = part.Select(i => (object?)Truncate(i.ControlNumber, 1000) ?? DBNull.Value).ToArray(),
            });
            command.Parameters.Add(new NpgsqlParameter("columns", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = part.Select(i => (object?)Truncate(i.Column, 1000) ?? DBNull.Value).ToArray(),
            });
            command.Parameters.AddWithValue("codes", part.Select(i => Truncate(i.Code, 100)!).ToArray());
            command.Parameters.AddWithValue("messages", part.Select(i => Truncate(i.Message, 2000)!).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportPreflightRecord?> GetAsync(Guid workspaceId, Guid preflightId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        ImportPreflightRecord? record = null;
        await using (var command = tx.Command(
            """
            SELECT created_by, created_at, expires_at, mode, source_file_name, rows_read, error_count, warning_count, issues_dropped
            FROM opportunity.import_preflight
            WHERE workspace_id = @ws AND preflight_id = @id AND expires_at > now()
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", preflightId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                record = new ImportPreflightRecord(
                    workspaceId, preflightId, reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2),
                    (ImportMode)(reader.GetInt16(3) - 1), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                    reader.GetInt64(8));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<ImportPreflightIssue>> GetIssuesAsync(
        Guid workspaceId, Guid preflightId, int afterIssueNo, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var issues = new List<ImportPreflightIssue>();
        await using (var command = tx.Command(
            """
            SELECT row_no, control_number, column_name, code, severity, message
            FROM opportunity.import_preflight_issue
            WHERE workspace_id = @ws AND preflight_id = @id AND issue_no > @after
            ORDER BY issue_no
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", preflightId);
            command.Parameters.AddWithValue("after", afterIssueNo);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new ImportPreflightIssue(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt16(4) == 1 ? ImportRowIssueSeverity.Error : ImportRowIssueSeverity.Warning,
                    reader.GetString(5)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return issues;
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
