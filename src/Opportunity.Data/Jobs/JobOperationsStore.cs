using System.Globalization;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Data.Audit;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;

namespace Opportunity.Data.Jobs;

/// <summary>PostgreSQL implementation of <see cref="IJobOperationsStore"/> (E06-T06, ADR-010 §7).</summary>
public sealed class JobOperationsStore(NpgsqlDataSource dataSource) : IJobOperationsStore
{
    // The job columns of JobSql, then the monitor's extras: initiator display name, import/export name, failed tasks.
    private const string OverviewSelect =
        $"""
        SELECT {JobSql.JobColumns}, u.display_name, coalesce(b.name, e.name),
               (SELECT count(*) FROM opportunity.index_chunk_task t
                 WHERE t.workspace_id = j.workspace_id AND t.job_id = j.job_id AND t.status = 6)
        FROM opportunity.job j
        JOIN opportunity.workspace w ON w.workspace_id = j.workspace_id
        LEFT JOIN opportunity.app_user u ON u.user_id = j.initiated_by
        LEFT JOIN opportunity.import_batch b ON b.workspace_id = j.workspace_id AND b.import_batch_id = j.import_batch_id
        LEFT JOIN LATERAL (SELECT x.name FROM opportunity.export x WHERE x.workspace_id = j.workspace_id AND x.job_id = j.job_id LIMIT 1) e ON true
        """;

    private const int OverviewOffset = 35;

    public async Task<JobOverviewPage> ListAsync(JobListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, JobListQuery.MaxLimit + 1);
        var polling = query.UpdatedSince is not null;
        var keyset = polling
            ? "AND (@after_at::timestamptz IS NULL OR (j.updated_at, j.job_id) > (@after_at, @after_id)) ORDER BY j.updated_at, j.job_id"
            : "AND (@after_at::timestamptz IS NULL OR (j.created_at, j.job_id) < (@after_at, @after_id)) ORDER BY j.created_at DESC, j.job_id DESC";

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, query.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var items = new List<JobOverview>();
        await using (var command = tx.Command(
            $"""
            {OverviewSelect}
            WHERE j.workspace_id = @ws
              AND (@initiator::uuid IS NULL OR j.initiated_by = @initiator)
              AND (cardinality(@types::text[]) = 0 OR j.job_type = ANY(@types))
              AND (cardinality(@statuses::text[]) = 0 OR j.status = ANY(@statuses))
              AND (@from::timestamptz IS NULL OR j.created_at >= @from)
              AND (@to::timestamptz IS NULL OR j.created_at < @to)
              AND (@since::timestamptz IS NULL OR j.updated_at > @since)
              {keyset}
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", query.WorkspaceId);
            command.Parameters.Add(new NpgsqlParameter("initiator", NpgsqlDbType.Uuid) { Value = (object?)query.InitiatedBy ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("types", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = query.Types.Select(t => t.ToString()).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = query.Statuses.Select(s => s.ToString()).ToArray() });
            command.Parameters.Add(Time("from", query.CreatedFrom));
            command.Parameters.Add(Time("to", query.CreatedTo));
            command.Parameters.Add(Time("since", query.UpdatedSince));
            command.Parameters.Add(Time("after_at", query.After?.At));
            command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = (object?)query.After?.JobId ?? DBNull.Value });
            command.Parameters.AddWithValue("limit", query.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadOverview(reader));
            }
        }

        var indexed = await SearchWatermarkStore.ReadIndexedThroughAsync(tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobOverviewPage(items, indexed);
    }

    public async Task<JobOperationsDetail?> GetDetailAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        JobOverview? overview;
        await using (var command = tx.Command($"{OverviewSelect} WHERE j.workspace_id = @ws AND j.job_id = @job"))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("job", jobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            overview = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOverview(reader) : null;
        }

        if (overview is null)
        {
            return null;
        }

        var counts = new Dictionary<JobChunkStatus, long>();
        long exhausted = 0, attempts = 0;
        string? lastError = null;
        await using (var batch = tx.Batch())
        {
            batch.BatchCommands.Add(Batch(
                $"""
                SELECT status, coalesce(error_class = {(short)ChunkErrorClass.AttemptsExhausted}, false), count(*), coalesce(sum(attempt_count), 0)
                FROM opportunity.job_chunk WHERE workspace_id = @ws AND job_id = @job
                GROUP BY 1, 2
                """, workspaceId, jobId));
            batch.BatchCommands.Add(Batch(
                """
                SELECT error FROM (
                    SELECT coalesce(c.error_code || ': ', '') || c.last_error AS error, c.updated_at
                    FROM opportunity.job_chunk c WHERE c.workspace_id = @ws AND c.job_id = @job AND c.last_error IS NOT NULL
                    UNION ALL
                    SELECT t.last_error, t.updated_at
                    FROM opportunity.index_chunk_task t WHERE t.workspace_id = @ws AND t.job_id = @job AND t.last_error IS NOT NULL) e
                ORDER BY updated_at DESC
                LIMIT 1
                """, workspaceId, jobId));
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = (JobChunkStatus)reader.GetInt16(0);
                var count = reader.GetInt64(2);
                if (status == JobChunkStatus.Failed && reader.GetBoolean(1))
                {
                    exhausted += count;
                }
                else
                {
                    counts[status] = counts.GetValueOrDefault(status) + count;
                }

                attempts += reader.GetInt64(3);
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lastError = reader.GetString(0);
            }
        }

        var indexed = await SearchWatermarkStore.ReadIndexedThroughAsync(tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobOperationsDetail(overview, counts, exhausted, attempts, lastError, indexed);
    }

    public async Task<IReadOnlyList<JobFailureRecord>> ListFailuresAsync(
        Guid workspaceId, Guid jobId, JobFailureRecord? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT kind, id, attempts, error, failed_at FROM (
                SELECT 0 AS kind, c.chunk_id::text AS id, c.attempt_count::bigint AS attempts,
                       coalesce(c.error_code || ': ', '') || c.last_error AS error, coalesce(c.settled_at, c.updated_at) AS failed_at
                FROM opportunity.job_chunk c
                WHERE c.workspace_id = @ws AND c.job_id = @job AND c.status = 6
                UNION ALL
                SELECT 1, t.task_id::text, t.attempt_count::bigint, t.last_error, t.updated_at
                FROM opportunity.index_chunk_task t
                WHERE t.workspace_id = @ws AND t.job_id = @job AND t.status = 6
                UNION ALL
                SELECT 3, d.message_id, d.death_count::bigint,
                       d.queue || ': ' || d.death_reason || coalesce(' — ' || d.error, ''), d.recorded_at
                FROM opportunity.dead_letter d
                WHERE d.workspace_id = @ws AND d.job_id = @job
                  AND (d.subject_id IS NULL
                       OR EXISTS (SELECT FROM opportunity.job_chunk c
                                   WHERE c.workspace_id = @ws AND c.job_id = @job AND c.chunk_id = d.subject_id AND c.status = 6)
                       OR EXISTS (SELECT FROM opportunity.index_chunk_task t
                                   WHERE t.workspace_id = @ws AND t.job_id = @job AND t.task_id = d.subject_id AND t.status = 6))) f
            WHERE @after_at::timestamptz IS NULL OR (failed_at, kind, id) > (@after_at, @after_kind, @after_id)
            ORDER BY failed_at, kind, id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        AddFailureCursor(command, after);
        command.Parameters.AddWithValue("limit", limit);
        var failures = await ReadFailuresAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return failures;
    }

    public async Task<IReadOnlyList<JobFailureRecord>> ListOutboxFailuresAsync(
        Guid workspaceId, JobFailureRecord? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            SELECT 2, o.outbox_id::text, o.attempt_count::bigint, o.last_error, o.updated_at
            FROM opportunity.search_outbox o
            WHERE o.workspace_id = @ws AND o.status = 5
              AND (@after_at::timestamptz IS NULL OR (o.updated_at, 2, o.outbox_id::text) > (@after_at, @after_kind, @after_id))
            ORDER BY o.updated_at, o.outbox_id::text
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        AddFailureCursor(command, after);
        command.Parameters.AddWithValue("limit", limit);
        var failures = await ReadFailuresAsync(command, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return failures;
    }

    public async Task<JobReplayOutcome> ReplayFailedAsync(
        Guid workspaceId, Guid jobId, OperationsActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var locked = await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return new JobReplayOutcome(JobTransitionOutcome.NotFound, 0, 0, null);
        }

        var chunks = 0;
        var status = locked.Job.Status;
        if (JobRepository.CanReplayChunks(status))
        {
            (chunks, status, locked) = await JobRepository.ReplayChunksInTransactionAsync(tx, locked, null, cancellationToken).ConfigureAwait(false);
        }

        // Index tasks are never cancelled (ADR-010 §2): a committed chunk's changes must reach the index whatever the job
        // status. The job row lock taken above serializes concurrent replays of the same job.
        int tasks;
        await using (var command = tx.Command(
            """
            UPDATE opportunity.index_chunk_task SET status = 1, attempt_count = 0, replay_count = replay_count + 1,
                available_at = now(), error_class = NULL, updated_at = now()
            WHERE workspace_id = @ws AND job_id = @job AND status = 6
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("job", jobId);
            tasks = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (chunks + tasks > 0)
        {
            await using (var touch = tx.Command("UPDATE opportunity.job SET updated_at = now() WHERE workspace_id = @ws AND job_id = @job"))
            {
                touch.Parameters.AddWithValue("ws", workspaceId);
                touch.Parameters.AddWithValue("job", jobId);
                await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await JobSql.AuditAsync(tx, locked.Job, AuditTaxonomy.Job.Replayed, actor.UserId, cancellationToken,
                details: new Dictionary<string, string?>
                {
                    ["ChunksReplayed"] = chunks.ToString(CultureInfo.InvariantCulture),
                    ["IndexTasksReplayed"] = tasks.ToString(CultureInfo.InvariantCulture),
                },
                operatorName: actor.OperatorName).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobReplayOutcome(JobTransitionOutcome.Applied, chunks, tasks, status);
    }

    public async Task<int> ReplayFailedOutboxAsync(Guid workspaceId, OperationsActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        int rows;
        await using (var command = tx.Command(
            """
            UPDATE opportunity.search_outbox SET status = 1, attempt_count = 0, available_at = now(), updated_at = now()
            WHERE workspace_id = @ws AND status = 5
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (rows > 0)
        {
            string? display = null;
            if (actor.UserId is { } user)
            {
                await using var lookup = tx.Command("SELECT coalesce(display_name, subject) FROM opportunity.app_user WHERE user_id = @user");
                lookup.Parameters.AddWithValue("user", user);
                display = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            }

            await AuditSql.InsertAsync(tx, new AuditEvent
            {
                WorkspaceId = workspaceId,
                OccurredAt = DateTimeOffset.UtcNow,
                Category = AuditTaxonomy.Job.Category,
                Action = AuditTaxonomy.Job.Replayed,
                ActorType = actor.UserId is null ? AuditActorType.Service : AuditActorType.User,
                ActorId = actor.UserId?.ToString() ?? OperationsActor.CliServiceId,
                ActorDisplay = JobSql.Truncate(
                    actor.UserId is { } id ? display ?? id.ToString() : $"Operations CLI ({actor.OperatorName})", AuditEventRules.MaxActorDisplayLength),
                ResourceType = "SearchOutbox",
                ResourceId = workspaceId.ToString(),
                Outcome = AuditOutcome.Success,
                Details = new Dictionary<string, string?> { ["RowsReplayed"] = rows.ToString(CultureInfo.InvariantCulture) },
            }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static JobOverview ReadOverview(NpgsqlDataReader reader) => new(
        JobSql.ReadLockedJob(reader).Job,
        reader.IsDBNull(OverviewOffset) ? null : reader.GetString(OverviewOffset),
        reader.IsDBNull(OverviewOffset + 1) ? null : reader.GetString(OverviewOffset + 1),
        reader.GetInt64(OverviewOffset + 2));

    private static NpgsqlBatchCommand Batch(string sql, Guid workspaceId, Guid jobId)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        return command;
    }

    private static NpgsqlParameter Time(string name, DateTimeOffset? value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value is { } v ? v.ToUniversalTime() : DBNull.Value };

    private static void AddFailureCursor(NpgsqlCommand command, JobFailureRecord? after)
    {
        command.Parameters.Add(Time("after_at", after?.FailedAt));
        command.Parameters.AddWithValue("after_kind", after is null ? 0 : (int)after.Source);
        command.Parameters.AddWithValue("after_id", after?.Id ?? string.Empty);
    }

    private static async Task<IReadOnlyList<JobFailureRecord>> ReadFailuresAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var failures = new List<JobFailureRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            failures.Add(new JobFailureRecord(
                (JobFailureSource)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return failures;
    }
}
