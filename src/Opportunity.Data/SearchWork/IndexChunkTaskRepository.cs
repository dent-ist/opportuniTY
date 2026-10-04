using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Jobs;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.SearchWork;

/// <summary>
/// PostgreSQL implementation of <see cref="IIndexChunkTaskRepository"/> over <c>opportunity.index_chunk_task</c> (V0011).
/// A task id is a UUIDv7 whose timestamp is the row's <c>created_at</c>, so every lookup by id prunes to one partition.
/// </summary>
public sealed class IndexChunkTaskRepository(NpgsqlDataSource dataSource) : IIndexChunkTaskRepository
{
    private const int MaxOwnerLength = 200;

    private const string TaskColumns =
        """
        t.workspace_id, t.task_id, t.job_id, t.chunk_id, t.task_kind, t.membership_kind, t.snapshot_id, t.import_batch_id,
        t.range_from, t.range_to, t.projection_generation, t.document_id_from, t.document_id_to, t.document_ids,
        t.change_mask, t.lane, t.search_generation, t.committed_at, t.status, t.idempotency_key, t.attempt_count,
        t.max_attempts, t.lease_token, t.lease_expires_at, t.created_at, t.completed_at, t.last_error
        """;

    private const string ById = "t.workspace_id = @ws AND t.task_id = @task AND t.created_at = opportunity.uuid_v7_timestamp(@task)";

    public async Task<IReadOnlyList<ClaimedIndexTask>> ClaimForDispatchAsync(
        Guid workspaceId, string owner, int limit, TimeSpan claimDuration, CancellationToken cancellationToken = default)
    {
        ValidateOwner(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(claimDuration, TimeSpan.Zero);

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            WITH candidate AS (
                SELECT workspace_id, created_at, task_id
                FROM opportunity.index_chunk_task
                WHERE workspace_id = @ws AND status IN (1, 4) AND available_at <= now()
                  AND (claim_expires_at IS NULL OR claim_expires_at < now())
                ORDER BY lane, search_generation NULLS LAST, created_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED)
            UPDATE opportunity.index_chunk_task t
            SET claim_owner = @owner, claim_expires_at = clock_timestamp() + @claim, updated_at = now()
            FROM candidate c
            WHERE t.workspace_id = c.workspace_id AND t.created_at = c.created_at AND t.task_id = c.task_id
            RETURNING t.workspace_id, t.task_id, t.job_id, t.chunk_id, t.task_kind, t.lane, t.search_generation, t.committed_at,
                      t.idempotency_key, t.attempt_count
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("claim", claimDuration);
        var tasks = new List<ClaimedIndexTask>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tasks.Add(new ClaimedIndexTask(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), (IndexTaskKind)reader.GetInt16(4),
                    SearchWorkSql.Lane(reader.GetInt16(5)), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetInt32(9)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. tasks.OrderBy(t => t.Lane).ThenBy(t => t.SearchGeneration ?? long.MaxValue).ThenBy(t => t.TaskId)];
    }

    public Task<int> MarkDispatchedAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default) =>
        UpdateClaimedAsync(workspaceId, owner, taskIds,
            "status = 2, dispatched_at = now(), claim_owner = NULL, claim_expires_at = NULL, updated_at = now()", null, cancellationToken);

    public Task<int> ReleaseClaimAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, TimeSpan retryAfter, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryAfter, TimeSpan.Zero);
        return UpdateClaimedAsync(workspaceId, owner, taskIds,
            "available_at = now() + @after, claim_owner = NULL, claim_expires_at = NULL, updated_at = now()",
            c => c.Parameters.AddWithValue("after", retryAfter), cancellationToken);
    }

    public async Task<IndexTaskLeaseResult> LeaseAsync(
        Guid workspaceId, Guid taskId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ValidateOwner(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        if (taskId.Version != 7)
        {
            return new IndexTaskLeaseResult(IndexTaskLeaseOutcome.NotFound);
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using (var fence = tx.Command("SELECT status FROM opportunity.workspace WHERE workspace_id = @ws"))
        {
            fence.Parameters.AddWithValue("ws", workspaceId);
            if (await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string status
                || status != nameof(WorkspaceStatus.Active))
            {
                return new IndexTaskLeaseResult(IndexTaskLeaseOutcome.WorkspaceNotActive);
            }
        }

        IndexChunkTaskInfo? leased;
        await using (var command = tx.Command(
            $"""
            UPDATE opportunity.index_chunk_task t
            SET status = 3, lease_owner = @worker, lease_expires_at = clock_timestamp() + @lease, lease_token = t.lease_token + 1,
                attempt_count = t.attempt_count + 1, claim_owner = NULL, claim_expires_at = NULL,
                started_at = coalesce(t.started_at, now()), updated_at = now()
            WHERE {ById} AND t.attempt_count < t.max_attempts
              AND (t.status IN (1, 2) OR (t.status = 4 AND t.available_at <= now()) OR (t.status = 3 AND t.lease_expires_at < now()))
            RETURNING {TaskColumns}
            """))
        {
            AddId(command, workspaceId, taskId);
            command.Parameters.AddWithValue("worker", workerId);
            command.Parameters.AddWithValue("lease", leaseDuration);
            leased = await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (leased is not null)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new IndexTaskLeaseResult(IndexTaskLeaseOutcome.Leased, leased,
                new IndexTaskLease(workspaceId, taskId, leased.LeaseToken, workerId));
        }

        var current = await ReadAsync(tx, workspaceId, taskId, cancellationToken).ConfigureAwait(false);
        var outcome = current switch
        {
            null => IndexTaskLeaseOutcome.NotFound,
            { Status: IndexChunkTaskStatus.Applied or IndexChunkTaskStatus.Failed } => IndexTaskLeaseOutcome.AlreadySettled,
            _ when current.AttemptCount >= current.MaxAttempts => IndexTaskLeaseOutcome.AttemptsExhausted,
            { Status: IndexChunkTaskStatus.Running } => IndexTaskLeaseOutcome.LeaseHeld,
            _ => IndexTaskLeaseOutcome.NotDue,
        };

        if (outcome == IndexTaskLeaseOutcome.AttemptsExhausted)
        {
            // Ends crash loops even when no worker recorded an error (ADR-010 §3.1).
            await using var exhaust = tx.Command(
                $"""
                UPDATE opportunity.index_chunk_task t
                SET status = 6, lease_owner = NULL, lease_expires_at = NULL, claim_owner = NULL, claim_expires_at = NULL,
                    error_class = {(short)ChunkErrorClass.AttemptsExhausted},
                    last_error = coalesce(t.last_error, 'Attempts exhausted.'), updated_at = now()
                WHERE {ById} AND t.status NOT IN (5, 6) AND (t.status <> 3 OR t.lease_expires_at < now())
                """);
            AddId(exhaust, workspaceId, taskId);
            if (await exhaust.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                outcome = IndexTaskLeaseOutcome.LeaseHeld;
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new IndexTaskLeaseResult(outcome, current);
    }

    public async Task<bool> CompleteAsync(IndexTaskLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            WITH done AS (
                UPDATE opportunity.index_chunk_task t
                SET status = 5, completed_at = now(), lease_owner = NULL, lease_expires_at = NULL, last_error = NULL,
                    error_class = NULL, updated_at = now()
                WHERE {ById} AND t.status = 3 AND t.lease_token = @token
                RETURNING t.job_id)
            UPDATE opportunity.job j SET index_tasks_applied = j.index_tasks_applied + 1, updated_at = now()
            FROM done WHERE j.workspace_id = @ws AND j.job_id = done.job_id
            RETURNING 1
            """);
        AddId(command, lease.WorkspaceId, lease.TaskId);
        command.Parameters.AddWithValue("token", lease.LeaseToken);
        var completed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public async Task<IndexTaskFailureResult> FailAsync(IndexTaskLease lease, ChunkError failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Message);
        if (failure.Class == ChunkErrorClass.AttemptsExhausted)
        {
            throw new ArgumentException("AttemptsExhausted is recorded by leases, not by workers.", nameof(failure));
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var current = await ReadAsync(tx, lease.WorkspaceId, lease.TaskId, cancellationToken).ConfigureAwait(false);
        if (current is not { Status: IndexChunkTaskStatus.Running } || current.LeaseToken != lease.LeaseToken)
        {
            return new IndexTaskFailureResult(IndexTaskFailureOutcome.LeaseLost, null);
        }

        var retry = ChunkRetryPolicy.OnError(failure.Class, current.AttemptCount, current.MaxAttempts) == JobChunkTrigger.RetryLater;
        var backoff = ChunkRetryPolicy.Backoff(Math.Max(1, current.AttemptCount), Random.Shared.NextDouble());
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.index_chunk_task t
            SET status = @status, available_at = CASE WHEN @retry THEN now() + @backoff ELSE t.available_at END,
                lease_owner = NULL, lease_expires_at = NULL, error_class = @class, last_error = @error, updated_at = now()
            WHERE {ById} AND t.status = 3 AND t.lease_token = @token
            RETURNING t.available_at
            """);
        AddId(command, lease.WorkspaceId, lease.TaskId);
        command.Parameters.AddWithValue("status", (short)(retry ? IndexChunkTaskStatus.RetryWait : IndexChunkTaskStatus.Failed));
        command.Parameters.AddWithValue("retry", retry);
        command.Parameters.AddWithValue("backoff", backoff);
        command.Parameters.AddWithValue("class", (short)failure.Class);
        command.Parameters.AddWithValue("error", SearchOutboxRepository.Truncate($"{failure.Code}: {failure.Message}"));
        command.Parameters.AddWithValue("token", lease.LeaseToken);
        DateTimeOffset? availableAt = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                availableAt = reader.GetFieldValue<DateTimeOffset>(0);
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return availableAt switch
        {
            null => new IndexTaskFailureResult(IndexTaskFailureOutcome.LeaseLost, null),
            _ when retry => new IndexTaskFailureResult(IndexTaskFailureOutcome.RetryScheduled, availableAt),
            _ => new IndexTaskFailureResult(IndexTaskFailureOutcome.Failed, null),
        };
    }

    public async Task<IndexTaskRenewal> RenewLeaseAsync(IndexTaskLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            WITH renewed AS (
                UPDATE opportunity.index_chunk_task t
                SET lease_expires_at = greatest(t.lease_expires_at, clock_timestamp() + @lease), updated_at = now()
                WHERE {ById} AND t.status = 3 AND t.lease_token = @token
                RETURNING 1)
            SELECT (SELECT count(*) FROM renewed),
                   (SELECT w.status FROM opportunity.workspace w WHERE w.workspace_id = @ws)
            """);
        AddId(command, lease.WorkspaceId, lease.TaskId);
        command.Parameters.AddWithValue("token", lease.LeaseToken);
        command.Parameters.AddWithValue("lease", leaseDuration);
        long renewed;
        string? status;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            renewed = reader.GetInt64(0);
            status = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return renewed == 0 ? IndexTaskRenewal.LeaseLost
            : status == nameof(WorkspaceStatus.Active) ? IndexTaskRenewal.Renewed
            : IndexTaskRenewal.WorkspaceNotActive;
    }

    public async Task<bool> ReleaseAsync(IndexTaskLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.index_chunk_task t
            SET status = 1, attempt_count = greatest(t.attempt_count - 1, 0), available_at = now(), lease_owner = NULL,
                lease_expires_at = NULL, updated_at = now()
            WHERE {ById} AND t.status = 3 AND t.lease_token = @token
            """);
        AddId(command, lease.WorkspaceId, lease.TaskId);
        command.Parameters.AddWithValue("token", lease.LeaseToken);
        var released = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return released;
    }

    public async Task<IndexChunkTaskInfo?> GetAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken = default)
    {
        if (taskId.Version != 7)
        {
            return null;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var task = await ReadAsync(tx, workspaceId, taskId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task<IReadOnlyList<IndexChunkTaskInfo>> GetByJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"SELECT {TaskColumns} FROM opportunity.index_chunk_task t WHERE t.workspace_id = @ws AND t.job_id = @job ORDER BY t.created_at, t.task_id");
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        var tasks = new List<IndexChunkTaskInfo>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tasks.Add(Read(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return tasks;
    }

    private async Task<int> UpdateClaimedAsync(
        Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, string set, Action<NpgsqlCommand>? parameters,
        CancellationToken cancellationToken)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(taskIds);
        if (taskIds.Count == 0)
        {
            return 0;
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.index_chunk_task t SET {set}
            FROM unnest(@ids) AS k(task_id)
            WHERE t.workspace_id = @ws AND t.task_id = k.task_id AND t.created_at = opportunity.uuid_v7_timestamp(k.task_id)
              AND t.status IN (1, 4) AND t.claim_owner = @owner
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = taskIds.ToArray() });
        parameters?.Invoke(command);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async Task<IndexChunkTaskInfo?> ReadAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command($"SELECT {TaskColumns} FROM opportunity.index_chunk_task t WHERE {ById}");
        AddId(command, workspaceId, taskId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IndexChunkTaskInfo?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static IndexChunkTaskInfo Read(NpgsqlDataReader reader) => new()
    {
        WorkspaceId = reader.GetGuid(0),
        TaskId = reader.GetGuid(1),
        JobId = reader.GetGuid(2),
        ChunkId = reader.GetGuid(3),
        Kind = (IndexTaskKind)reader.GetInt16(4),
        Membership = ChunkMembership.Restore(
            (ChunkMembershipKind)reader.GetInt16(5),
            NullableGuid(reader, 6),
            NullableGuid(reader, 7),
            NullableInt64(reader, 8),
            NullableInt64(reader, 9),
            NullableInt64(reader, 10),
            NullableGuid(reader, 11),
            NullableGuid(reader, 12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<Guid[]>(13)),
        ChangeMask = (SearchChangeMask)reader.GetInt16(14),
        Lane = SearchWorkSql.Lane(reader.GetInt16(15)),
        SearchGeneration = NullableInt64(reader, 16),
        CommittedAt = reader.GetFieldValue<DateTimeOffset>(17),
        Status = (IndexChunkTaskStatus)reader.GetInt16(18),
        IdempotencyKey = reader.GetString(19),
        AttemptCount = reader.GetInt32(20),
        MaxAttempts = reader.GetInt16(21),
        LeaseToken = reader.GetInt64(22),
        LeaseExpiresAt = reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(24),
        CompletedAt = reader.IsDBNull(25) ? null : reader.GetFieldValue<DateTimeOffset>(25),
        LastError = reader.IsDBNull(26) ? null : reader.GetString(26),
    };

    private static void AddId(NpgsqlCommand command, Guid workspaceId, Guid taskId)
    {
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("task", taskId);
    }

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static void ValidateOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(owner.Length, MaxOwnerLength, nameof(owner));
    }
}
