using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Jobs;

namespace Opportunity.Data.SearchWork;

/// <summary>
/// Creates search work inside the caller's transaction (ADR-001 §1 R1): the transaction that changes authoritative
/// state also writes its SearchOutbox rows or its one IndexChunkTask. Each helper ends with the late-lock
/// SearchGeneration increment (ADR-001 §7.1), so call it as the transaction's last statement and commit right after:
/// the counter row stays locked until the commit, which makes generations commit-ordered and gap-free per workspace and
/// makes <c>committed_at</c> (the increment's <c>clock_timestamp()</c>) the commit time lag is measured from.
/// </summary>
internal static class SearchWorkSql
{
    /// <summary>LISTEN channel the dispatcher wakes on; the payload is <c>{workspaceId}|{lane}</c>.</summary>
    public const string NotifyChannel = "search_outbox";

    // @n OutboxIds are allocated with the generation; the row lock taken here is held until the commit.
    private const string NextGeneration =
        """
        INSERT INTO opportunity.workspace_search_generation AS s (workspace_id, value, last_outbox_id) VALUES (@ws, 1, @n)
        ON CONFLICT (workspace_id) DO UPDATE SET value = s.value + 1, last_outbox_id = s.last_outbox_id + @n
        RETURNING s.value, s.last_outbox_id, clock_timestamp() AS committed_at
        """;
    /// <summary>
    /// The applied search watermark and the generation counter (ADR-001 §7.2), read in the caller's transaction (use
    /// REPEATABLE READ for one snapshot): every change with a generation at or below <c>Applied</c> is applied to the
    /// index. Not refresh-aware yet (E07-T08).
    /// </summary>
    public static async Task<SearchWatermark> ReadWatermarkAsync(WorkspaceTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT coalesce((SELECT g.value FROM opportunity.workspace_search_generation g WHERE g.workspace_id = @ws), 0),
                   (SELECT min(o.search_generation) FROM opportunity.search_outbox o WHERE o.workspace_id = @ws AND o.status <> 4),
                   (SELECT min(t.search_generation) FROM opportunity.index_chunk_task t
                     WHERE t.workspace_id = @ws AND t.status <> 5 AND t.search_generation IS NOT NULL)
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        long counter;
        long? outbox, tasks;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            counter = reader.GetInt64(0);
            outbox = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            tasks = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        }

        var oldestPending = Math.Min(outbox ?? long.MaxValue, tasks ?? long.MaxValue);
        return new SearchWatermark(oldestPending == long.MaxValue ? counter : Math.Min(counter, oldestPending - 1), counter);
    }

    /// <summary>
    /// One SearchOutbox row per document (interactive edits). Returns the stamped SearchGeneration. Wakes the dispatcher
    /// with <c>pg_notify</c>, delivered at commit.
    /// </summary>
    public static async Task<long> AddOutboxRowsAsync(
        WorkspaceTransaction tx, IReadOnlyCollection<(Guid DocumentId, long DocumentVersion)> documents, SearchChangeMask changeMask,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0 || changeMask == SearchChangeMask.None)
        {
            throw new ArgumentException("Outbox rows need documents and a change mask.", nameof(documents));
        }

        var lane = SearchLanes.ForOutbox(changeMask);
        await using var batch = new NpgsqlBatch(tx.Connection, tx.Transaction);
        var insert = new NpgsqlBatchCommand(
            $"""
            WITH g AS ({NextGeneration})
            INSERT INTO opportunity.search_outbox
                (workspace_id, outbox_id, document_id, document_version, change_mask, lane, search_generation, committed_at)
            SELECT @ws, g.last_outbox_id - @n + d.ord, d.id, d.version, @mask, @lane, g.value, g.committed_at
            FROM g, unnest(@ids, @versions) WITH ORDINALITY AS d(id, version, ord)
            RETURNING search_generation
            """);
        insert.Parameters.AddWithValue("ws", tx.WorkspaceId);
        insert.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = documents.Select(d => d.DocumentId).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = documents.Select(d => d.DocumentVersion).ToArray() });
        insert.Parameters.AddWithValue("n", (long)documents.Count);
        insert.Parameters.AddWithValue("mask", (short)changeMask);
        insert.Parameters.AddWithValue("lane", (short)lane);
        batch.BatchCommands.Add(insert);

        var notify = new NpgsqlBatchCommand("SELECT pg_notify(@channel, @payload)");
        notify.Parameters.AddWithValue("channel", NotifyChannel);
        notify.Parameters.AddWithValue("payload", $"{tx.WorkspaceId:D}|{lane}");
        batch.BatchCommands.Add(notify);

        long generation = 0;
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                generation = reader.GetInt64(0);
            }
        }

        return generation;
    }

    /// <summary>
    /// The one IndexChunkTask of a committing chunk, with the job's <c>job_generation</c> advanced to its generation.
    /// Reindex tasks get no generation (ADR-001 §7.5) and leave the counter alone. Returns the task id and generation.
    /// </summary>
    public static async Task<(Guid TaskId, long? SearchGeneration)> AddIndexChunkTaskAsync(
        WorkspaceTransaction tx, NewIndexChunkTask task, CancellationToken cancellationToken)
    {
        Validate(task);
        var taskId = Guid.CreateVersion7();
        var stamped = task.Kind != IndexTaskKind.Reindex;
        var generation = stamped ? NextGeneration : "SELECT NULL::bigint AS value, clock_timestamp() AS committed_at";
        await using var command = tx.Command(
            $"""
            WITH g AS ({generation}),
            t AS (
                INSERT INTO opportunity.index_chunk_task
                    (workspace_id, created_at, task_id, job_id, chunk_id, task_kind, membership_kind, snapshot_id, import_batch_id,
                     range_from, range_to, projection_generation, document_id_from, document_id_to, document_ids, change_mask, lane,
                     search_generation, committed_at, idempotency_key, max_attempts)
                SELECT @ws, opportunity.uuid_v7_timestamp(@task), @task, @job, @chunk, @kind, @membership_kind, @snapshot, @batch,
                       @range_from, @range_to, @projection_generation, @doc_from, @doc_to, @doc_ids, @mask, @lane,
                       g.value, g.committed_at, @key, @max_attempts
                FROM g
                RETURNING search_generation),
            j AS (
                UPDATE opportunity.job SET job_generation = GREATEST(coalesce(job_generation, 0), (SELECT search_generation FROM t))
                WHERE workspace_id = @ws AND job_id = @job AND (SELECT search_generation FROM t) IS NOT NULL)
            SELECT search_generation FROM t
            """);
        var m = task.Membership;
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("task", taskId);
        command.Parameters.AddWithValue("job", task.JobId);
        command.Parameters.AddWithValue("chunk", task.ChunkId);
        command.Parameters.AddWithValue("kind", (short)task.Kind);
        command.Parameters.AddWithValue("membership_kind", (short)m.Kind);
        command.Parameters.Add(Nullable("snapshot", NpgsqlDbType.Uuid, m.SnapshotId));
        command.Parameters.Add(Nullable("batch", NpgsqlDbType.Uuid, m.ImportBatchId));
        command.Parameters.Add(Nullable("range_from", NpgsqlDbType.Bigint, m.RangeFrom));
        command.Parameters.Add(Nullable("range_to", NpgsqlDbType.Bigint, m.RangeTo));
        command.Parameters.Add(Nullable("projection_generation", NpgsqlDbType.Bigint, m.ProjectionGeneration));
        command.Parameters.Add(Nullable("doc_from", NpgsqlDbType.Uuid, m.DocumentIdFrom));
        command.Parameters.Add(Nullable("doc_to", NpgsqlDbType.Uuid, m.DocumentIdTo));
        command.Parameters.Add(Nullable("doc_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, m.DocumentIds?.ToArray()));
        command.Parameters.AddWithValue("mask", (short)task.ChangeMask);
        command.Parameters.AddWithValue("lane", (short)SearchLanes.ForChunkTask(task.ChangeMask));
        command.Parameters.AddWithValue("key", task.IdempotencyKey);
        command.Parameters.AddWithValue("max_attempts", (short)task.MaxAttempts);
        if (stamped)
        {
            command.Parameters.AddWithValue("n", 0L);
        }
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (taskId, result is long value ? value : null);
    }

    /// <summary>
    /// Fence F3 and the chunk's IndexChunkTask in the caller's transaction, after the chunk's own writes: commits the
    /// chunk (counting one index task when <paramref name="task"/> is given) and, only if F3 passes, writes the task as
    /// the last statement. On any outcome but Committed the caller rolls back (and releases the chunk unless the lease
    /// was lost), exactly as for <see cref="JobChunkRepository.CommitInTransactionAsync"/>.
    /// </summary>
    public static async Task<(ChunkCommitResult Commit, Guid? TaskId)> CommitChunkAsync(
        WorkspaceTransaction tx, ChunkLease lease, ChunkCompletion completion, NewIndexChunkTask? task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(completion);
        if (task is not null && (task.JobId != lease.JobId || task.ChunkId != lease.ChunkId))
        {
            throw new ArgumentException("The index task must belong to the leased chunk.", nameof(task));
        }

        var commit = await JobChunkRepository.CommitInTransactionAsync(
            tx, lease, completion with { IndexTasks = task is null ? 0 : 1 }, cancellationToken).ConfigureAwait(false);
        if (!commit.Committed || task is null)
        {
            return (commit, null);
        }

        var (taskId, _) = await AddIndexChunkTaskAsync(tx, task, cancellationToken).ConfigureAwait(false);
        return (commit, taskId);
    }

    private static void Validate(NewIndexChunkTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(task.Membership);
        if (task.ChangeMask == SearchChangeMask.None || !Enum.IsDefined(task.Kind))
        {
            throw new ArgumentException("An index task needs a kind and a change mask.", nameof(task));
        }

        if (task.IdempotencyKey is not { Length: ChunkIdempotencyKey.Length })
        {
            throw new ArgumentException("The idempotency key must be an ADR-010 §5.1 key.", nameof(task));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(task.MaxAttempts);
    }

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    internal static MessageLane Lane(short value) => (MessageLane)value;
}
