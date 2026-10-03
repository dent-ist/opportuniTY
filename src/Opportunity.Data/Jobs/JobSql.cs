using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Workspaces;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Jobs;

/// <summary>A job row locked by the current transaction, with its workspace's status.</summary>
internal sealed record LockedJob(JobInfo Job, string WorkspaceStatus, int ConsecutiveFailures, ChunkErrorClass? LastFailureClass)
{
    public bool WorkspaceActive => WorkspaceStatus == nameof(Core.Workspaces.WorkspaceStatus.Active);

    /// <summary>Fences F1–F3 pass: the job is Running and its workspace Active.</summary>
    public bool Runnable => Job.Status == JobStatus.Running && WorkspaceActive;

    /// <summary>Chunks of this job must not run any more and are cancelled at their next fence.</summary>
    public bool CancelsChunks => Job.Status is JobStatus.Cancelling or JobStatus.Failed or JobStatus.Cancelled;
}

/// <summary>Counter changes of one settling transaction.</summary>
internal sealed record JobDelta
{
    public int Committed { get; init; }

    public int Failed { get; init; }

    public int Cancelled { get; init; }

    public int Replayed { get; init; }

    public long ItemsApplied { get; init; }

    public long ItemsUnchanged { get; init; }

    public long ItemsSkippedConcurrentEdit { get; init; }

    public long ItemsExcludedNoAccess { get; init; }

    public long ItemsFailed { get; init; }

    public long IndexTasks { get; init; }

    /// <summary>Error class of <see cref="Failed"/> chunks (circuit breaker).</summary>
    public ChunkErrorClass? FailureClass { get; init; }
}

/// <summary>
/// SQL shared by the job and chunk repositories. Lock order is always job row first, then chunk rows, so settling
/// transactions cannot deadlock each other; single-statement chunk updates (claim-next, heartbeat) lock one row only.
/// </summary>
internal static class JobSql
{
    public const string JobColumns =
        """
        j.workspace_id, j.job_id, j.job_type, j.status, j.status_reason, j.target_snapshot_id, j.import_batch_id,
        j.parameters::text, j.initiated_by, j.client_idempotency_key, j.correlation_id, j.operation_kind,
        j.projection_generation, j.max_attempts, j.job_generation, j.chunks_total, j.chunks_committed, j.chunks_failed,
        j.chunks_cancelled, j.items_applied, j.items_unchanged, j.items_skipped_concurrent_edit,
        j.items_excluded_no_access, j.items_failed, j.index_tasks_total, j.index_tasks_applied, j.cancel_requested_by,
        j.cancel_requested_at, j.created_at, j.updated_at, j.started_at, j.finished_at, j.consecutive_failures,
        j.last_failure_class, w.status
        """;

    public const string ChunkColumns =
        """
        c.workspace_id, c.job_id, c.chunk_id, c.chunk_sequence, c.status, c.membership_kind, c.snapshot_id,
        c.import_batch_id, c.range_from, c.range_to, c.projection_generation, c.document_id_from, c.document_id_to,
        c.document_ids, c.item_count, c.estimated_bytes, c.idempotency_key, c.attempt_count, c.max_attempts,
        c.available_at, c.lease_owner, c.lease_expires_at, c.lease_token, c.last_error, c.error_class, c.error_code,
        c.replay_count, c.claimed_at, c.settled_at
        """;

    public const int ChunkColumnCount = 29;

    private const string ReleaseLease = "lease_owner = NULL, lease_expires_at = NULL, updated_at = now()";

    /// <summary>The SET clause (besides status) of each chunk transition.</summary>
    public static string ChunkSet(JobChunkTrigger trigger) => trigger switch
    {
        JobChunkTrigger.Dispatch => "updated_at = now()",
        JobChunkTrigger.Claim or JobChunkTrigger.Reclaim =>
            "lease_owner = @worker, lease_expires_at = now() + @lease, lease_token = lease_token + 1, " +
            "attempt_count = attempt_count + 1, claimed_at = now(), updated_at = now()",
        JobChunkTrigger.Commit or JobChunkTrigger.Cancel => ReleaseLease + ", settled_at = now()",
        JobChunkTrigger.RetryLater =>
            ReleaseLease + ", available_at = now() + @backoff, last_error = @error, error_class = @error_class, error_code = @error_code",
        JobChunkTrigger.FailPermanently or JobChunkTrigger.ExhaustAttempts =>
            ReleaseLease + ", settled_at = now(), last_error = @error, error_class = @error_class, error_code = @error_code",
        // No attempt is charged for a yield (ADR-010 §2): the claim's increment is taken back.
        JobChunkTrigger.Yield =>
            ReleaseLease + ", available_at = now(), attempt_count = CASE WHEN status = 3 THEN greatest(attempt_count - 1, 0) ELSE attempt_count END",
        JobChunkTrigger.ExpireLease => ReleaseLease + ", available_at = now()",
        JobChunkTrigger.Replay =>
            "attempt_count = 0, replay_count = replay_count + 1, available_at = now(), settled_at = NULL, updated_at = now()",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
    };

    public static short[] Sources(JobChunkTrigger trigger) =>
        [.. JobChunkStateMachine.SourcesOf(trigger).Select(s => (short)s)];

    /// <summary>
    /// One conditional chunk transition: <c>WHERE status = ANY(allowed sources)</c>, optionally fenced by the lease
    /// token (then only a Running chunk qualifies). Returns whether a row changed.
    /// </summary>
    public static async Task<bool> TransitionChunkAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid chunkId, JobChunkTrigger trigger, long? leaseToken,
        CancellationToken cancellationToken, Action<NpgsqlCommand>? parameters = null)
    {
        var fence = leaseToken is null ? string.Empty : " AND lease_token = @token AND status = 3";
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.job_chunk SET status = @to, {ChunkSet(trigger)}
            WHERE workspace_id = @ws AND chunk_id = @chunk AND status = ANY(@sources){fence}
            """);
        command.Parameters.AddWithValue("to", (short)JobChunkStateMachine.TargetOf(trigger));
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("chunk", chunkId);
        command.Parameters.AddWithValue("sources", Sources(trigger));
        if (leaseToken is { } token)
        {
            command.Parameters.AddWithValue("token", token);
        }

        parameters?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public static void AddError(NpgsqlCommand command, ChunkErrorClass errorClass, string code, string message)
    {
        command.Parameters.AddWithValue("error_class", (short)errorClass);
        command.Parameters.AddWithValue("error_code", Truncate(code, ChunkError.MaxCodeLength));
        command.Parameters.AddWithValue("error", Truncate(message, ChunkError.MaxMessageLength));
    }

    /// <summary>Locks the job row (<c>FOR NO KEY UPDATE</c>) and shares the workspace row, so status changes of either
    /// serialize with this transaction (fence F3). Null when the job does not exist in the workspace.</summary>
    public static async Task<LockedJob?> LockJobAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            SELECT {JobColumns}
            FROM opportunity.job j JOIN opportunity.workspace w ON w.workspace_id = j.workspace_id
            WHERE j.workspace_id = @ws AND j.job_id = @job
            FOR NO KEY UPDATE OF j FOR SHARE OF w
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLockedJob(reader) : null;
    }

    public static async Task<JobInfo?> ReadJobAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            SELECT {JobColumns}
            FROM opportunity.job j JOIN opportunity.workspace w ON w.workspace_id = j.workspace_id
            WHERE j.workspace_id = @ws AND j.job_id = @job
            """);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLockedJob(reader).Job : null;
    }

    /// <summary>
    /// Writes a job status transition (validated against <see cref="JobStateMachine"/>) and returns the new status. The
    /// caller holds the job lock; the UPDATE is still conditional on the status it read.
    /// </summary>
    public static async Task<JobStatus> TransitionJobAsync(
        WorkspaceTransaction tx, LockedJob locked, JobTrigger trigger, string? reason, CancellationToken cancellationToken,
        string extraSet = "", Action<NpgsqlCommand>? parameters = null)
    {
        var to = JobStateMachine.Transition(locked.Job.Status, trigger);
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.job SET status = @to, status_reason = coalesce(@reason, status_reason),
                started_at = CASE WHEN @to = 'Running' THEN coalesce(started_at, now()) ELSE started_at END,
                finished_at = CASE WHEN @finished THEN coalesce(finished_at, now()) END,
                updated_at = now(){extraSet}
            WHERE workspace_id = @ws AND job_id = @job AND status = @from
            """);
        command.Parameters.AddWithValue("to", to.ToString());
        command.Parameters.AddWithValue("from", locked.Job.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)TruncateOrNull(reason, 2000) ?? DBNull.Value });
        command.Parameters.AddWithValue("finished", JobStateMachine.IsFinished(to));
        command.Parameters.AddWithValue("ws", locked.Job.WorkspaceId);
        command.Parameters.AddWithValue("job", locked.Job.JobId);
        parameters?.Invoke(command);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The locked job row changed under its lock.");
        }

        return to;
    }

    /// <summary>
    /// Applies counter changes and the automatic transition that follows them (completion, finished cancellation,
    /// circuit breaker; <see cref="JobSettlement"/>). The caller holds the job lock. Returns the job status after.
    /// </summary>
    public static async Task<JobStatus> SettleAsync(
        WorkspaceTransaction tx, LockedJob locked, JobDelta delta, CancellationToken cancellationToken)
    {
        var before = locked.Job.Counters;
        var counters = before with
        {
            ChunksCommitted = before.ChunksCommitted + delta.Committed,
            ChunksFailed = before.ChunksFailed + delta.Failed - delta.Replayed,
            ChunksCancelled = before.ChunksCancelled + delta.Cancelled,
            ItemsApplied = before.ItemsApplied + delta.ItemsApplied,
            ItemsUnchanged = before.ItemsUnchanged + delta.ItemsUnchanged,
            ItemsSkippedConcurrentEdit = before.ItemsSkippedConcurrentEdit + delta.ItemsSkippedConcurrentEdit,
            ItemsExcludedNoAccess = before.ItemsExcludedNoAccess + delta.ItemsExcludedNoAccess,
            ItemsFailed = before.ItemsFailed + delta.ItemsFailed,
            IndexTasksTotal = before.IndexTasksTotal + delta.IndexTasks,
        };

        var consecutive = locked.ConsecutiveFailures;
        var lastClass = locked.LastFailureClass;
        if (delta.Failed > 0 && delta.FailureClass is { } failureClass)
        {
            consecutive = (lastClass == failureClass ? consecutive : 0) + delta.Failed;
            lastClass = failureClass;
        }
        else if (delta.Committed > 0 || delta.Replayed > 0)
        {
            consecutive = 0;
        }

        var status = locked.Job.Status;
        string? reason = null;
        if (JobSettlement.Next(status, counters, consecutive) is { } trigger)
        {
            status = JobStateMachine.Transition(status, trigger);
            if (trigger == JobTrigger.Pause)
            {
                reason = $"Circuit breaker: {counters.ChunksFailed} failed chunks, {consecutive} consecutive ({lastClass}).";
            }
        }

        await using var command = tx.Command(
            """
            UPDATE opportunity.job SET
                chunks_committed = @committed, chunks_failed = @failed, chunks_cancelled = @cancelled,
                items_applied = @applied, items_unchanged = @unchanged, items_skipped_concurrent_edit = @skipped,
                items_excluded_no_access = @excluded, items_failed = @items_failed, index_tasks_total = @index_tasks,
                consecutive_failures = @consecutive, last_failure_class = @last_class,
                status = @status, status_reason = coalesce(@reason, status_reason),
                finished_at = CASE WHEN @finished THEN coalesce(finished_at, now()) END,
                updated_at = now()
            WHERE workspace_id = @ws AND job_id = @job AND status = @from
            """);
        command.Parameters.AddWithValue("committed", (int)counters.ChunksCommitted);
        command.Parameters.AddWithValue("failed", (int)counters.ChunksFailed);
        command.Parameters.AddWithValue("cancelled", (int)counters.ChunksCancelled);
        command.Parameters.AddWithValue("applied", counters.ItemsApplied);
        command.Parameters.AddWithValue("unchanged", counters.ItemsUnchanged);
        command.Parameters.AddWithValue("skipped", counters.ItemsSkippedConcurrentEdit);
        command.Parameters.AddWithValue("excluded", counters.ItemsExcludedNoAccess);
        command.Parameters.AddWithValue("items_failed", counters.ItemsFailed);
        command.Parameters.AddWithValue("index_tasks", counters.IndexTasksTotal);
        command.Parameters.AddWithValue("consecutive", consecutive);
        command.Parameters.Add(new NpgsqlParameter("last_class", NpgsqlDbType.Smallint) { Value = lastClass is { } c ? (short)c : DBNull.Value });
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue("from", locked.Job.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });
        command.Parameters.AddWithValue("finished", JobStateMachine.IsFinished(status));
        command.Parameters.AddWithValue("ws", locked.Job.WorkspaceId);
        command.Parameters.AddWithValue("job", locked.Job.JobId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The locked job row changed under its lock.");
        }

        if (status == JobStatus.CompletedWithErrors && locked.Job.Status != JobStatus.CompletedWithErrors)
        {
            await AuditAsync(tx, locked.Job, AuditTaxonomy.Job.CompletedWithErrors, null, cancellationToken,
                AuditOutcome.Failure, "ChunksFailed", CountDetails(counters)).ConfigureAwait(false);
        }

        return status;
    }

    /// <summary>Service identity of job-engine actions taken on behalf of the job's initiator (ADR-013 §4).</summary>
    public const string JobEngineActor = "service:jobs";

    /// <summary>
    /// Writes a <c>Job.*</c> audit event (ADR-013 §5) in the caller's transaction, so the job change and its event commit
    /// together. <paramref name="userId"/> is the acting user; without one the job engine acts on behalf of the
    /// initiator. IDs, enums and counts only: status reasons can hold free text and are not copied (ADR-013 §7).
    /// </summary>
    public static async Task AuditAsync(
        WorkspaceTransaction tx, JobInfo job, string action, Guid? userId, CancellationToken cancellationToken,
        AuditOutcome outcome = AuditOutcome.Success, string? reasonCode = null, IReadOnlyDictionary<string, string?>? details = null)
    {
        string? display = null;
        if (userId is { } user)
        {
            await using var lookup = tx.Command("SELECT coalesce(display_name, subject) FROM opportunity.app_user WHERE user_id = @user");
            lookup.Parameters.AddWithValue("user", user);
            display = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        var allDetails = new Dictionary<string, string?> { ["JobType"] = job.JobType.ToString() };
        foreach (var (key, value) in details ?? new Dictionary<string, string?>())
        {
            allDetails[key] = value;
        }

        await AuditSql.InsertAsync(tx, new AuditEvent
        {
            WorkspaceId = job.WorkspaceId,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Job.Category,
            Action = action,
            ActorType = userId is null ? AuditActorType.Service : AuditActorType.User,
            ActorId = userId?.ToString() ?? JobEngineActor,
            ActorDisplay = userId is null ? "Job engine" : Truncate(display ?? userId.Value.ToString(), AuditEventRules.MaxActorDisplayLength),
            OnBehalfOf = userId is null ? job.InitiatedBy : null,
            ResourceType = "Job",
            ResourceId = job.JobId.ToString(),
            Outcome = outcome,
            ReasonCode = reasonCode,
            CorrelationId = string.IsNullOrEmpty(job.CorrelationId) ? null : job.CorrelationId,
            JobId = job.JobId,
            SnapshotId = job.TargetSnapshotId,
            Details = allDetails,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string?> CountDetails(JobCounters counters) => new()
    {
        ["ChunksTotal"] = counters.ChunksTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ChunksCommitted"] = counters.ChunksCommitted.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ChunksFailed"] = counters.ChunksFailed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ChunksCancelled"] = counters.ChunksCancelled.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ItemsApplied"] = counters.ItemsApplied.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ItemsFailed"] = counters.ItemsFailed.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Cancels every open (not Running) chunk of the job; returns how many.</summary>
    public static async Task<int> CancelOpenChunksAsync(
        WorkspaceTransaction tx, Guid workspaceId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            $"""
            UPDATE opportunity.job_chunk SET status = @to, {ChunkSet(JobChunkTrigger.Cancel)}
            WHERE workspace_id = @ws AND job_id = @job AND status = ANY(@sources)
            """);
        command.Parameters.AddWithValue("to", (short)JobChunkStatus.Cancelled);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("sources",
            Sources(JobChunkTrigger.Cancel).Where(s => s != (short)JobChunkStatus.Running).ToArray());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static JobChunkInfo ReadChunk(NpgsqlDataReader reader, int offset = 0)
    {
        var o = offset;
        var membership = ChunkMembership.Restore(
            (ChunkMembershipKind)reader.GetInt16(o + 5),
            NullableGuid(reader, o + 6),
            NullableGuid(reader, o + 7),
            NullableInt64(reader, o + 8),
            NullableInt64(reader, o + 9),
            NullableInt64(reader, o + 10),
            NullableGuid(reader, o + 11),
            NullableGuid(reader, o + 12),
            reader.IsDBNull(o + 13) ? null : reader.GetFieldValue<Guid[]>(o + 13));
        return new JobChunkInfo
        {
            WorkspaceId = reader.GetGuid(o),
            JobId = reader.GetGuid(o + 1),
            ChunkId = reader.GetGuid(o + 2),
            Sequence = reader.GetInt32(o + 3),
            Status = (JobChunkStatus)reader.GetInt16(o + 4),
            Membership = membership,
            ItemCount = reader.GetInt32(o + 14),
            EstimatedBytes = NullableInt64(reader, o + 15),
            IdempotencyKey = reader.GetString(o + 16),
            AttemptCount = reader.GetInt32(o + 17),
            MaxAttempts = reader.GetInt16(o + 18),
            AvailableAt = reader.GetFieldValue<DateTimeOffset>(o + 19),
            LeaseOwner = reader.IsDBNull(o + 20) ? null : reader.GetString(o + 20),
            LeaseExpiresAt = NullableTime(reader, o + 21),
            LeaseToken = reader.GetInt64(o + 22),
            LastError = reader.IsDBNull(o + 23) ? null : reader.GetString(o + 23),
            ErrorClass = reader.IsDBNull(o + 24) ? null : (ChunkErrorClass)reader.GetInt16(o + 24),
            ErrorCode = reader.IsDBNull(o + 25) ? null : reader.GetString(o + 25),
            ReplayCount = reader.GetInt32(o + 26),
            ClaimedAt = NullableTime(reader, o + 27),
            SettledAt = NullableTime(reader, o + 28),
        };
    }

    public static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    public static string? TruncateOrNull(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private static LockedJob ReadLockedJob(NpgsqlDataReader reader)
    {
        var job = new JobInfo
        {
            WorkspaceId = reader.GetGuid(0),
            JobId = reader.GetGuid(1),
            JobType = Enum.Parse<JobType>(reader.GetString(2)),
            Status = Enum.Parse<JobStatus>(reader.GetString(3)),
            StatusReason = reader.IsDBNull(4) ? null : reader.GetString(4),
            TargetSnapshotId = NullableGuid(reader, 5),
            ImportBatchId = NullableGuid(reader, 6),
            Parameters = JsonNode.Parse(reader.GetString(7))!.AsObject(),
            InitiatedBy = reader.GetGuid(8),
            ClientIdempotencyKey = reader.IsDBNull(9) ? null : reader.GetString(9),
            CorrelationId = reader.IsDBNull(10) ? null : reader.GetString(10),
            OperationKind = reader.IsDBNull(11) ? null : Enum.Parse<ChunkOperationKind>(reader.GetString(11)),
            ProjectionGeneration = reader.GetInt64(12),
            MaxAttemptsPerChunk = reader.GetInt16(13),
            JobGeneration = NullableInt64(reader, 14),
            Counters = new JobCounters
            {
                ChunksTotal = reader.GetInt32(15),
                ChunksCommitted = reader.GetInt32(16),
                ChunksFailed = reader.GetInt32(17),
                ChunksCancelled = reader.GetInt32(18),
                ItemsApplied = reader.GetInt64(19),
                ItemsUnchanged = reader.GetInt64(20),
                ItemsSkippedConcurrentEdit = reader.GetInt64(21),
                ItemsExcludedNoAccess = reader.GetInt64(22),
                ItemsFailed = reader.GetInt64(23),
                IndexTasksTotal = reader.GetInt64(24),
                IndexTasksApplied = reader.GetInt64(25),
            },
            CancelRequestedBy = NullableGuid(reader, 26),
            CancelRequestedAt = NullableTime(reader, 27),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(28),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(29),
            StartedAt = NullableTime(reader, 30),
            FinishedAt = NullableTime(reader, 31),
        };
        return new LockedJob(
            job,
            reader.GetString(34),
            reader.GetInt32(32),
            reader.IsDBNull(33) ? null : (ChunkErrorClass)reader.GetInt16(33));
    }

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? NullableTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
