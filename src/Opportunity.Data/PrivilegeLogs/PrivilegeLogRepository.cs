using System.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.PrivilegeLogs;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;

namespace Opportunity.Data.PrivilegeLogs;

/// <summary>
/// PostgreSQL storage of privilege logs (E13-T03, V0059): templates, immutable versions and entries, and the candidate
/// reads. Candidates come set-based from the frozen production (members, their frozen redaction versions and reasons),
/// the coding store (Privilege Status by built-in key) and, for a frozen-set log or a review set, the frozen membership.
/// </summary>
public sealed class PrivilegeLogRepository(NpgsqlDataSource dataSource) : IPrivilegeLogStore
{
    private const int InsertBatch = 1_000;

    private const string TemplateColumns =
        "template_id, name, definition, version, modified_by, modified_by_display, modified_at";

    private const string LogColumns =
        """
        log_id, series_key, version, source_kind, production_id, snapshot_id, scope_snapshot_id, template_id, template_name,
        template_definition, metadata, content_sha256, csv_sha256, csv_bytes, xlsx_sha256, xlsx_bytes, entry_count, withheld_count,
        redacted_count, excluded_count, field_ids, generated_by, generated_by_display, generated_at
        """;

    public async Task<IReadOnlyList<PrivilegeLogTemplateRecord>> ListTemplatesAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var list = await ReadTemplatesAsync(tx, $"SELECT {TemplateColumns} FROM opportunity.privilege_log_template WHERE workspace_id = @ws ORDER BY lower(name), template_id",
            null, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list;
    }

    public async Task<PrivilegeLogTemplateRecord?> GetTemplateAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var list = await ReadTemplatesAsync(tx, $"SELECT {TemplateColumns} FROM opportunity.privilege_log_template WHERE workspace_id = @ws AND template_id = @id",
            templateId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list.SingleOrDefault();
    }

    public async Task<PrivilegeLogTemplateWrite> CreateTemplateAsync(
        Guid workspaceId, Guid templateId, string name, string definitionJson, Guid userId, string userDisplay, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.privilege_log_template
                (workspace_id, template_id, name, definition, created_by, modified_by, modified_by_display)
            VALUES (@ws, @id, @name, @definition, @user, @user, @display)
            """))
        {
            insert.Parameters.AddWithValue("ws", workspaceId);
            insert.Parameters.AddWithValue("id", templateId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("definition", definitionJson);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("display", userDisplay);
            if (!await TryExecuteAsync(insert, cancellationToken).ConfigureAwait(false))
            {
                return new PrivilegeLogTemplateWrite(PrivilegeLogTemplateWriteStatus.NameConflict);
            }
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var record = (await ReadTemplatesAsync(tx, $"SELECT {TemplateColumns} FROM opportunity.privilege_log_template WHERE workspace_id = @ws AND template_id = @id",
            templateId, cancellationToken).ConfigureAwait(false)).Single();
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrivilegeLogTemplateWrite(PrivilegeLogTemplateWriteStatus.Applied, record);
    }

    public async Task<PrivilegeLogTemplateWrite> UpdateTemplateAsync(
        Guid workspaceId, Guid templateId, long expectedVersion, string name, string definitionJson, Guid userId, string userDisplay, AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var lockRow = tx.Command(
            "SELECT version FROM opportunity.privilege_log_template WHERE workspace_id = @ws AND template_id = @id FOR UPDATE"))
        {
            lockRow.Parameters.AddWithValue("ws", workspaceId);
            lockRow.Parameters.AddWithValue("id", templateId);
            switch (await lockRow.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            {
                case long version when version == expectedVersion:
                    break;
                case long:
                    return new PrivilegeLogTemplateWrite(PrivilegeLogTemplateWriteStatus.VersionConflict);
                default:
                    return new PrivilegeLogTemplateWrite(PrivilegeLogTemplateWriteStatus.NotFound);
            }
        }

        await using (var update = tx.Command(
            """
            UPDATE opportunity.privilege_log_template
               SET name = @name, definition = @definition, version = version + 1, modified_by = @user, modified_by_display = @display,
                   modified_at = now()
             WHERE workspace_id = @ws AND template_id = @id
            """))
        {
            update.Parameters.AddWithValue("ws", workspaceId);
            update.Parameters.AddWithValue("id", templateId);
            update.Parameters.AddWithValue("name", name);
            update.Parameters.AddWithValue("definition", definitionJson);
            update.Parameters.AddWithValue("user", userId);
            update.Parameters.AddWithValue("display", userDisplay);
            if (!await TryExecuteAsync(update, cancellationToken).ConfigureAwait(false))
            {
                return new PrivilegeLogTemplateWrite(PrivilegeLogTemplateWriteStatus.NameConflict);
            }
        }

        await AuditSql.InsertAsync(tx, audit, cancellationToken).ConfigureAwait(false);
        var record = (await ReadTemplatesAsync(tx, $"SELECT {TemplateColumns} FROM opportunity.privilege_log_template WHERE workspace_id = @ws AND template_id = @id",
            templateId, cancellationToken).ConfigureAwait(false)).Single();
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrivilegeLogTemplateWrite(PrivilegeLogTemplateWriteStatus.Applied, record);
    }

    public async Task<IReadOnlyList<PrivilegeLogCandidate>> ReadCandidatesAsync(PrivilegeLogCandidateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, query.WorkspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var candidates = new List<(PrivilegeLogCandidate Candidate, long? RedactionSetVersion)>();
        var reasonsWanted = new List<(Guid DocumentId, Guid SetId, long Version)>();
        await using (var command = tx.Command(query.ProductionId is null ? CandidateSql.Snapshot : CandidateSql.Production))
        {
            command.Parameters.AddWithValue("ws", query.WorkspaceId);
            command.Parameters.AddWithValue("snapshot", query.SnapshotId);
            command.Parameters.Add(new NpgsqlParameter("production", NpgsqlDbType.Uuid) { Value = (object?)query.ProductionId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Uuid) { Value = (object?)query.ScopeSnapshotId ?? DBNull.Value });
            command.Parameters.AddWithValue("status", PrivilegeFields.Status);
            command.Parameters.AddWithValue("withhold", PrivilegeFields.Keys.Withhold);
            command.Parameters.AddWithValue("redact", PrivilegeFields.Keys.Redact);
            command.Parameters.AddWithValue("limit", query.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var documentId = reader.GetGuid(0);
                PrivilegeLogMember? member = null;
                if (!reader.IsDBNull(5))
                {
                    var count = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                    member = new PrivilegeLogMember(reader.GetInt64(5), (ProductionOutputKind)reader.GetInt16(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), count, []);
                    if (count > 0 && !reader.IsDBNull(10) && !reader.IsDBNull(11))
                    {
                        reasonsWanted.Add((documentId, reader.GetGuid(10), reader.GetInt64(11)));
                    }
                }

                candidates.Add((new PrivilegeLogCandidate(documentId, reader.GetString(1), reader.GetGuid(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), member), null));
            }
        }

        var reasons = reasonsWanted.Count == 0
            ? new Dictionary<Guid, List<PrivilegeLogRedactionReason>>()
            : await ReadReasonsAsync(tx, reasonsWanted, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. candidates.Select(c => c.Candidate.Member is { } m && reasons.TryGetValue(c.Candidate.DocumentId, out var list)
                ? c.Candidate with { Member = m with { RedactionReasons = list } }
                : c.Candidate),
        ];
    }

    public async Task<IReadOnlyList<PrivilegeLogFamilyMember>> ReadFamilyMembersAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> familyIds, Guid? productionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(familyIds);
        var result = new List<PrivilegeLogFamilyMember>();
        if (familyIds.Count == 0)
        {
            return result;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT d.family_id, d.document_id, d.family_sequence, pd.prod_beg_bates, pd.prod_end_bates
              FROM opportunity.document d
              JOIN opportunity.document_projection_state ps ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
              LEFT JOIN opportunity.production_document pd
                ON pd.workspace_id = d.workspace_id AND pd.production_id = @production AND pd.document_id = d.document_id
             WHERE d.workspace_id = @ws AND d.family_id = ANY(@families)
             ORDER BY d.family_id, d.family_sequence, d.document_id
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("families", familyIds.Distinct().ToArray());
            command.Parameters.Add(new NpgsqlParameter("production", NpgsqlDbType.Uuid) { Value = (object?)productionId ?? DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new PrivilegeLogFamilyMember(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<PrivilegeLogCreation> CreateVersionAsync(NewPrivilegeLogVersion version, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(audit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, version.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using (var gate = tx.Command("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))"))
        {
            gate.Parameters.AddWithValue("key", "privilege-log|" + version.WorkspaceId.ToString("N") + "|" + version.SeriesKey);
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var latest = (await ReadLogsAsync(tx,
            $"SELECT {LogColumns} FROM opportunity.privilege_log WHERE workspace_id = @ws AND series_key = @series ORDER BY version DESC LIMIT 1",
            p => p.AddWithValue("series", version.SeriesKey), cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (latest is not null && latest.ContentSha256.AsSpan().SequenceEqual(version.ContentSha256))
        {
            await AuditSql.InsertAsync(tx, audit with { ResourceId = latest.LogId.ToString() }, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PrivilegeLogCreation(latest, Unchanged: true);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.privilege_log
                (workspace_id, log_id, series_key, version, source_kind, production_id, snapshot_id, scope_snapshot_id, template_id,
                 template_name, template_definition, metadata, content_sha256, csv_sha256, csv_bytes, xlsx_sha256, xlsx_bytes, entry_count,
                 withheld_count, redacted_count, excluded_count, field_ids, generated_by, generated_by_display, generated_at)
            VALUES (@ws, @id, @series, @version, @source, @production, @snapshot, @scope, @template, @templateName, @definition, @metadata,
                    @content, @csv, @csvBytes, @xlsx, @xlsxBytes, @entries, @withheld, @redacted, @excluded, @fields, @by, @display, @at)
            """))
        {
            insert.Parameters.AddWithValue("ws", version.WorkspaceId);
            insert.Parameters.AddWithValue("id", version.LogId);
            insert.Parameters.AddWithValue("series", version.SeriesKey);
            insert.Parameters.AddWithValue("version", (latest?.Version ?? 0) + 1);
            insert.Parameters.AddWithValue("source", (short)version.Source);
            insert.Parameters.Add(new NpgsqlParameter("production", NpgsqlDbType.Uuid) { Value = (object?)version.ProductionId ?? DBNull.Value });
            insert.Parameters.AddWithValue("snapshot", version.SnapshotId);
            insert.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Uuid) { Value = (object?)version.ScopeSnapshotId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("template", NpgsqlDbType.Uuid) { Value = (object?)version.TemplateId ?? DBNull.Value });
            insert.Parameters.AddWithValue("templateName", version.TemplateName);
            insert.Parameters.AddWithValue("definition", version.TemplateDefinition);
            insert.Parameters.AddWithValue("metadata", version.Metadata);
            insert.Parameters.AddWithValue("content", version.ContentSha256);
            insert.Parameters.AddWithValue("csv", version.CsvSha256);
            insert.Parameters.AddWithValue("csvBytes", version.CsvBytes);
            insert.Parameters.AddWithValue("xlsx", version.XlsxSha256);
            insert.Parameters.AddWithValue("xlsxBytes", version.XlsxBytes);
            insert.Parameters.AddWithValue("entries", version.Entries.Count);
            insert.Parameters.AddWithValue("withheld", version.Withheld);
            insert.Parameters.AddWithValue("redacted", version.Redacted);
            insert.Parameters.AddWithValue("excluded", version.Excluded);
            insert.Parameters.AddWithValue("fields", version.FieldIds.ToArray());
            insert.Parameters.AddWithValue("by", version.GeneratedBy);
            insert.Parameters.AddWithValue("display", version.GeneratedByDisplay);
            insert.Parameters.AddWithValue("at", version.GeneratedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var batch in version.Entries.Chunk(InsertBatch))
        {
            await using var entries = tx.Command(
                """
                INSERT INTO opportunity.privilege_log_entry (workspace_id, log_id, ordinal, document_id, treatment, cells)
                SELECT @ws, @id, e.ordinal, e.document_id, e.treatment, (string_to_array(e.cells, @separator))[2:]
                  FROM unnest(@ordinals, @documents, @treatments, @cells) AS e(ordinal, document_id, treatment, cells)
                """);
            entries.Parameters.AddWithValue("ws", version.WorkspaceId);
            entries.Parameters.AddWithValue("id", version.LogId);
            entries.Parameters.AddWithValue("ordinals", batch.Select(e => e.Ordinal).ToArray());
            entries.Parameters.AddWithValue("documents", batch.Select(e => e.DocumentId).ToArray());
            entries.Parameters.AddWithValue("treatments", batch.Select(e => (short)e.Treatment).ToArray());
            // Cells travel joined by a separator no cell can hold (control characters are removed when cells are built), each
            // preceded by it so a row of one empty cell keeps its cell.
            entries.Parameters.AddWithValue("cells", batch.Select(e => CellSeparator + string.Join(CellSeparator, e.Cells)).ToArray());
            entries.Parameters.AddWithValue("separator", CellSeparator);
            await entries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, audit with { ResourceId = version.LogId.ToString() }, cancellationToken).ConfigureAwait(false);
        var stored = (await ReadLogsAsync(tx, $"SELECT {LogColumns} FROM opportunity.privilege_log WHERE workspace_id = @ws AND log_id = @id",
            p => p.AddWithValue("id", version.LogId), cancellationToken).ConfigureAwait(false)).Single();
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PrivilegeLogCreation(stored, Unchanged: false);
    }

    /// <summary>Joins a row's cells for the bulk insert; cells never hold control characters.</summary>
    public const string CellSeparator = "\u001f";

    public async Task<PrivilegeLogRecord?> GetAsync(Guid workspaceId, Guid logId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var list = await ReadLogsAsync(tx, $"SELECT {LogColumns} FROM opportunity.privilege_log WHERE workspace_id = @ws AND log_id = @id",
            p => p.AddWithValue("id", logId), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list.SingleOrDefault();
    }

    public async Task<IReadOnlyList<PrivilegeLogRecord>> ListAsync(
        Guid workspaceId, Guid? productionId, Guid? snapshotId, (DateTimeOffset At, Guid LogId)? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var list = await ReadLogsAsync(tx,
            $"""
            SELECT {LogColumns} FROM opportunity.privilege_log
             WHERE workspace_id = @ws
               AND (@production::uuid IS NULL OR production_id = @production)
               AND (@snapshot::uuid IS NULL OR snapshot_id = @snapshot OR scope_snapshot_id = @snapshot)
               AND (@afterAt::timestamptz IS NULL OR (generated_at, log_id) < (@afterAt, @afterId))
             ORDER BY generated_at DESC, log_id DESC
             LIMIT @limit
            """,
            p =>
            {
                p.Add(new NpgsqlParameter("production", NpgsqlDbType.Uuid) { Value = (object?)productionId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Uuid) { Value = (object?)snapshotId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("afterAt", NpgsqlDbType.TimestampTz) { Value = after is { } a ? a.At : DBNull.Value });
                p.Add(new NpgsqlParameter("afterId", NpgsqlDbType.Uuid) { Value = after is { } b ? b.LogId : Guid.Empty });
                p.AddWithValue("limit", limit);
            }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return list;
    }

    public async Task<IReadOnlyList<PrivilegeLogEntryRow>> ReadEntriesAsync(
        Guid workspaceId, Guid logId, int afterOrdinal, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var rows = new List<PrivilegeLogEntryRow>();
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            """
            SELECT ordinal, document_id, treatment, cells FROM opportunity.privilege_log_entry
             WHERE workspace_id = @ws AND log_id = @id AND ordinal > @after
             ORDER BY ordinal
             LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", logId);
            command.Parameters.AddWithValue("after", afterOrdinal);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new PrivilegeLogEntryRow(reader.GetInt32(0), reader.GetGuid(1), (PrivilegeLogEntryTreatment)reader.GetInt16(2),
                    reader.GetFieldValue<string[]>(3)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    public async Task<IReadOnlyList<Guid>> ReadDocumentIdsAsync(Guid workspaceId, Guid logId, CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>();
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var command = tx.Command(
            "SELECT document_id FROM opportunity.privilege_log_entry WHERE workspace_id = @ws AND log_id = @id ORDER BY ordinal"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("id", logId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    /// <summary>The distinct reasons of each member's active redactions as of its frozen version in its frozen set.</summary>
    private static async Task<Dictionary<Guid, List<PrivilegeLogRedactionReason>>> ReadReasonsAsync(
        WorkspaceTransaction tx, List<(Guid DocumentId, Guid SetId, long Version)> wanted, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<PrivilegeLogRedactionReason>>();
        await using var command = tx.Command(
            """
            WITH wanted AS (
                SELECT * FROM unnest(@documents, @sets, @versions) AS w(document_id, redaction_set_id, version)
            ), latest AS (
                SELECT DISTINCT ON (r.document_id, r.redaction_id) r.document_id, r.operation, r.reason_code
                  FROM opportunity.redaction_revision r
                  JOIN wanted w ON w.document_id = r.document_id AND w.redaction_set_id = r.redaction_set_id
                 WHERE r.workspace_id = @ws AND r.redaction_version <= w.version
                 ORDER BY r.document_id, r.redaction_id, r.redaction_version DESC
            )
            SELECT DISTINCT l.document_id, rr.code, rr.name, rr.category, rr.sort_order
              FROM latest l
              JOIN opportunity.redaction_reason rr ON rr.workspace_id = @ws AND rr.code = l.reason_code
             WHERE l.operation <> 3
             ORDER BY l.document_id, rr.sort_order, rr.name, rr.code
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("documents", wanted.Select(w => w.DocumentId).ToArray());
        command.Parameters.AddWithValue("sets", wanted.Select(w => w.SetId).ToArray());
        command.Parameters.AddWithValue("versions", wanted.Select(w => w.Version).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            if (!result.TryGetValue(id, out var list))
            {
                result[id] = list = [];
            }

            list.Add(new PrivilegeLogRedactionReason(reader.GetString(1), reader.GetString(2), (RedactionReasonCategory)reader.GetInt16(3)));
        }

        return result;
    }

    private static async Task<List<PrivilegeLogTemplateRecord>> ReadTemplatesAsync(
        WorkspaceTransaction tx, string sql, Guid? templateId, CancellationToken cancellationToken)
    {
        var list = new List<PrivilegeLogTemplateRecord>();
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        if (templateId is { } id)
        {
            command.Parameters.AddWithValue("id", id);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new PrivilegeLogTemplateRecord(tx.WorkspaceId, reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetGuid(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return list;
    }

    private static async Task<List<PrivilegeLogRecord>> ReadLogsAsync(
        WorkspaceTransaction tx, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken cancellationToken)
    {
        var list = new List<PrivilegeLogRecord>();
        await using var command = tx.Command(sql);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        bind(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new PrivilegeLogRecord
            {
                WorkspaceId = tx.WorkspaceId,
                LogId = reader.GetGuid(0),
                SeriesKey = reader.GetString(1),
                Version = reader.GetInt32(2),
                Source = (PrivilegeLogSource)reader.GetInt16(3),
                ProductionId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                SnapshotId = reader.GetGuid(5),
                ScopeSnapshotId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                TemplateId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
                TemplateName = reader.GetString(8),
                TemplateDefinition = reader.GetString(9),
                Metadata = reader.GetString(10),
                ContentSha256 = reader.GetFieldValue<byte[]>(11),
                CsvSha256 = reader.GetFieldValue<byte[]>(12),
                CsvBytes = reader.GetInt64(13),
                XlsxSha256 = reader.GetFieldValue<byte[]>(14),
                XlsxBytes = reader.GetInt64(15),
                EntryCount = reader.GetInt32(16),
                Withheld = reader.GetInt32(17),
                Redacted = reader.GetInt32(18),
                Excluded = reader.GetInt32(19),
                FieldIds = reader.GetFieldValue<int[]>(20),
                GeneratedBy = reader.GetGuid(21),
                GeneratedByDisplay = reader.GetString(22),
                GeneratedAt = reader.GetFieldValue<DateTimeOffset>(23),
            });
        }

        return list;
    }

    /// <summary>Runs a statement inside a savepoint; false when it violated a unique constraint (the name).</summary>
    private static async Task<bool> TryExecuteAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await command.Transaction!.SaveAsync("unique_name", cancellationToken).ConfigureAwait(false);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await command.Transaction.ReleaseAsync("unique_name", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await command.Transaction.RollbackAsync("unique_name", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Candidate reads. Columns: document, control number, family, family sequence, Privilege Status key, member sequence,
    /// output, ProdBegBates, ProdEndBates, frozen redaction count, frozen Redaction Set, frozen redaction version.
    /// </summary>
    private static class CandidateSql
    {
        // Privilege Status by built-in key, from the coding adapter (only it names the coding tables).
        private const string Status = "status AS (" + CodingRepository.BuiltInChoiceKeysSql + ")";

        /// <summary>
        /// A finalized production: members with frozen redactions or a placeholder; and documents coded Withhold that were
        /// not produced, from the members' families and the review set. In production order; a withheld family member
        /// follows its family's produced members; the others come last, by family.
        /// </summary>
        public const string Production =
            $"""
            WITH member AS (
                SELECT pd.document_id, pd.sequence, pd.output, pd.prod_beg_bates, pd.prod_end_bates, pd.redaction_count,
                       pd.redaction_set_id, pd.redaction_version
                  FROM opportunity.production_document pd
                 WHERE pd.workspace_id = @ws AND pd.production_id = @production
            ), member_family AS (
                SELECT d.family_id, min(m.sequence) AS anchor
                  FROM member m
                  JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = m.document_id
                 GROUP BY d.family_id
            ), {Status}, outside AS (
                SELECT d.document_id
                  FROM opportunity.document d
                  JOIN member_family f ON f.family_id = d.family_id
                 WHERE d.workspace_id = @ws
                UNION
                SELECT u.document_id
                  FROM opportunity.document_set_snapshot_page p
                 CROSS JOIN LATERAL unnest(p.document_ids) AS u(document_id)
                 WHERE p.workspace_id = @ws AND p.snapshot_id = @scope
            ), candidate AS (
                SELECT m.document_id FROM member m
                 WHERE m.output = 3 OR coalesce(m.redaction_count, 0) > 0
                UNION
                SELECT o.document_id
                  FROM outside o
                  JOIN status s ON s.document_id = o.document_id AND s.system_key = @withhold
                 WHERE NOT EXISTS (SELECT FROM member m WHERE m.document_id = o.document_id)
            )
            SELECT d.document_id, d.control_number, d.family_id, d.family_sequence, s.system_key, m.sequence, m.output, m.prod_beg_bates,
                   m.prod_end_bates, m.redaction_count, m.redaction_set_id, m.redaction_version
              FROM candidate c
              JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = c.document_id
              JOIN opportunity.document root ON root.workspace_id = @ws AND root.document_id = d.family_id
              LEFT JOIN member m ON m.document_id = d.document_id
              LEFT JOIN member_family f ON f.family_id = d.family_id
              LEFT JOIN status s ON s.document_id = d.document_id
             WHERE m.document_id IS NOT NULL OR EXISTS (
                   SELECT FROM opportunity.document_projection_state ps
                    WHERE ps.workspace_id = @ws AND ps.document_id = d.document_id AND NOT ps.is_deleted)
             ORDER BY f.anchor NULLS LAST, root.control_number_sort_key, d.family_id, d.family_sequence, d.document_id
             LIMIT @limit
            """;

        /// <summary>A frozen set: its live members coded Withhold or Redact, in frozen-set order.</summary>
        public const string Snapshot =
            $"""
            WITH {Status}, member AS (
                SELECT u.document_id, p.first_ordinal + u.n - 1 AS ordinal
                  FROM opportunity.document_set_snapshot_page p
                 CROSS JOIN LATERAL unnest(p.document_ids) WITH ORDINALITY AS u(document_id, n)
                 WHERE p.workspace_id = @ws AND p.snapshot_id = @snapshot
            )
            SELECT d.document_id, d.control_number, d.family_id, d.family_sequence, s.system_key, NULL::bigint, NULL::smallint, NULL::text,
                   NULL::text, NULL::integer, NULL::uuid, NULL::bigint
              FROM member m
              JOIN status s ON s.document_id = m.document_id AND s.system_key IN (@withhold, @redact)
              JOIN opportunity.document d ON d.workspace_id = @ws AND d.document_id = m.document_id
              JOIN opportunity.document_projection_state ps ON ps.workspace_id = @ws AND ps.document_id = d.document_id AND NOT ps.is_deleted
             WHERE @production::uuid IS NULL AND @scope::uuid IS NULL
             ORDER BY m.ordinal
             LIMIT @limit
            """;
    }
}

public static class PrivilegeLogStoreRegistration
{
    /// <summary>Registers the PostgreSQL <see cref="IPrivilegeLogStore"/> (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresPrivilegeLogStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPrivilegeLogStore, PrivilegeLogRepository>();
        return services;
    }
}
