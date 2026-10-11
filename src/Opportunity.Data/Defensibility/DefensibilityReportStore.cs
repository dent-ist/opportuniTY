using System.Data;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Defensibility;
using Opportunity.Application.Search.TermReports;
using Opportunity.Core.Pages;
using Opportunity.Core.Storage;
using Opportunity.Data.Search;

namespace Opportunity.Data.Defensibility;

/// <summary>
/// PostgreSQL reads for the defensibility reports (E14-T05). Each report reads in one REPEATABLE READ transaction inside
/// the workspace's RLS context and only rows stamped at or before the as-of point: imports,
/// stored objects, page sets and overlays by their creation time, productions by finalization (a later void is not
/// applied), volume runs and QC runs by completion. Search term reports are read as their current run stored them.
/// </summary>
public sealed class DefensibilityReportStore(NpgsqlDataSource dataSource) : IDefensibilityReportStore
{
    public async Task<DefensibilitySnapshot?> GetSnapshotAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT snapshot_id, name, purpose, document_count, materialized_at, root_sha256
              FROM opportunity.document_set_snapshot
             WHERE workspace_id = @ws AND snapshot_id = @id AND status = 'Ready'
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("id", snapshotId);
        DefensibilitySnapshot? snapshot = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                snapshot = new DefensibilitySnapshot(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(5)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<IReadOnlyList<ProductionHistoryEntry>> ReadProductionHistoryAsync(
        Guid workspaceId, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        var productions = new List<ProductionHistoryEntry>();
        await using (var command = tx.Command(
            """
            SELECT p.production_id, p.lineage_id, p.version, p.name, p.snapshot_id, p.finalized_at, p.finalized_by,
                   (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = p.finalized_by),
                   CASE WHEN p.voided_at <= @asOf THEN p.voided_at END, CASE WHEN p.voided_at <= @asOf THEN p.void_reason END,
                   (SELECT pd.prod_beg_bates FROM opportunity.production_document pd
                     WHERE pd.workspace_id = p.workspace_id AND pd.production_id = p.production_id AND pd.beg_number IS NOT NULL
                     ORDER BY pd.sequence LIMIT 1),
                   (SELECT pd.prod_end_bates FROM opportunity.production_document pd
                     WHERE pd.workspace_id = p.workspace_id AND pd.production_id = p.production_id AND pd.beg_number IS NOT NULL
                     ORDER BY pd.sequence DESC LIMIT 1),
                   coalesce(p.bates_documents, 0), coalesce(p.bates_units, 0), p.specification_sha256, p.assignments_sha256, p.manifest,
                   p.manifest_sha256
              FROM opportunity.production p
             WHERE p.workspace_id = @ws AND p.status IN (2, 3) AND p.finalized_at <= @asOf
             ORDER BY p.finalized_at, p.production_id
            """))
        {
            Bind(command, workspaceId, asOf, null);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                productions.Add(new ProductionHistoryEntry(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetGuid(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetInt64(12), reader.GetInt64(13), reader.GetFieldValue<byte[]>(14),
                    reader.IsDBNull(15) ? null : reader.GetFieldValue<byte[]>(15), reader.GetString(16), reader.GetFieldValue<byte[]>(17),
                    null, []));
            }
        }

        if (productions.Count == 0)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return productions;
        }

        var ids = productions.Select(p => p.ProductionId).ToArray();
        var qc = new Dictionary<Guid, ProductionQcHistory>();
        await using (var command = tx.Command(
            """
            SELECT DISTINCT ON (production_id) production_id, qc_run_id, run_at, outcome, report, report_sha256
              FROM opportunity.production_qc_run
             WHERE workspace_id = @ws AND production_id = ANY(@ids) AND purpose = 2 AND run_at <= @asOf
             ORDER BY production_id, outcome, run_at DESC, qc_run_id DESC
            """))
        {
            Bind(command, workspaceId, asOf, null);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var report = reader.GetString(4);
                var (acknowledged, checks) = QcChecks(report);
                qc[reader.GetGuid(0)] = new ProductionQcHistory(reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetInt16(3) == 1 ? "Passed" : "Blocked", acknowledged, report, reader.GetFieldValue<byte[]>(5), checks);
            }
        }

        var volumes = new Dictionary<Guid, List<ProductionVolumeHistory>>();
        await using (var command = tx.Command(
            """
            SELECT production_id, export_id, settings::text, completed_at, coalesce(documents_exported, 0), coalesce(pages, 0),
                   coalesce(file_count, 0), coalesce(total_bytes, 0), manifest_sha256, verification_status, verification_documents,
                   verification_report_sha256
              FROM opportunity.export
             WHERE workspace_id = @ws AND production_id = ANY(@ids) AND status = 2 AND completed_at <= @asOf
             ORDER BY completed_at, export_id
            """))
        {
            Bind(command, workspaceId, asOf, null);
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var production = reader.GetGuid(0);
                if (!volumes.TryGetValue(production, out var list))
                {
                    volumes[production] = list = [];
                }

                list.Add(new ProductionVolumeHistory(reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetFieldValue<byte[]>(8),
                    reader.IsDBNull(9) ? null : reader.GetInt16(9) == 1, reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. productions.Select(p => p with
        {
            Qc = qc.GetValueOrDefault(p.ProductionId),
            Volumes = volumes.TryGetValue(p.ProductionId, out var v) ? v : [],
        })];
    }

    public async Task<IReadOnlyList<TermHitReportEntry>> ReadTermHitReportsAsync(
        Guid workspaceId, DateTimeOffset asOf, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var reports = new List<SearchTermReportRecord>();
        await using (var command = tx.Command(
            $"""
            SELECT {SearchTermReportStore.Columns}
              FROM opportunity.search_term_report r
             WHERE r.workspace_id = @ws AND r.status = 'Completed' AND r.completed_at <= @asOf
             ORDER BY r.completed_at, r.report_id
             LIMIT @limit
            """))
        {
            Bind(command, workspaceId, asOf, null);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reports.Add(SearchTermReportStore.Read(reader));
            }
        }

        var terms = new Dictionary<Guid, List<SearchTermRecord>>();
        if (reports.Count > 0)
        {
            await using var command = tx.Command(
                """
                SELECT term_no, term_id, name, expression, error_code, error_message, error_position, documents_with_hits,
                       documents_with_hits_family, unique_hits, unique_hits_family, report_id
                  FROM opportunity.search_term_report_term
                 WHERE workspace_id = @ws AND report_id = ANY(@ids)
                 ORDER BY report_id, term_no
                """);
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("ids", reports.Select(r => r.ReportId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var report = reader.GetGuid(11);
                if (!terms.TryGetValue(report, out var list))
                {
                    terms[report] = list = [];
                }

                list.Add(SearchTermReportStore.ReadTerm(reader, 0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. reports.Select(r => new TermHitReportEntry(r, terms.TryGetValue(r.ReportId, out var t) ? t : []))];
    }

    public async Task<ChainOfCustodyData?> ReadChainOfCustodyAsync(
        Guid workspaceId, Guid documentId, DateTimeOffset asOf, int maxObjects, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        string controlNumber;
        DateTimeOffset createdAt;
        byte[]? md5, sha1, sha256;
        string? upstream, fileName;
        long? fileSize;
        Guid? native, text, activePageSet;
        await using (var command = tx.Command(
            """
            SELECT control_number, created_at, md5, sha1, sha256, upstream_dedupe_hash, file_name, file_size, native_object_id,
                   text_object_id, active_page_set_id
              FROM opportunity.document
             WHERE workspace_id = @ws AND document_id = @doc
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("doc", documentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            controlNumber = reader.GetString(0);
            createdAt = reader.GetFieldValue<DateTimeOffset>(1);
            md5 = Bytes(reader, 2);
            sha1 = Bytes(reader, 3);
            sha256 = Bytes(reader, 4);
            upstream = reader.IsDBNull(5) ? null : reader.GetString(5);
            fileName = reader.IsDBNull(6) ? null : reader.GetString(6);
            fileSize = reader.IsDBNull(7) ? null : reader.GetInt64(7);
            native = reader.IsDBNull(8) ? null : reader.GetGuid(8);
            text = reader.IsDBNull(9) ? null : reader.GetGuid(9);
            activePageSet = reader.IsDBNull(10) ? null : reader.GetGuid(10);
        }

        var imports = new List<CustodyImport>();
        await using (var command = tx.Command(
            """
            SELECT b.import_batch_id, b.job_id, b.name, b.mode, b.source_file_name, b.source_object_key, b.source_sha256, b.source_size,
                   m.row_no, m.action, b.created_by, (SELECT coalesce(u.display_name, u.email) FROM opportunity.app_user u WHERE u.user_id = b.created_by),
                   b.created_at, CASE WHEN b.completed_at <= @asOf THEN b.completed_at END
              FROM opportunity.import_batch_member m
              JOIN opportunity.import_batch b ON b.workspace_id = m.workspace_id AND b.import_batch_id = m.import_batch_id
             WHERE m.workspace_id = @ws AND m.document_id = @doc AND b.created_at <= @asOf
             ORDER BY b.created_at, b.import_batch_id, m.row_no
            """))
        {
            BindDocument(command, workspaceId, documentId, asOf);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                imports.Add(new CustodyImport(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetInt16(3) switch { 1 => "Append", 2 => "Overlay", _ => "AppendOverlay" },
                    reader.GetString(4), reader.GetString(5), reader.GetFieldValue<byte[]>(6), reader.GetInt64(7), reader.GetInt64(8),
                    reader.GetInt16(9) switch { 1 => "Imported", 2 => "Overlaid", _ => "Skipped" },
                    reader.GetGuid(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetFieldValue<DateTimeOffset>(12),
                    reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)));
            }
        }

        var objects = new List<CustodyObject>();
        await using (var command = tx.Command(
            """
            SELECT o.object_id, o.area, o.logical_key, o.sha256, o.size_bytes, o.content_type, o.state, o.created_at, o.created_by_job_id,
                   pi.page_set_id, pi.ordinal
              FROM opportunity.stored_object o
              LEFT JOIN LATERAL (
                  SELECT i.page_set_id, i.ordinal FROM opportunity.page_image i
                   WHERE i.workspace_id = o.workspace_id AND i.object_id = o.object_id
                   ORDER BY i.page_set_id, i.ordinal LIMIT 1) pi ON true
             WHERE o.workspace_id = @ws AND o.document_id = @doc AND o.created_at <= @asOf
             ORDER BY CASE o.object_id WHEN @native THEN 0 WHEN @text THEN 1 ELSE 2 END, o.area, o.created_at, pi.page_set_id, pi.ordinal,
                      o.object_id
             LIMIT @limit
            """))
        {
            BindDocument(command, workspaceId, documentId, asOf);
            command.Parameters.AddWithValue("native", native ?? Guid.Empty);
            command.Parameters.AddWithValue("text", text ?? Guid.Empty);
            command.Parameters.AddWithValue("limit", maxObjects + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                var pageSet = reader.IsDBNull(9) ? (Guid?)null : reader.GetGuid(9);
                objects.Add(new CustodyObject(
                    id, (ObjectArea)reader.GetInt16(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3), reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), (StoredObjectState)reader.GetInt16(6), reader.GetFieldValue<DateTimeOffset>(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    id == native || id == text || (pageSet is not null && pageSet == activePageSet),
                    pageSet, reader.IsDBNull(10) ? null : reader.GetInt32(10)));
            }
        }

        var objectsTruncated = objects.Count > maxObjects;
        if (objectsTruncated)
        {
            objects.RemoveAt(objects.Count - 1);
        }

        var pageSets = new List<CustodyPageSet>();
        await using (var command = tx.Command(
            """
            SELECT page_set_id, source, import_job_id, renderer_name, renderer_version, render_settings_hash, page_count, status, created_at
              FROM opportunity.page_set
             WHERE workspace_id = @ws AND document_id = @doc AND created_at <= @asOf
             ORDER BY created_at, page_set_id
            """))
        {
            BindDocument(command, workspaceId, documentId, asOf);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                pageSets.Add(new CustodyPageSet(id, ((PageSetSource)reader.GetInt16(1)).ToString(), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), Bytes(reader, 5),
                    reader.GetInt32(6), ((PageSetStatus)reader.GetInt16(7)).ToString(), reader.GetFieldValue<DateTimeOffset>(8), id == activePageSet));
            }
        }

        var overlays = new List<CustodyOverlay>();
        await using (var command = tx.Command(
            """
            SELECT import_batch_id, row_no, document_version, occurred_at, ARRAY(SELECT jsonb_object_keys(changes) ORDER BY 1)
              FROM opportunity.document_overlay_event
             WHERE workspace_id = @ws AND document_id = @doc AND occurred_at <= @asOf
             ORDER BY occurred_at, document_version
            """))
        {
            BindDocument(command, workspaceId, documentId, asOf);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                overlays.Add(new CustodyOverlay(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<string[]>(4)));
            }
        }

        var productions = new List<CustodyProduction>();
        await using (var command = tx.Command(
            """
            SELECT p.production_id, p.name, p.version, p.finalized_at, CASE WHEN p.voided_at <= @asOf THEN p.voided_at END, pd.output,
                   pd.prod_beg_bates, pd.prod_end_bates,
                   ARRAY(SELECT e.export_id FROM opportunity.export e
                          WHERE e.workspace_id = p.workspace_id AND e.production_id = p.production_id AND e.status = 2 AND e.completed_at <= @asOf
                          ORDER BY e.completed_at, e.export_id)
              FROM opportunity.production_document pd
              JOIN opportunity.production p ON p.workspace_id = pd.workspace_id AND p.production_id = pd.production_id
             WHERE pd.workspace_id = @ws AND pd.document_id = @doc AND p.status IN (2, 3) AND p.finalized_at <= @asOf
             ORDER BY p.finalized_at, p.production_id
            """))
        {
            BindDocument(command, workspaceId, documentId, asOf);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                productions.Add(new CustodyProduction(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetInt16(5) switch { 2 => "Native", 3 => "Placeholder", _ => "Image" },
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetFieldValue<Guid[]>(8)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChainOfCustodyData(documentId, controlNumber, createdAt, md5, sha1, sha256, upstream, fileName, fileSize, imports, objects,
            objectsTruncated, pageSets, overlays, productions);
    }

    public async Task<long> ReadSearchGenerationAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT value FROM opportunity.workspace_search_generation WHERE workspace_id = @ws");
        command.Parameters.AddWithValue("ws", workspaceId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value is long generation ? generation : 0;
    }

    private static (bool Acknowledged, IReadOnlyList<ProductionQcCheckHistory> Checks) QcChecks(string report)
    {
        try
        {
            using var json = JsonDocument.Parse(report);
            var root = json.RootElement;
            var acknowledged = root.TryGetProperty("warningsAcknowledged", out var a) && a.ValueKind == JsonValueKind.True;
            var checks = new List<ProductionQcCheckHistory>();
            if (root.TryGetProperty("checks", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in list.EnumerateArray())
                {
                    checks.Add(new ProductionQcCheckHistory(
                        check.TryGetProperty("check", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                        check.TryGetProperty("severity", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                        check.TryGetProperty("status", out var st) ? st.GetString() ?? string.Empty : string.Empty,
                        check.TryGetProperty("documents", out var d) && d.TryGetInt64(out var n) ? n : 0));
                }
            }

            return (acknowledged, checks);
        }
        catch (JsonException)
        {
            return (false, []);
        }
    }

    private static byte[]? Bytes(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<byte[]>(ordinal);

    private static void Bind(NpgsqlCommand command, Guid workspaceId, DateTimeOffset asOf, Guid? snapshotId)
    {
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("asOf", asOf);
        if (snapshotId is { } snapshot)
        {
            command.Parameters.AddWithValue("snapshot", snapshot);
        }
    }

    private static void BindDocument(NpgsqlCommand command, Guid workspaceId, Guid documentId, DateTimeOffset asOf)
    {
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("doc", documentId);
        command.Parameters.AddWithValue("asOf", asOf);
    }
}

public static class DefensibilityReportStoreRegistration
{
    public static IServiceCollection AddPostgresDefensibilityReports(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDefensibilityReportStore, DefensibilityReportStore>();
        return services;
    }
}
