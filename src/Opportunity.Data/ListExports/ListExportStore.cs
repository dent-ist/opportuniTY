using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.ListExports;
using Opportunity.Core.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;

namespace Opportunity.Data.ListExports;

/// <summary>
/// <see cref="IListExportStore"/> on PostgreSQL (V0065): document list exports with their jobs and CSV parts, in the
/// workspace's own RLS context.
/// </summary>
public sealed class PostgresListExportStore(NpgsqlDataSource dataSource) : IListExportStore
{
    private const string Columns =
        """
        x.workspace_id, x.export_id, x.job_id, x.snapshot_id, x.name, x.columns::text, x.status, x.status_reason,
        x.created_by, x.created_by_display, x.created_by_groups, x.created_at, x.completed_at, x.row_count,
        x.excluded_count, x.byte_count, x.content_sha256,
        (SELECT s.document_count FROM opportunity.document_set_snapshot s WHERE s.workspace_id = x.workspace_id AND s.snapshot_id = x.snapshot_id)
        """;

    public async Task<ListExportCreation> CreateAsync(NewListExport request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var job = await JobRepository.CreateInTransactionAsync(tx, new NewJob
        {
            WorkspaceId = request.WorkspaceId,
            JobId = request.JobId,
            JobType = JobType.ListExport,
            InitiatedBy = request.CreatedBy,
            Parameters = new JsonObject { ["listExportId"] = request.ExportId.ToString(), ["snapshotId"] = request.SnapshotId.ToString() },
            ClientIdempotencyKey = request.ClientIdempotencyKey,
            CorrelationId = request.CorrelationId,
        }, cancellationToken).ConfigureAwait(false);

        if (!job.Created)
        {
            var existing = await ReadOneAsync(tx, "x.job_id = @id", job.Job.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Job {job.Job.JobId} is not a list export.");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ListExportCreation(existing, job.Job, false);
        }

        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.list_export (workspace_id, export_id, job_id, snapshot_id, name, columns,
                created_by, created_by_display, created_by_groups)
            VALUES (@ws, @id, @job, @snapshot, @name, @columns::jsonb, @by, @display, @groups)
            """))
        {
            insert.Parameters.AddWithValue("ws", request.WorkspaceId);
            insert.Parameters.AddWithValue("id", request.ExportId);
            insert.Parameters.AddWithValue("job", request.JobId);
            insert.Parameters.AddWithValue("snapshot", request.SnapshotId);
            insert.Parameters.AddWithValue("name", request.Name);
            insert.Parameters.AddWithValue("columns", SerializeColumns(request.Columns));
            insert.Parameters.AddWithValue("by", request.CreatedBy);
            insert.Parameters.AddWithValue("display", request.CreatedByDisplay.Length > 512 ? request.CreatedByDisplay[..512] : request.CreatedByDisplay);
            insert.Parameters.AddWithValue("groups", request.CreatedByGroups.ToArray());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditSql.InsertAsync(tx, request.Requested with
        {
            EventId = Guid.CreateVersion7(),
            WorkspaceId = request.WorkspaceId,
            ResourceType = AuditTaxonomy.Export.ListResourceType,
            ResourceId = request.ExportId.ToString(),
            JobId = request.JobId,
            Details = new Dictionary<string, string?>(request.Requested.Details) { ["ExportId"] = request.ExportId.ToString() },
        }, cancellationToken).ConfigureAwait(false);

        var record = (await ReadOneAsync(tx, "x.export_id = @id", request.ExportId, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ListExportCreation(record, job.Job, true);
    }

    public async Task<ListExportRecord?> GetAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var record = await ReadOneAsync(tx, "x.export_id = @id", exportId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<ListExportRecord>> ListAsync(
        Guid workspaceId, Guid createdBy, (DateTimeOffset CreatedAt, Guid ExportId)? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var records = new List<ListExportRecord>();
        await using (var command = tx.Command(
            $"""
            SELECT {Columns} FROM opportunity.list_export x
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
            SELECT export_id FROM opportunity.list_export
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
            UPDATE opportunity.list_export SET claimed_by = @owner, claimed_until = now() + @lease
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
            UPDATE opportunity.list_export SET claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND export_id = @id AND claimed_by = @owner
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("owner", owner);
            }, cancellationToken);

    public Task MarkRunningAsync(Guid workspaceId, Guid exportId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.list_export SET status = 'Running'
            WHERE workspace_id = @ws AND export_id = @id AND status IN ('Queued', 'Running');
            UPDATE opportunity.job j SET target_snapshot_id = x.snapshot_id, updated_at = now()
            FROM opportunity.list_export x
            WHERE x.workspace_id = @ws AND x.export_id = @id AND j.workspace_id = @ws AND j.job_id = x.job_id
              AND j.target_snapshot_id IS DISTINCT FROM x.snapshot_id
            """,
            c => c.Parameters.AddWithValue("id", exportId), cancellationToken);

    public Task WritePartAsync(
        Guid workspaceId, Guid exportId, int chunkSequence, int rows, int excluded, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        ExecuteAsync(workspaceId,
            """
            INSERT INTO opportunity.list_export_part (workspace_id, export_id, chunk_sequence, row_count, excluded_count, content)
            SELECT @ws, @id, @seq, @rows, @excluded, @content
            WHERE EXISTS (SELECT FROM opportunity.list_export x WHERE x.workspace_id = @ws AND x.export_id = @id AND x.status = 'Running')
            ON CONFLICT (workspace_id, export_id, chunk_sequence)
                DO UPDATE SET row_count = EXCLUDED.row_count, excluded_count = EXCLUDED.excluded_count, content = EXCLUDED.content,
                              written_at = now()
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("seq", chunkSequence);
                c.Parameters.AddWithValue("rows", rows);
                c.Parameters.AddWithValue("excluded", excluded);
                c.Parameters.Add(new NpgsqlParameter("content", NpgsqlDbType.Bytea) { Value = content.ToArray() });
            }, cancellationToken);

    public async Task<(long Rows, long Excluded)> ReadPartsAsync(
        Guid workspaceId, Guid exportId, Func<ReadOnlyMemory<byte>, ValueTask> onPart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onPart);
        long rows = 0, excluded = 0;
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
                    SELECT chunk_sequence, row_count, excluded_count, content FROM opportunity.list_export_part
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
                        return (rows, excluded);
                    }

                    sequence = reader.GetInt32(0);
                    rows += reader.GetInt32(1);
                    excluded += reader.GetInt32(2);
                    content = reader.GetFieldValue<byte[]>(3);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await onPart(content).ConfigureAwait(false);
            after = sequence;
        }
    }

    public async Task<bool> CompleteAsync(
        Guid workspaceId, Guid exportId, long rows, long excluded, long bytes, string sha256, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.list_export
            SET status = 'Completed', completed_at = now(), row_count = @rows, excluded_count = @excluded, byte_count = @bytes,
                content_sha256 = @sha, claimed_by = NULL, claimed_until = NULL
            WHERE workspace_id = @ws AND export_id = @id AND status = 'Running'
            """,
            c =>
            {
                c.Parameters.AddWithValue("id", exportId);
                c.Parameters.AddWithValue("rows", rows);
                c.Parameters.AddWithValue("excluded", excluded);
                c.Parameters.AddWithValue("bytes", bytes);
                c.Parameters.AddWithValue("sha", sha256);
            }, cancellationToken).ConfigureAwait(false) == 1;

    public async Task<bool> FailAsync(Guid workspaceId, Guid exportId, string reason, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(workspaceId,
            """
            UPDATE opportunity.list_export
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

    private static async Task<ListExportRecord?> ReadOneAsync(WorkspaceTransaction tx, string where, Guid id, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {Columns} FROM opportunity.list_export x WHERE x.workspace_id = @ws AND {where}");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static string SerializeColumns(IReadOnlyList<ListExportColumn> columns) =>
        new JsonArray([.. columns.Select(c => (JsonNode)new JsonObject { ["fieldId"] = c.FieldId, ["header"] = c.Header })]).ToJsonString();

    private static List<ListExportColumn> DeserializeColumns(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().Select(n => new ListExportColumn(n!["fieldId"]!.GetValue<int>(), n["header"]!.GetValue<string>()))];

    private static ListExportRecord Read(NpgsqlDataReader r) => new()
    {
        WorkspaceId = r.GetGuid(0),
        ExportId = r.GetGuid(1),
        JobId = r.GetGuid(2),
        SnapshotId = r.GetGuid(3),
        Name = r.GetString(4),
        Columns = DeserializeColumns(r.GetString(5)),
        Status = Enum.Parse<ListExportStatus>(r.GetString(6)),
        StatusReason = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedBy = r.GetGuid(8),
        CreatedByDisplay = r.GetString(9),
        CreatedByGroups = r.GetFieldValue<string[]>(10),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(11),
        CompletedAt = r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        RowCount = r.IsDBNull(13) ? null : r.GetInt64(13),
        ExcludedCount = r.IsDBNull(14) ? null : r.GetInt64(14),
        ByteCount = r.IsDBNull(15) ? null : r.GetInt64(15),
        ContentSha256 = r.IsDBNull(16) ? null : r.GetString(16),
        DocumentCount = r.IsDBNull(17) ? null : r.GetInt64(17),
    };
}

public static class ListExportStoreRegistration
{
    /// <summary>The list exports' store (needs an <see cref="NpgsqlDataSource"/>).</summary>
    public static IServiceCollection AddPostgresListExportStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IListExportStore>(sp => new PostgresListExportStore(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
