using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Data.Jobs;

namespace Opportunity.Data.Audit;

/// <summary>
/// <see cref="IAuditLogStore"/> on PostgreSQL (V0062): audit exports with their jobs and CSV parts, and the reads the
/// viewer and the export runner need from <c>audit.audit_event</c> in the workspace's own RLS context.
/// </summary>
public sealed class PostgresAuditLogStore(NpgsqlDataSource dataSource) : IAuditLogStore
{
    private const string ExportColumns =
        """
        x.workspace_id, x.export_id, x.job_id, x.filters::text, x.include_search_text, x.occurred_before, x.status, x.status_reason,
        x.created_by, x.created_by_display, x.created_by_groups, x.created_at, x.completed_at, x.row_count,
        x.byte_count, x.content_sha256
        """;

    public async Task<Guid?> FindDocumentAsync(
        Guid workspaceId, string? controlNumber, Guid? documentId, AuditDocumentVisibility visibility, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        var forms = controlNumber is null ? null : AuditControlNumber.Normalize(controlNumber);
        if (visibility.HideAllDocuments || (controlNumber is null && documentId is null) || (controlNumber is not null && forms is null))
        {
            return null;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        Guid? found = null;
        await using (var command = tx.Command(
            """
            SELECT d.document_id
            FROM opportunity.document d
            JOIN opportunity.workspace w ON w.workspace_id = d.workspace_id
            WHERE d.workspace_id = @ws
              AND (@id::uuid IS NULL OR d.document_id = @id)
              AND (@sensitive::text IS NULL
                   OR d.control_number_norm = normalize(CASE WHEN w.control_number_case_sensitive THEN @sensitive ELSE @insensitive END, NFC))
              AND NOT EXISTS (SELECT FROM opportunity.document_restriction r
                              WHERE r.workspace_id = d.workspace_id AND r.document_id = d.document_id AND r.class_key = ANY (@denied))
              AND NOT EXISTS (SELECT FROM opportunity.document_wall x
                              WHERE x.workspace_id = d.workspace_id AND x.document_id = d.document_id AND x.wall_id = ANY (@walls))
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)documentId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("sensitive", NpgsqlDbType.Text) { Value = (object?)forms?.Sensitive ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("insensitive", NpgsqlDbType.Text) { Value = (object?)forms?.Insensitive ?? DBNull.Value });
            command.Parameters.AddWithValue("denied", visibility.DeniedClasses.ToArray());
            command.Parameters.AddWithValue("walls", visibility.WallIds.ToArray());
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id)
            {
                found = id;
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found;
    }

    public async Task<AuditExportCreation> CreateAsync(NewAuditExport request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var job = await JobRepository.CreateInTransactionAsync(tx, new NewJob
        {
            WorkspaceId = request.WorkspaceId,
            JobId = request.JobId,
            JobType = JobType.AuditExport,
            InitiatedBy = request.CreatedBy,
            Parameters = new JsonObject { ["auditExportId"] = request.ExportId.ToString() },
            ClientIdempotencyKey = request.ClientIdempotencyKey,
            CorrelationId = request.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);

        if (!job.Created)
        {
            var existing = await ReadOneAsync(tx, "x.job_id = @id", job.Job.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Job {job.Job.JobId} is not an audit request.");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AuditExportCreation(existing, job.Job, false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.audit_export (workspace_id, export_id, job_id, filters, include_search_text, occurred_before,
                created_by, created_by_display, created_by_groups)
            VALUES (@ws, @id, @job, @filters::jsonb, @search_text, @before, @by, @display, @groups)
            """))
        {
            insert.Parameters.AddWithValue("ws", request.WorkspaceId);
            insert.Parameters.AddWithValue("id", request.ExportId);
            insert.Parameters.AddWithValue("job", request.JobId);
            insert.Parameters.AddWithValue("filters", request.Filters.ToJson().ToJsonString());
            insert.Parameters.AddWithValue("search_text", request.IncludeSearchText);
            insert.Parameters.AddWithValue("before", request.OccurredBefore.ToUniversalTime());
            insert.Parameters.AddWithValue("by", request.CreatedBy);
            insert.Parameters.AddWithValue("display", request.CreatedByDisplay.Length > 512 ? request.CreatedByDisplay[..512] : request.CreatedByDisplay);
            insert.Parameters.AddWithValue("groups", request.CreatedByGroups.ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, request.Requested with
        {
            EventId = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId,
            ResourceType = AuditTaxonomy.Audit.ExportResourceType,
            ResourceId = request.ExportId.ToString(),
            JobId = request.JobId,
        }, cancellationToken).ConfigureAwait(false);

        var record = (await ReadOneAsync(tx, "x.export_id = @id", request.ExportId, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AuditExportCreation(record, job.Job, true);
    }

    public async Task<AuditExportRecord?> GetAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await ReadOneAsync(tx, "x.export_id = @id", exportId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<AuditExportRecord>> ListAsync(
        Guid workspaceId, Guid createdBy, (DateTimeOffset CreatedAt, Guid ExportId)? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var records = new List<AuditExportRecord>();
        await using (var command = tx.Command(
            $"""
            SELECT {ExportColumns} FROM opportunity.audit_export x
            WHERE x.workspace_id = @ws AND x.created_by = @by
              {(after is null ? string.Empty : "AND (x.created_at, x.export_id) < (@after_at, @after_id)")}
            ORDER BY x.created_at DESC, x.export_id DESC
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("by", createdBy);
            command.Parameters.AddWithValue("limit", limit);
            if (after is { } a)
            {
                command.Parameters.AddWithValue("after_at", a.CreatedAt.ToUniversalTime());
                command.Parameters.AddWithValue("after_id", a.ExportId);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(Read(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async Task<IReadOnlyList<Guid>> GetActiveAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var ids = new List<Guid>();
        await using (var command = tx.Command(
            """
            SELECT export_id FROM opportunity.audit_export
            WHERE workspace_id = @ws AND status IN ('Queued', 'Running') ORDER BY created_at LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public async Task<bool> TryClaimAsync(Guid workspaceId, Guid exportId, string owner, TimeSpan lease, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.audit_export SET claimed_by = @owner, claimed_until = now() + @lease
            WHERE workspace_id = @ws AND export_id = @id AND status IN ('Queued', 'Running')
              AND (claimed_by IS NULL OR claimed_by = @owner OR claimed_until < now())
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("owner", owner);
                c.Parameters.AddWithValue("lease", lease);
            }, cancellationToken).ConfigureAwait(false) == 1;

    public Task ReleaseClaimAsync(Guid workspaceId, Guid exportId, string owner, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.audit_export SET claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND export_id = @id AND claimed_by = @owner
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("owner", owner);
            }, cancellationToken);

    public async Task<IReadOnlyList<AuditExportWindow>?> PlanAsync(
        AuditQuery query, int maxPerWindow, long maxEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerWindow, 1);
        var workspaceId = query.WorkspaceId ?? throw new ArgumentException("An export reads one workspace's audit trail.", nameof(query));
        var windows = new List<AuditExportWindow>();
        long total = 0;
        long? start = null;
        long last = 0;
        var count = 0;
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await AuditSql.ForEachTimeAsync(tx, query, maxEvents, micros =>
        {
            total++;
            // A window closes once full, but never inside one microsecond: its bounds are times, not positions.
            if (start is not null && count >= maxPerWindow && micros != last)
            {
                windows.Add(new AuditExportWindow(start.Value, last, count));
                start = null;
                count = 0;
            }

            start ??= micros;
            last = micros;
            count++;
        }, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (total > maxEvents)
        {
            return null;
        }

        if (start is not null)
        {
            windows.Add(new AuditExportWindow(start.Value, last, count));
        }

        return windows;
    }

    public async Task<long> ReadWindowAsync(
        AuditQuery query, AuditExportWindow window, Func<StoredAuditEvent, ValueTask> onEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(window);
        var workspaceId = query.WorkspaceId ?? throw new ArgumentException("An export reads one workspace's audit trail.", nameof(query));
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        // Planned windows end before the export's bound, so the window alone selects (the bound stays out of the SQL).
        var count = await AuditSql.ForEachAsync(
            tx, query, AuditExportWindow.Time(window.FromMicros), AuditExportWindow.Time(window.ToMicros), onEvent, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public Task MarkRunningAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.audit_export SET status = 'Running'
            WHERE workspace_id = @ws AND export_id = @id AND status IN ('Queued', 'Running')
            """,
            c => c.Parameters.AddWithValue("id", exportId), cancellationToken);

    public Task WritePartAsync(
        Guid workspaceId, Guid exportId, int chunkSequence, int rows, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            INSERT INTO opportunity.audit_export_part (workspace_id, export_id, chunk_sequence, row_count, content)
            SELECT @ws, @id, @seq, @rows, @content
            WHERE EXISTS (SELECT FROM opportunity.audit_export x WHERE x.workspace_id = @ws AND x.export_id = @id AND x.status = 'Running')
            ON CONFLICT (workspace_id, export_id, chunk_sequence)
                DO UPDATE SET row_count = EXCLUDED.row_count, content = EXCLUDED.content, written_at = now()
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("seq", chunkSequence);
                c.Parameters.AddWithValue("rows", rows);
                c.Parameters.Add(new NpgsqlParameter("content", NpgsqlDbType.Bytea) { Value = content.ToArray() });
            }, cancellationToken);

    public async Task<long> ReadPartsAsync(
        Guid workspaceId, Guid exportId, Func<ReadOnlyMemory<byte>, ValueTask> onPart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onPart);
        long rows = 0;
        var after = 0;
        // One part per round trip, so a large export never sits in memory as a whole.
        while (true)
        {
            int sequence;
            byte[] content;
            await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false))
            {
                await using var command = tx.Command(
                    """
                    SELECT chunk_sequence, row_count, content FROM opportunity.audit_export_part
                    WHERE workspace_id = @ws AND export_id = @id AND chunk_sequence > @after
                    ORDER BY chunk_sequence LIMIT 1
                    """);
                command.Parameters.AddWithValue("ws", workspaceId);
                command.Parameters.AddWithValue("id", exportId);
                command.Parameters.AddWithValue("after", after);
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return rows;
                    }

                    sequence = reader.GetInt32(0);
                    rows += reader.GetInt32(1);
                    content = reader.GetFieldValue<byte[]>(2);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await onPart(content).ConfigureAwait(false);
            after = sequence;
        }
    }

    public async Task<bool> CompleteAsync(
        Guid workspaceId, Guid exportId, long rows, long bytes, string sha256, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.audit_export
            SET status = 'Completed', completed_at = now(), row_count = @rows, byte_count = @bytes, content_sha256 = @sha,
                claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND export_id = @id AND status = 'Running'
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("rows", rows);
                c.Parameters.AddWithValue("bytes", bytes);
                c.Parameters.AddWithValue("sha", sha256);
            }, cancellationToken).ConfigureAwait(false) == 1;

    public async Task<bool> FailAsync(Guid workspaceId, Guid exportId, string reason, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.audit_export
            SET status = 'Failed', completed_at = now(), status_reason = left(@reason, 2000), claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND export_id = @id AND status IN ('Queued', 'Running')
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("reason", reason);
            }, cancellationToken).ConfigureAwait(false) == 1;

    private async Task<int> ExecuteAsync(Guid workspaceId, string sql, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        int affected;
        await using (var command = tx.Command(sql))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            parameters(command);
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private static async Task<AuditExportRecord?> ReadOneAsync(WorkspaceTransaction tx, string where, Guid id, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {ExportColumns} FROM opportunity.audit_export x WHERE x.workspace_id = @ws AND {where}");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static AuditExportRecord Read(NpgsqlDataReader r) => new()
    {
        WorkspaceId = r.GetGuid(0),
        ExportId = r.GetGuid(1),
        JobId = r.GetGuid(2),
        Filters = AuditLogFilters.FromJson(JsonNode.Parse(r.GetString(3))!.AsObject()),
        IncludeSearchText = r.GetBoolean(4),
        OccurredBefore = r.GetFieldValue<DateTimeOffset>(5),
        Status = Enum.Parse<AuditExportStatus>(r.GetString(6)),
        StatusReason = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedBy = r.GetGuid(8),
        CreatedByDisplay = r.GetString(9),
        CreatedByGroups = r.GetFieldValue<string[]>(10),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(11),
        CompletedAt = r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        RowCount = r.IsDBNull(13) ? null : r.GetInt64(13),
        ByteCount = r.IsDBNull(14) ? null : r.GetInt64(14),
        ContentSha256 = r.IsDBNull(15) ? null : r.GetString(15),
    };
}

public static class AuditLogStoreRegistration
{
    /// <summary>The audit log viewer's and audit exports' store (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresAuditLogStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAuditLogStore>(sp => new PostgresAuditLogStore(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
