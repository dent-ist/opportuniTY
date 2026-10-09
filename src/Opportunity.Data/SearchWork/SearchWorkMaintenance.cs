using Npgsql;

using Opportunity.Application.SearchWork;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.SearchWork;

/// <summary>PostgreSQL implementation of <see cref="ISearchWorkMaintenance"/> (V0011).</summary>
public sealed class SearchWorkMaintenance(NpgsqlDataSource dataSource) : ISearchWorkMaintenance
{
    public async Task<SearchWorkRecovery> RecoverAsync(
        Guid workspaceId, TimeSpan dispatchedTimeout, TimeSpan leaseGrace, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dispatchedTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseGrace, TimeSpan.Zero);

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(tx.Connection, tx.Transaction);
        batch.BatchCommands.Add(Command(
            """
            UPDATE opportunity.search_outbox SET status = 1, dispatched_at = NULL, updated_at = now()
            WHERE workspace_id = @ws AND status = 3 AND dispatched_at < now() - @timeout
            """, workspaceId, dispatchedTimeout, leaseGrace));
        batch.BatchCommands.Add(Command(
            """
            UPDATE opportunity.index_chunk_task SET status = 1, lease_owner = NULL, lease_expires_at = NULL, updated_at = now()
            WHERE workspace_id = @ws AND status = 3 AND lease_expires_at < now() - @grace
            """, workspaceId, dispatchedTimeout, leaseGrace));
        batch.BatchCommands.Add(Command(
            """
            UPDATE opportunity.index_chunk_task SET status = 1, dispatched_at = NULL, updated_at = now()
            WHERE workspace_id = @ws AND status = 2 AND dispatched_at < now() - @timeout
            """, workspaceId, dispatchedTimeout, leaseGrace));
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var recovery = new SearchWorkRecovery(
            batch.BatchCommands[0].RecordsAffected, batch.BatchCommands[1].RecordsAffected, batch.BatchCommands[2].RecordsAffected);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovery;
    }

    public async Task<SearchWorkBacklog> GetBacklogAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status <> 4),
                   (SELECT min(committed_at) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status <> 4),
                   (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status = 5),
                   (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status <> 5),
                   (SELECT min(committed_at) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status <> 5),
                   (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status = 6)
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        SearchWorkBacklog backlog;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            backlog = new SearchWorkBacklog(
                reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.GetInt64(5));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return backlog;
    }

    public async Task<IReadOnlyList<OutboxLaneBacklog>> GetOutboxLaneBacklogAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT lane, count(*), min(committed_at) FILTER (WHERE status IN (1, 2, 5))
            FROM opportunity.search_outbox
            WHERE workspace_id = @ws AND status <> 4
            GROUP BY lane
            ORDER BY lane
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        var lanes = new List<OutboxLaneBacklog>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lanes.Add(new OutboxLaneBacklog(
                    SearchWorkSql.Lane(reader.GetInt16(0)), reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lanes;
    }

    public async Task<PipelineBacklog> GetPipelineBacklogAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        // Each query is served by a partial index over open rows only (index_chunk_task_unapplied_ix, job_chunk_open_ix
        // and job_chunk_lease_ix, job_active_ix), so the cost follows the backlog, not the history.
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(tx.Connection, tx.Transaction);
        batch.BatchCommands.Add(Command(
            """
            SELECT status, lane, count(*), min(CASE WHEN status = 3 THEN coalesce(started_at, updated_at) ELSE committed_at END)
            FROM opportunity.index_chunk_task
            WHERE workspace_id = @ws AND status <> 5
            GROUP BY status, lane
            """, workspaceId, TimeSpan.Zero, TimeSpan.Zero));
        batch.BatchCommands.Add(Command(
            """
            SELECT j.job_type, c.status, count(*), min(CASE WHEN c.status = 3 THEN coalesce(c.claimed_at, c.updated_at) ELSE c.updated_at END)
            FROM opportunity.job_chunk c
            JOIN opportunity.job j ON j.workspace_id = c.workspace_id AND j.job_id = c.job_id
            WHERE c.workspace_id = @ws AND c.status IN (1, 2, 3, 4)
            GROUP BY j.job_type, c.status
            """, workspaceId, TimeSpan.Zero, TimeSpan.Zero));
        batch.BatchCommands.Add(Command(
            """
            SELECT job_type, count(*)
            FROM opportunity.job
            WHERE workspace_id = @ws AND status NOT IN ('Completed', 'CompletedWithErrors', 'Cancelled', 'Failed')
            GROUP BY job_type
            """, workspaceId, TimeSpan.Zero, TimeSpan.Zero));

        var tasks = new List<IndexTaskBacklog>();
        var chunks = new List<JobChunkBacklog>();
        var jobs = new List<ActiveJobCount>();
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tasks.Add(new IndexTaskBacklog(
                    (IndexChunkTaskStatus)reader.GetInt16(0), SearchWorkSql.Lane(reader.GetInt16(1)), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Enum.TryParse<JobType>(reader.GetString(0), out var type))
                {
                    chunks.Add(new JobChunkBacklog(
                        type, (JobChunkStatus)reader.GetInt16(1), reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
                }
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Enum.TryParse<JobType>(reader.GetString(0), out var type))
                {
                    jobs.Add(new ActiveJobCount(type, reader.GetInt64(1)));
                }
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PipelineBacklog(tasks, chunks, jobs);
    }

    public async Task<IReadOnlyList<Guid>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        // The workspace registry is installation-level (ADR-015 D7.1): readable without a workspace context.
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT workspace_id FROM opportunity.workspace WHERE status <> '{nameof(WorkspaceStatus.Purged)}' ORDER BY workspace_id");
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public async Task<int> EnsurePartitionsAsync(DateTimeOffset through, CancellationToken cancellationToken = default)
    {
        // DDL through the owner's SECURITY DEFINER function; no tenant rows are read.
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command("SELECT opportunity.search_work_ensure_partitions(@through)");
        command.Parameters.AddWithValue("through", through.ToUniversalTime());
        var created = (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<IReadOnlyList<SearchWorkPartition>> DropExpiredPartitionsAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT parent_table, partition_name, range_end, unapplied_rows, dropped FROM opportunity.search_work_drop_expired_partitions(@cutoff)");
        command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
        var partitions = new List<SearchWorkPartition>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                partitions.Add(new SearchWorkPartition(
                    reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt64(3), reader.GetBoolean(4)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return partitions;
    }

    private static NpgsqlBatchCommand Command(string sql, Guid workspaceId, TimeSpan timeout, TimeSpan grace)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("ws", workspaceId);
        if (sql.Contains("@timeout", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("timeout", timeout);
        }

        if (sql.Contains("@grace", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("grace", grace);
        }

        return command;
    }
}
