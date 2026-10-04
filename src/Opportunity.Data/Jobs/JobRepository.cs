using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Data.Audit;

namespace Opportunity.Data.Jobs;

/// <summary>PostgreSQL implementation of <see cref="IJobRepository"/> (ADR-010). Every call is one workspace transaction.</summary>
public sealed class JobRepository(NpgsqlDataSource dataSource) : IJobRepository
{
    public async Task<JobCreation> CreateAsync(NewJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.ClientIdempotencyKey is { Length: 0 or > NewJob.MaxClientIdempotencyKeyLength })
        {
            throw new ArgumentException($"A client idempotency key has 1 to {NewJob.MaxClientIdempotencyKeyLength} characters.", nameof(job));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(job.MaxAttemptsPerChunk, nameof(job));
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, job.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var creation = await CreateInTransactionAsync(tx, job, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return creation;
    }

    /// <summary>
    /// <see cref="CreateAsync"/> inside the caller's transaction, for stores that create a job together with the rows it
    /// works on (an import batch). The caller has validated <paramref name="job"/> and commits.
    /// </summary>
    internal static async Task<JobCreation> CreateInTransactionAsync(WorkspaceTransaction tx, NewJob job, CancellationToken cancellationToken)
    {
        bool created;
        Guid jobId;
        await using (var insert = tx.Command(
            """
            INSERT INTO opportunity.job (workspace_id, job_id, job_type, target_snapshot_id, import_batch_id, parameters,
                                         initiated_by, client_idempotency_key, correlation_id, max_attempts)
            VALUES (@ws, @job, @type, @snapshot, @batch, @parameters::jsonb, @initiated_by, @client_key, @correlation, @max_attempts)
            ON CONFLICT (workspace_id, initiated_by, client_idempotency_key) WHERE client_idempotency_key IS NOT NULL DO NOTHING
            RETURNING job_id
            """))
        {
            insert.Parameters.AddWithValue("ws", job.WorkspaceId);
            insert.Parameters.AddWithValue("job", job.JobId);
            insert.Parameters.AddWithValue("type", job.JobType.ToString());
            insert.Parameters.Add(new NpgsqlParameter("snapshot", NpgsqlDbType.Uuid) { Value = (object?)job.TargetSnapshotId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Uuid) { Value = (object?)job.ImportBatchId ?? DBNull.Value });
            insert.Parameters.AddWithValue("parameters", (job.Parameters ?? new JsonObject()).ToJsonString());
            insert.Parameters.AddWithValue("initiated_by", job.InitiatedBy);
            insert.Parameters.Add(new NpgsqlParameter("client_key", NpgsqlDbType.Text) { Value = (object?)job.ClientIdempotencyKey ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("correlation", NpgsqlDbType.Text) { Value = (object?)job.CorrelationId ?? DBNull.Value });
            insert.Parameters.AddWithValue("max_attempts", checked((short)job.MaxAttemptsPerChunk));
            var inserted = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            created = inserted is Guid;
            jobId = inserted as Guid? ?? Guid.Empty;
        }

        if (!created)
        {
            await using var existing = tx.Command(
                """
                SELECT job_id FROM opportunity.job
                WHERE workspace_id = @ws AND initiated_by = @initiated_by AND client_idempotency_key = @client_key
                """);
            existing.Parameters.AddWithValue("ws", job.WorkspaceId);
            existing.Parameters.AddWithValue("initiated_by", job.InitiatedBy);
            existing.Parameters.AddWithValue("client_key", job.ClientIdempotencyKey!);
            jobId = (Guid)(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var info = await JobSql.ReadJobAsync(tx, job.WorkspaceId, jobId, cancellationToken).ConfigureAwait(false);
        if (created)
        {
            await JobSql.AuditAsync(tx, info!, AuditTaxonomy.Job.Created, info!.InitiatedBy, cancellationToken).ConfigureAwait(false);
            if (job.SubmissionAudit is { } submission)
            {
                await AuditSql.InsertAsync(tx, submission with
                {
                    WorkspaceId = job.WorkspaceId,
                    JobId = jobId,
                    SnapshotId = submission.SnapshotId ?? job.TargetSnapshotId,
                    ResourceType = submission.ResourceType ?? "Job",
                    ResourceId = submission.ResourceId ?? jobId.ToString(),
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        return new JobCreation(info!, created);
    }

    public Task<JobTransitionResult> BeginPreparingAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default) =>
        TransitionAsync(workspaceId, jobId, JobTrigger.BeginPreparing, null, cancellationToken);

    public async Task<JobTransitionResult> StartAsync(JobStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Chunks);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ProjectionGeneration);
        if (request.Chunks.Any(c => c is null || c.ItemCount < 0 || c.EstimatedBytes < 0))
        {
            throw new ArgumentException("Every chunk needs a membership and non-negative counts.", nameof(request));
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var locked = await JobSql.LockJobAsync(tx, request.WorkspaceId, request.JobId, cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return new JobTransitionResult(JobTransitionOutcome.NotFound, null);
        }

        if (!JobStateMachine.TryTransition(locked.Job.Status, JobTrigger.Start, out _))
        {
            return new JobTransitionResult(JobTransitionOutcome.NotAllowed, locked.Job.Status);
        }

        await InsertChunksAsync(tx, locked.Job, request, cancellationToken).ConfigureAwait(false);
        var status = await JobSql.TransitionJobAsync(tx, locked, JobTrigger.Start, null, cancellationToken,
            ", operation_kind = @operation_kind, projection_generation = @projection_generation, chunks_total = @total",
            c =>
            {
                c.Parameters.AddWithValue("operation_kind", request.OperationKind.ToString());
                c.Parameters.AddWithValue("projection_generation", request.ProjectionGeneration);
                c.Parameters.AddWithValue("total", request.Chunks.Count);
            }).ConfigureAwait(false);

        if (request.Chunks.Count == 0)
        {
            var started = (await JobSql.LockJobAsync(tx, request.WorkspaceId, request.JobId, cancellationToken).ConfigureAwait(false))!;
            status = await JobSql.SettleAsync(tx, started, new JobDelta(), cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobTransitionResult(JobTransitionOutcome.Applied, status);
    }

    public async Task<JobTransitionResult> FailAsync(Guid workspaceId, Guid jobId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var locked = await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
        if (Refuse(locked, JobTrigger.Fail) is { } refused)
        {
            return refused;
        }

        var status = await JobSql.TransitionJobAsync(tx, locked!, JobTrigger.Fail, reason, cancellationToken).ConfigureAwait(false);
        var cancelled = await JobSql.CancelOpenChunksAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
        if (cancelled > 0)
        {
            var failed = (await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false))!;
            status = await JobSql.SettleAsync(tx, failed, new JobDelta { Cancelled = cancelled }, cancellationToken).ConfigureAwait(false);
        }

        await JobSql.AuditAsync(tx, locked!.Job, AuditTaxonomy.Job.Failed, null, cancellationToken, AuditOutcome.Failure, "JobFailed")
            .ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobTransitionResult(JobTransitionOutcome.Applied, status);
    }

    public Task<JobTransitionResult> PauseAsync(Guid workspaceId, Guid jobId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return TransitionAsync(workspaceId, jobId, JobTrigger.Pause, reason, cancellationToken);
    }

    public Task<JobTransitionResult> ResumeAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default) =>
        TransitionAsync(workspaceId, jobId, JobTrigger.Resume, null, cancellationToken, ", consecutive_failures = 0");

    public async Task<JobTransitionResult> CancelAsync(
        Guid workspaceId, Guid jobId, Guid requestedBy, string? reason = null, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var locked = await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
        if (Refuse(locked, JobTrigger.Cancel) is { } refused)
        {
            return refused;
        }

        var status = await JobSql.TransitionJobAsync(tx, locked!, JobTrigger.Cancel, reason ?? "Cancelled by user.", cancellationToken,
            ", cancel_requested_by = @requested_by, cancel_requested_at = now()",
            c => c.Parameters.AddWithValue("requested_by", requestedBy)).ConfigureAwait(false);

        if (status == JobStatus.Cancelling)
        {
            var cancelled = await JobSql.CancelOpenChunksAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
            var cancelling = (await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false))!;
            status = await JobSql.SettleAsync(tx, cancelling, new JobDelta { Cancelled = cancelled }, cancellationToken).ConfigureAwait(false);
        }

        await JobSql.AuditAsync(tx, locked!.Job, AuditTaxonomy.Job.Cancelled, requestedBy, cancellationToken,
            details: new Dictionary<string, string?> { ["Status"] = status.ToString() }).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobTransitionResult(JobTransitionOutcome.Applied, status);
    }

    public async Task<ChunkReplayResult> ReplayFailedChunksAsync(
        Guid workspaceId, Guid jobId, Guid? chunkId = null, Guid? requestedBy = null, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var locked = await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
        if (locked is null)
        {
            return new ChunkReplayResult(JobTransitionOutcome.NotFound, 0, null);
        }

        // Replay feeds chunks back to a job that is still executing, or reopens one that completed with errors.
        if (locked.Job.Status is not (JobStatus.Running or JobStatus.Paused or JobStatus.CompletedWithErrors))
        {
            return new ChunkReplayResult(JobTransitionOutcome.NotAllowed, 0, locked.Job.Status);
        }

        int replayed;
        await using (var command = tx.Command(
            $"""
            UPDATE opportunity.job_chunk SET status = @to, {JobSql.ChunkSet(JobChunkTrigger.Replay)}
            WHERE workspace_id = @ws AND job_id = @job AND status = ANY(@sources) AND (@chunk::uuid IS NULL OR chunk_id = @chunk)
            """))
        {
            command.Parameters.AddWithValue("to", (short)JobChunkStateMachine.TargetOf(JobChunkTrigger.Replay));
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("job", jobId);
            command.Parameters.AddWithValue("sources", JobSql.Sources(JobChunkTrigger.Replay));
            command.Parameters.Add(new NpgsqlParameter("chunk", NpgsqlDbType.Uuid) { Value = (object?)chunkId ?? DBNull.Value });
            replayed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var status = locked.Job.Status;
        if (replayed > 0)
        {
            if (status == JobStatus.CompletedWithErrors)
            {
                status = await JobSql.TransitionJobAsync(tx, locked, JobTrigger.ReplayFailedChunks, "Failed chunks replayed.", cancellationToken)
                    .ConfigureAwait(false);
                locked = (await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false))!;
            }

            status = await JobSql.SettleAsync(tx, locked, new JobDelta { Replayed = replayed }, cancellationToken).ConfigureAwait(false);
            await JobSql.AuditAsync(tx, locked.Job, AuditTaxonomy.Job.Replayed, requestedBy, cancellationToken, details: new Dictionary<string, string?>
            {
                ["ChunksReplayed"] = replayed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ChunkId"] = chunkId?.ToString(),
            }).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChunkReplayResult(JobTransitionOutcome.Applied, replayed, status);
    }

    public async Task RecordIndexTasksAppliedAsync(Guid workspaceId, Guid jobId, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            """
            UPDATE opportunity.job SET index_tasks_applied = least(index_tasks_total, index_tasks_applied + @count), updated_at = now()
            WHERE workspace_id = @ws AND job_id = @job
            """);
        command.Parameters.AddWithValue("count", (long)count);
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("job", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobInfo?> GetAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var job = await JobSql.ReadJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<IReadOnlyList<JobChunkInfo>> GetChunksAsync(
        Guid workspaceId, Guid jobId, JobChunkStatus? status = null, int afterSequence = 0, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var chunks = new List<JobChunkInfo>();
        await using (var command = tx.Command(
            $"""
            SELECT {JobSql.ChunkColumns} FROM opportunity.job_chunk c
            WHERE c.workspace_id = @ws AND c.job_id = @job AND c.chunk_sequence > @after
              AND (@status::smallint IS NULL OR c.status = @status)
            ORDER BY c.chunk_sequence
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("job", jobId);
            command.Parameters.AddWithValue("after", afterSequence);
            command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint) { Value = status is { } s ? (short)s : DBNull.Value });
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(JobSql.ReadChunk(reader));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return chunks;
    }

    public async Task<IReadOnlyList<StoredJobItemResult>> GetItemResultsAsync(
        JobItemResultQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, query.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var results = new List<StoredJobItemResult>();
        await using (var command = tx.Command(
            """
            SELECT r.chunk_id, c.chunk_sequence, r.item_no, r.kind, r.document_id, r.row_no, r.field_id, r.reason_code, r.detail
            FROM opportunity.job_chunk_item_result r
            JOIN opportunity.job_chunk c ON c.workspace_id = r.workspace_id AND c.chunk_id = r.chunk_id
            WHERE r.workspace_id = @ws AND r.job_id = @job AND (@kind::smallint IS NULL OR r.kind = @kind)
              AND (c.chunk_sequence, r.item_no) > (@after_sequence, @after_item)
            ORDER BY c.chunk_sequence, r.item_no
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", query.WorkspaceId);
            command.Parameters.AddWithValue("job", query.JobId);
            command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Smallint) { Value = query.Kind is { } k ? (short)k : DBNull.Value });
            command.Parameters.AddWithValue("after_sequence", query.After?.ChunkSequence ?? 0);
            command.Parameters.AddWithValue("after_item", query.After?.ItemNo ?? 0);
            command.Parameters.AddWithValue("limit", query.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new StoredJobItemResult(
                    reader.GetGuid(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    new JobItemResult(
                        (JobItemResultKind)reader.GetInt16(3),
                        reader.IsDBNull(4) ? null : reader.GetGuid(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6),
                        reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8))));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private async Task<JobTransitionResult> TransitionAsync(
        Guid workspaceId, Guid jobId, JobTrigger trigger, string? reason, CancellationToken cancellationToken, string extraSet = "")
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var locked = await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false);
        if (Refuse(locked, trigger) is { } refused)
        {
            return refused;
        }

        var status = await JobSql.TransitionJobAsync(tx, locked!, trigger, reason, cancellationToken, extraSet).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new JobTransitionResult(JobTransitionOutcome.Applied, status);
    }

    private static JobTransitionResult? Refuse(LockedJob? locked, JobTrigger trigger) =>
        locked is null
            ? new JobTransitionResult(JobTransitionOutcome.NotFound, null)
            : JobStateMachine.TryTransition(locked.Job.Status, trigger, out _)
                ? null
                : new JobTransitionResult(JobTransitionOutcome.NotAllowed, locked.Job.Status);

    private static async Task InsertChunksAsync(
        WorkspaceTransaction tx, JobInfo job, JobStartRequest request, CancellationToken cancellationToken)
    {
        var chunks = request.Chunks;
        var ranged = Enumerable.Range(0, chunks.Count).Where(i => chunks[i].Membership.Kind != ChunkMembershipKind.ExplicitIds).ToArray();
        if (ranged.Length > 0)
        {
            await using var command = tx.Command(
                """
                INSERT INTO opportunity.job_chunk (workspace_id, chunk_id, job_id, chunk_sequence, membership_kind, snapshot_id,
                    import_batch_id, range_from, range_to, projection_generation, document_id_from, document_id_to,
                    item_count, estimated_bytes, idempotency_key, max_attempts)
                SELECT @ws, t.chunk_id, @job, t.seq, t.kind, t.snapshot_id, t.import_batch_id, t.range_from, t.range_to,
                       t.projection_generation, t.document_id_from, t.document_id_to, t.item_count, t.estimated_bytes, t.key,
                       @max_attempts
                FROM unnest(@chunk_ids, @seqs, @kinds, @snapshot_ids, @batch_ids, @range_froms, @range_tos, @generations,
                            @doc_froms, @doc_tos, @item_counts, @bytes, @keys)
                    AS t(chunk_id, seq, kind, snapshot_id, import_batch_id, range_from, range_to, projection_generation,
                         document_id_from, document_id_to, item_count, estimated_bytes, key)
                """);
            command.CommandTimeout = 0;
            AddCommon(command, job);
            command.Parameters.AddWithValue("chunk_ids", ranged.Select(_ => Guid.CreateVersion7()).ToArray());
            command.Parameters.AddWithValue("seqs", ranged.Select(i => i + 1).ToArray());
            command.Parameters.AddWithValue("kinds", ranged.Select(i => (short)chunks[i].Membership.Kind).ToArray());
            command.Parameters.AddWithValue("snapshot_ids", ranged.Select(i => chunks[i].Membership.SnapshotId).ToArray());
            command.Parameters.AddWithValue("batch_ids", ranged.Select(i => chunks[i].Membership.ImportBatchId).ToArray());
            command.Parameters.AddWithValue("range_froms", ranged.Select(i => chunks[i].Membership.RangeFrom).ToArray());
            command.Parameters.AddWithValue("range_tos", ranged.Select(i => chunks[i].Membership.RangeTo).ToArray());
            command.Parameters.AddWithValue("generations", ranged.Select(i => chunks[i].Membership.ProjectionGeneration).ToArray());
            command.Parameters.AddWithValue("doc_froms", ranged.Select(i => chunks[i].Membership.DocumentIdFrom).ToArray());
            command.Parameters.AddWithValue("doc_tos", ranged.Select(i => chunks[i].Membership.DocumentIdTo).ToArray());
            command.Parameters.AddWithValue("item_counts", ranged.Select(i => chunks[i].ItemCount).ToArray());
            command.Parameters.AddWithValue("bytes", ranged.Select(i => chunks[i].EstimatedBytes).ToArray());
            command.Parameters.AddWithValue("keys", ranged.Select(i => Key(job, request, i + 1)).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var i in Enumerable.Range(0, chunks.Count).Except(ranged))
        {
            await using var command = tx.Command(
                """
                INSERT INTO opportunity.job_chunk (workspace_id, chunk_id, job_id, chunk_sequence, membership_kind, document_ids,
                    item_count, estimated_bytes, idempotency_key, max_attempts)
                VALUES (@ws, @chunk_id, @job, @seq, @kind, @document_ids, @item_count, @bytes, @key, @max_attempts)
                """);
            AddCommon(command, job);
            command.Parameters.AddWithValue("chunk_id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("seq", i + 1);
            command.Parameters.AddWithValue("kind", (short)ChunkMembershipKind.ExplicitIds);
            command.Parameters.AddWithValue("document_ids", chunks[i].Membership.DocumentIds!.ToArray());
            command.Parameters.AddWithValue("item_count", chunks[i].ItemCount);
            command.Parameters.Add(new NpgsqlParameter("bytes", NpgsqlDbType.Bigint) { Value = (object?)chunks[i].EstimatedBytes ?? DBNull.Value });
            command.Parameters.AddWithValue("key", Key(job, request, i + 1));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Key(JobInfo job, JobStartRequest request, int sequence) =>
        ChunkIdempotencyKey.ForChunk(job.WorkspaceId, job.JobId, sequence, request.OperationKind, request.ProjectionGeneration);

    private static void AddCommon(NpgsqlCommand command, JobInfo job)
    {
        command.Parameters.AddWithValue("ws", job.WorkspaceId);
        command.Parameters.AddWithValue("job", job.JobId);
        command.Parameters.AddWithValue("max_attempts", (short)job.MaxAttemptsPerChunk);
    }
}
