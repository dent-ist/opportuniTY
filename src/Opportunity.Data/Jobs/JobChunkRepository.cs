using Npgsql;

using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Workspaces;

namespace Opportunity.Data.Jobs;

/// <summary>
/// PostgreSQL implementation of <see cref="IJobChunkRepository"/>: claims with leases and fencing tokens, fences F1–F3,
/// attempt counting with backoff, lease recovery (ADR-010 §2–§3, §7).
/// </summary>
public sealed class JobChunkRepository(NpgsqlDataSource dataSource) : IJobChunkRepository
{
    private const int MaxWorkerIdLength = 200;

    private static readonly string ClaimByIdSql =
        $"""
        UPDATE opportunity.job_chunk c SET status = 3, {JobSql.ChunkSet(JobChunkTrigger.Claim)}
        WHERE c.workspace_id = @ws AND c.chunk_id = @chunk AND c.attempt_count < c.max_attempts
          AND ((c.status = ANY(@sources) AND c.available_at <= now()) OR (c.status = 3 AND c.lease_expires_at < now()))
        RETURNING {JobSql.ChunkColumns}
        """;

    // F1 without the job lock: a single statement that locks one chunk row, skipping rows other transactions hold.
    // The authoritative check is F3 at commit.
    private static readonly string ClaimNextSql =
        $"""
        WITH candidate AS (
            SELECT c.workspace_id, c.chunk_id
            FROM opportunity.job_chunk c
            WHERE c.workspace_id = @ws AND c.job_id = @job AND c.status = ANY(@sources) AND c.available_at <= now()
              AND c.attempt_count < c.max_attempts
              AND EXISTS (SELECT FROM opportunity.job j
                          WHERE j.workspace_id = c.workspace_id AND j.job_id = c.job_id AND j.status = 'Running')
              AND EXISTS (SELECT FROM opportunity.workspace w
                          WHERE w.workspace_id = c.workspace_id AND w.status = '{nameof(WorkspaceStatus.Active)}')
            ORDER BY c.chunk_sequence
            LIMIT 1
            FOR UPDATE OF c SKIP LOCKED)
        UPDATE opportunity.job_chunk c SET status = 3, {JobSql.ChunkSet(JobChunkTrigger.Claim)}
        FROM candidate
        WHERE c.workspace_id = candidate.workspace_id AND c.chunk_id = candidate.chunk_id
        RETURNING {JobSql.ChunkColumns}
        """;

    public async Task<ChunkClaimResult> ClaimAsync(
        Guid workspaceId, Guid chunkId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ValidateClaim(workerId, leaseDuration);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        Guid jobId;
        await using (var find = tx.Command("SELECT job_id FROM opportunity.job_chunk WHERE workspace_id = @ws AND chunk_id = @chunk"))
        {
            find.Parameters.AddWithValue("ws", workspaceId);
            find.Parameters.AddWithValue("chunk", chunkId);
            if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid found)
            {
                return new ChunkClaimResult(ChunkClaimOutcome.NotFound);
            }

            jobId = found;
        }

        var job = (await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false))!;
        if (job.Runnable && await TryClaimByIdAsync(tx, job, chunkId, workerId, leaseDuration, cancellationToken).ConfigureAwait(false) is { } claimed)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChunkClaimResult(ChunkClaimOutcome.Claimed, claimed);
        }

        var outcome = await DiagnoseUnclaimableAsync(tx, job, chunkId, cancellationToken).ConfigureAwait(false);
        if (outcome is null && job.Runnable)
        {
            // The chunk became claimable between the claim and the diagnosis (a concurrent release); try once more.
            claimed = await TryClaimByIdAsync(tx, job, chunkId, workerId, leaseDuration, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimed is null ? new ChunkClaimResult(ChunkClaimOutcome.LeaseHeld) : new ChunkClaimResult(ChunkClaimOutcome.Claimed, claimed);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChunkClaimResult(outcome ?? ChunkClaimOutcome.JobNotRunning);
    }

    public async Task<ChunkClaimResult> ClaimNextAsync(
        Guid workspaceId, Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ValidateClaim(workerId, leaseDuration);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        JobChunkInfo? chunk = null;
        await using (var claim = tx.Command(ClaimNextSql))
        {
            claim.Parameters.AddWithValue("ws", workspaceId);
            claim.Parameters.AddWithValue("job", jobId);
            AddClaimParameters(claim, workerId, leaseDuration);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                chunk = JobSql.ReadChunk(reader);
            }
        }

        if (chunk is null)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChunkClaimResult(ChunkClaimOutcome.NoneAvailable);
        }

        var job = (await JobSql.ReadJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false))!;
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChunkClaimResult(ChunkClaimOutcome.Claimed, ToClaimed(job, chunk, workerId));
    }

    public async Task<ChunkHeartbeat> HeartbeatAsync(ChunkLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await using var command = tx.Command(
            $"""
            WITH extended AS (
                UPDATE opportunity.job_chunk SET lease_expires_at = now() + @lease, updated_at = now()
                WHERE workspace_id = @ws AND chunk_id = @chunk AND lease_token = @token AND status = 3
                RETURNING workspace_id, job_id, lease_expires_at)
            SELECT e.lease_expires_at, j.status, w.status
            FROM extended e
            JOIN opportunity.job j ON j.workspace_id = e.workspace_id AND j.job_id = e.job_id
            JOIN opportunity.workspace w ON w.workspace_id = e.workspace_id
            """);
        command.Parameters.AddWithValue("lease", leaseDuration);
        AddLease(command, lease);
        ChunkHeartbeat heartbeat;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                heartbeat = new ChunkHeartbeat(ChunkFence.LeaseLost, null);
            }
            else
            {
                var jobStatus = Enum.Parse<JobStatus>(reader.GetString(1));
                var fence = jobStatus switch
                {
                    JobStatus.Cancelling or JobStatus.Failed or JobStatus.Cancelled => ChunkFence.JobCancelling,
                    JobStatus.Running when reader.GetString(2) == nameof(WorkspaceStatus.Active) => ChunkFence.Proceed,
                    _ => ChunkFence.JobNotRunning,
                };
                heartbeat = new ChunkHeartbeat(fence, reader.GetFieldValue<DateTimeOffset>(0));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return heartbeat;
    }

    public async Task<ChunkCommitResult> CompleteAsync(ChunkLease lease, ChunkCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var result = await CommitInTransactionAsync(tx, lease, completion, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is ChunkCommitOutcome.JobNotRunning or ChunkCommitOutcome.Cancelled)
        {
            // Nothing of the chunk's work happened in this transaction, so the fence outcome is recorded right here.
            var release = await ReleaseInTransactionAsync(tx, lease, cancellationToken).ConfigureAwait(false);
            result = release.Outcome switch
            {
                ChunkReleaseOutcome.Cancelled => new ChunkCommitResult(ChunkCommitOutcome.Cancelled, release.JobStatus),
                ChunkReleaseOutcome.ReturnedToPending => new ChunkCommitResult(ChunkCommitOutcome.JobNotRunning, release.JobStatus),
                _ => new ChunkCommitResult(ChunkCommitOutcome.LeaseLost, release.JobStatus),
            };
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Fence F3 inside a caller's transaction, for Data-layer chunk executors whose writes must commit atomically with
    /// the chunk (bulk coding, import). Call it last, after the chunk's own writes; on any outcome but
    /// <see cref="ChunkCommitOutcome.Committed"/> the caller must roll back and then call <see cref="ReleaseAsync"/>
    /// (unless the lease was lost).
    /// </summary>
    internal static async Task<ChunkCommitResult> CommitInTransactionAsync(
        WorkspaceTransaction tx, ChunkLease lease, ChunkCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ValidateItemResults(completion.ItemResults);

        // Job row first (FOR NO KEY UPDATE, serializes with cancel/pause), workspace row shared (deletion).
        var job = await JobSql.LockJobAsync(tx, lease.WorkspaceId, lease.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return new ChunkCommitResult(ChunkCommitOutcome.LeaseLost, null);
        }

        if (!job.Runnable)
        {
            return new ChunkCommitResult(
                job.CancelsChunks ? ChunkCommitOutcome.Cancelled : ChunkCommitOutcome.JobNotRunning, job.Job.Status);
        }

        if (!await JobSql.TransitionChunkAsync(tx, lease.WorkspaceId, lease.ChunkId, JobChunkTrigger.Commit, lease.LeaseToken, cancellationToken)
                .ConfigureAwait(false))
        {
            return new ChunkCommitResult(ChunkCommitOutcome.LeaseLost, job.Job.Status);
        }

        await InsertItemResultsAsync(tx, lease, completion.ItemResults, cancellationToken).ConfigureAwait(false);
        var results = completion.ItemResults;
        var status = await JobSql.SettleAsync(tx, job, new JobDelta
        {
            Committed = 1,
            ItemsApplied = completion.ItemsApplied,
            ItemsUnchanged = completion.ItemsUnchanged,
            ItemsSkippedConcurrentEdit = results.Count(r => r.Kind == JobItemResultKind.SkippedConcurrentEdit),
            ItemsExcludedNoAccess = results.Count(r => r.Kind == JobItemResultKind.ExcludedNoAccess),
            ItemsFailed = results.Count(r => r.Kind == JobItemResultKind.Failed),
            IndexTasks = completion.IndexTasks,
        }, cancellationToken).ConfigureAwait(false);
        return new ChunkCommitResult(ChunkCommitOutcome.Committed, status);
    }

    public async Task<ChunkFailureResult> FailAsync(ChunkLease lease, ChunkError failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Code);
        if (failure.Class == ChunkErrorClass.AttemptsExhausted)
        {
            throw new ArgumentException("AttemptsExhausted is recorded by claims and the sweeper, not by workers.", nameof(failure));
        }

        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var job = await JobSql.LockJobAsync(tx, lease.WorkspaceId, lease.JobId, cancellationToken).ConfigureAwait(false);
        var attempt = job is null ? null : await ReadLeasedAttemptAsync(tx, lease, cancellationToken).ConfigureAwait(false);
        if (job is null || attempt is null)
        {
            return new ChunkFailureResult(ChunkFailureOutcome.LeaseLost, null, job?.Job.Status);
        }

        var trigger = ChunkRetryPolicy.OnError(failure.Class, attempt.Value.AttemptCount, attempt.Value.MaxAttempts);
        if (trigger == JobChunkTrigger.RetryLater && job.CancelsChunks)
        {
            trigger = JobChunkTrigger.Cancel;
        }

        var backoff = ChunkRetryPolicy.Backoff(Math.Max(1, attempt.Value.AttemptCount), Random.Shared.NextDouble());
        await JobSql.TransitionChunkAsync(tx, lease.WorkspaceId, lease.ChunkId, trigger, lease.LeaseToken, cancellationToken, c =>
        {
            if (trigger != JobChunkTrigger.Cancel)
            {
                JobSql.AddError(c, failure.Class, failure.Code, failure.Message);
            }

            if (trigger == JobChunkTrigger.RetryLater)
            {
                c.Parameters.AddWithValue("backoff", backoff);
            }
        }).ConfigureAwait(false);

        ChunkFailureResult result;
        switch (trigger)
        {
            case JobChunkTrigger.RetryLater:
                result = new ChunkFailureResult(ChunkFailureOutcome.RetryScheduled,
                    await ReadAvailableAtAsync(tx, lease, cancellationToken).ConfigureAwait(false), job.Job.Status);
                break;
            case JobChunkTrigger.Cancel:
                result = new ChunkFailureResult(ChunkFailureOutcome.Cancelled, null,
                    await JobSql.SettleAsync(tx, job, new JobDelta { Cancelled = 1 }, cancellationToken).ConfigureAwait(false));
                break;
            default:
                result = new ChunkFailureResult(ChunkFailureOutcome.Failed, null,
                    await JobSql.SettleAsync(tx, job, new JobDelta { Failed = 1, FailureClass = failure.Class }, cancellationToken).ConfigureAwait(false));
                break;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ChunkReleaseOutcome> ReleaseAsync(ChunkLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, lease.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var result = await ReleaseInTransactionAsync(tx, lease, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.Outcome;
    }

    public async Task<bool> MarkDispatchedAsync(Guid workspaceId, Guid chunkId, CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var changed = await JobSql.TransitionChunkAsync(tx, workspaceId, chunkId, JobChunkTrigger.Dispatch, null, cancellationToken)
            .ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<IReadOnlyList<DispatchableChunk>> GetDispatchableAsync(
        Guid workspaceId, Guid jobId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
        var chunks = new List<DispatchableChunk>();
        await using (var command = tx.Command(
            """
            SELECT c.chunk_id, c.chunk_sequence, c.status, c.idempotency_key, c.attempt_count
            FROM opportunity.job_chunk c
            WHERE c.workspace_id = @ws AND c.job_id = @job
              AND (c.status = 1 OR (c.status = 4 AND c.available_at <= now()))
            ORDER BY c.chunk_sequence
            LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("ws", workspaceId);
            command.Parameters.AddWithValue("job", jobId);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(new DispatchableChunk(workspaceId, jobId, reader.GetGuid(0), reader.GetInt32(1),
                    (JobChunkStatus)reader.GetInt16(2), reader.GetString(3), reader.GetInt32(4)));
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return chunks;
    }

    public async Task<IReadOnlyList<Guid>> GetWorkspacesToSweepAsync(CancellationToken cancellationToken = default)
    {
        // The workspace registry is installation-level (ADR-015 D7.1): readable without a workspace context.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT workspace_id FROM opportunity.workspace WHERE status <> '{nameof(WorkspaceStatus.Purged)}' ORDER BY workspace_id",
            connection, transaction);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public async Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
        Guid workspaceId, TimeSpan grace, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Stuck chunks: Running with an expired lease, or open with no attempts left (a crash on the last attempt).
        const string Stuck =
            """
            c.workspace_id = @ws AND ((c.status = 3 AND c.lease_expires_at < now() - @grace)
                                      OR (c.status = ANY(@open) AND c.attempt_count >= c.max_attempts))
            """;
        var jobs = new List<Guid>();
        await using (var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false))
        {
            await using var find = tx.Command($"SELECT DISTINCT c.job_id FROM opportunity.job_chunk c WHERE {Stuck} LIMIT @limit");
            AddStuckParameters(find, workspaceId, grace);
            find.Parameters.AddWithValue("limit", limit);
            await using (var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    jobs.Add(reader.GetGuid(0));
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var total = LeaseRecoveryResult.None;
        foreach (var jobId in jobs)
        {
            await using var tx = await WorkspaceTransaction.BeginAsync(dataSource, workspaceId, cancellationToken).ConfigureAwait(false);
            var job = (await JobSql.LockJobAsync(tx, workspaceId, jobId, cancellationToken).ConfigureAwait(false))!;

            // Running → Cancelled (job cancelling), → Failed (attempts used up), → Pending (re-dispatch); open → Failed.
            await using var recover = tx.Command(
                $"""
                WITH stuck AS (
                    SELECT c.workspace_id, c.chunk_id, c.status, c.attempt_count >= c.max_attempts AS exhausted
                    FROM opportunity.job_chunk c
                    WHERE {Stuck} AND c.job_id = @job
                    FOR UPDATE OF c SKIP LOCKED)
                UPDATE opportunity.job_chunk c SET
                    status = CASE WHEN @cancel AND s.status = 3 THEN @cancelled WHEN s.exhausted THEN @failed ELSE @pending END,
                    lease_owner = NULL, lease_expires_at = NULL, available_at = now(), updated_at = now(),
                    settled_at = CASE WHEN (@cancel AND s.status = 3) OR s.exhausted THEN now() END,
                    error_class = CASE WHEN s.exhausted AND NOT (@cancel AND s.status = 3) THEN @exhausted_class ELSE c.error_class END,
                    error_code = CASE WHEN s.exhausted AND NOT (@cancel AND s.status = 3) THEN 'AttemptsExhausted' ELSE c.error_code END,
                    last_error = CASE WHEN s.exhausted AND NOT (@cancel AND s.status = 3)
                                      THEN 'No attempts left after the lease expired (worker crash or timeout).' ELSE c.last_error END
                FROM stuck s
                WHERE c.workspace_id = s.workspace_id AND c.chunk_id = s.chunk_id
                RETURNING c.status
                """);
            AddStuckParameters(recover, workspaceId, grace);
            recover.Parameters.AddWithValue("job", jobId);
            recover.Parameters.AddWithValue("cancel", job.CancelsChunks);
            recover.Parameters.AddWithValue("cancelled", (short)JobChunkStateMachine.TargetOf(JobChunkTrigger.Cancel));
            recover.Parameters.AddWithValue("failed", (short)JobChunkStateMachine.TargetOf(JobChunkTrigger.ExhaustAttempts));
            recover.Parameters.AddWithValue("pending", (short)JobChunkStateMachine.TargetOf(JobChunkTrigger.ExpireLease));
            recover.Parameters.AddWithValue("exhausted_class", (short)ChunkErrorClass.AttemptsExhausted);

            int pending = 0, failed = 0, cancelled = 0;
            await using (var reader = await recover.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    switch ((JobChunkStatus)reader.GetInt16(0))
                    {
                        case JobChunkStatus.Pending: pending++; break;
                        case JobChunkStatus.Failed: failed++; break;
                        default: cancelled++; break;
                    }
                }
            }

            if (failed + cancelled > 0)
            {
                await JobSql.SettleAsync(tx, job, new JobDelta
                {
                    Failed = failed,
                    Cancelled = cancelled,
                    FailureClass = failed > 0 ? ChunkErrorClass.AttemptsExhausted : null,
                }, cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            total = total.Add(new LeaseRecoveryResult(pending, failed, cancelled));
        }

        return total;
    }

    private static async Task<(ChunkReleaseOutcome Outcome, JobStatus? JobStatus)> ReleaseInTransactionAsync(
        WorkspaceTransaction tx, ChunkLease lease, CancellationToken cancellationToken)
    {
        var job = await JobSql.LockJobAsync(tx, lease.WorkspaceId, lease.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return (ChunkReleaseOutcome.LeaseLost, null);
        }

        var trigger = job.CancelsChunks ? JobChunkTrigger.Cancel : JobChunkTrigger.Yield;
        if (!await JobSql.TransitionChunkAsync(tx, lease.WorkspaceId, lease.ChunkId, trigger, lease.LeaseToken, cancellationToken)
                .ConfigureAwait(false))
        {
            return (ChunkReleaseOutcome.LeaseLost, job.Job.Status);
        }

        if (trigger == JobChunkTrigger.Yield)
        {
            return (ChunkReleaseOutcome.ReturnedToPending, job.Job.Status);
        }

        var status = await JobSql.SettleAsync(tx, job, new JobDelta { Cancelled = 1 }, cancellationToken).ConfigureAwait(false);
        return (ChunkReleaseOutcome.Cancelled, status);
    }

    private static async Task<ClaimedChunk?> TryClaimByIdAsync(
        WorkspaceTransaction tx, LockedJob job, Guid chunkId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var claim = tx.Command(ClaimByIdSql);
        claim.Parameters.AddWithValue("ws", job.Job.WorkspaceId);
        claim.Parameters.AddWithValue("chunk", chunkId);
        AddClaimParameters(claim, workerId, leaseDuration);
        await using var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ToClaimed(job.Job, JobSql.ReadChunk(reader), workerId)
            : null;
    }

    /// <summary>
    /// Why a chunk could not be claimed, applying the F1 and attempt-limit transitions on the way. Null when the chunk
    /// turns out to be claimable after all.
    /// </summary>
    private static async Task<ChunkClaimOutcome?> DiagnoseUnclaimableAsync(
        WorkspaceTransaction tx, LockedJob job, Guid chunkId, CancellationToken cancellationToken)
    {
        JobChunkInfo chunk;
        bool leaseLive, due;
        await using (var read = tx.Command(
            $"""
            SELECT {JobSql.ChunkColumns}, (c.status = 3 AND c.lease_expires_at >= now()), c.available_at <= now()
            FROM opportunity.job_chunk c WHERE c.workspace_id = @ws AND c.chunk_id = @chunk
            FOR UPDATE OF c
            """))
        {
            read.Parameters.AddWithValue("ws", job.Job.WorkspaceId);
            read.Parameters.AddWithValue("chunk", chunkId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            chunk = JobSql.ReadChunk(reader);
            leaseLive = reader.GetBoolean(JobSql.ChunkColumnCount);
            due = reader.GetBoolean(JobSql.ChunkColumnCount + 1);
        }

        if (JobChunkStateMachine.IsSettled(chunk.Status))
        {
            return ChunkClaimOutcome.AlreadySettled;
        }

        if (leaseLive)
        {
            return ChunkClaimOutcome.LeaseHeld;
        }

        var ws = job.Job.WorkspaceId;
        if (job.CancelsChunks)
        {
            await JobSql.TransitionChunkAsync(tx, ws, chunkId, JobChunkTrigger.Cancel, null, cancellationToken).ConfigureAwait(false);
            await JobSql.SettleAsync(tx, job, new JobDelta { Cancelled = 1 }, cancellationToken).ConfigureAwait(false);
            return ChunkClaimOutcome.Cancelled;
        }

        if (!job.Runnable)
        {
            // F1 with the job paused (or the workspace not Active): the message is acked, so the chunk must be
            // re-dispatched after resume. Running chunks with an expired lease are left to the sweeper.
            if (chunk.Status == JobChunkStatus.Dispatched)
            {
                await JobSql.TransitionChunkAsync(tx, ws, chunkId, JobChunkTrigger.Yield, null, cancellationToken).ConfigureAwait(false);
            }

            return ChunkClaimOutcome.JobNotRunning;
        }

        if (chunk.AttemptCount >= chunk.MaxAttempts)
        {
            await JobSql.TransitionChunkAsync(tx, ws, chunkId, JobChunkTrigger.ExhaustAttempts, null, cancellationToken, c =>
                JobSql.AddError(c, ChunkErrorClass.AttemptsExhausted, "AttemptsExhausted",
                    $"No attempts left ({chunk.AttemptCount} of {chunk.MaxAttempts}) when the chunk was claimed again."))
                .ConfigureAwait(false);
            await JobSql.SettleAsync(tx, job, new JobDelta { Failed = 1, FailureClass = ChunkErrorClass.AttemptsExhausted }, cancellationToken)
                .ConfigureAwait(false);
            return ChunkClaimOutcome.AttemptsExhausted;
        }

        return due ? null : ChunkClaimOutcome.NotDue;
    }

    private static async Task<(int AttemptCount, int MaxAttempts)?> ReadLeasedAttemptAsync(
        WorkspaceTransaction tx, ChunkLease lease, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT attempt_count, max_attempts FROM opportunity.job_chunk
            WHERE workspace_id = @ws AND chunk_id = @chunk AND lease_token = @token AND status = 3
            FOR UPDATE
            """);
        AddLease(command, lease);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetInt32(0), reader.GetInt16(1)) : null;
    }

    private static async Task<DateTimeOffset> ReadAvailableAtAsync(WorkspaceTransaction tx, ChunkLease lease, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            "SELECT available_at FROM opportunity.job_chunk WHERE workspace_id = @ws AND chunk_id = @chunk");
        command.Parameters.AddWithValue("ws", lease.WorkspaceId);
        command.Parameters.AddWithValue("chunk", lease.ChunkId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task InsertItemResultsAsync(
        WorkspaceTransaction tx, ChunkLease lease, IReadOnlyList<JobItemResult> results, CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        await using var command = tx.Command(
            """
            INSERT INTO opportunity.job_chunk_item_result
                (workspace_id, job_id, chunk_id, item_no, kind, document_id, row_no, field_id, reason_code, detail)
            SELECT @ws, @job, @chunk, t.ordinality::integer, t.kind, t.document_id, t.row_no, t.field_id, t.reason_code, t.detail
            FROM unnest(@kinds, @document_ids, @row_nos, @field_ids, @reason_codes, @details) WITH ORDINALITY
                AS t(kind, document_id, row_no, field_id, reason_code, detail, ordinality)
            """);
        command.Parameters.AddWithValue("ws", lease.WorkspaceId);
        command.Parameters.AddWithValue("job", lease.JobId);
        command.Parameters.AddWithValue("chunk", lease.ChunkId);
        command.Parameters.AddWithValue("kinds", results.Select(r => (short)r.Kind).ToArray());
        command.Parameters.AddWithValue("document_ids", results.Select(r => r.DocumentId).ToArray());
        command.Parameters.AddWithValue("row_nos", results.Select(r => r.RowNo).ToArray());
        command.Parameters.AddWithValue("field_ids", results.Select(r => r.FieldId).ToArray());
        command.Parameters.AddWithValue("reason_codes", results.Select(r => r.ReasonCode).ToArray());
        command.Parameters.AddWithValue("details", results.Select(r => JobSql.TruncateOrNull(r.Detail, JobItemResult.MaxDetailLength)).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ClaimedChunk ToClaimed(JobInfo job, JobChunkInfo chunk, string workerId) => new()
    {
        Lease = new ChunkLease(chunk.WorkspaceId, chunk.JobId, chunk.ChunkId, chunk.LeaseToken, workerId),
        JobType = job.JobType,
        OperationKind = job.OperationKind!.Value,
        InitiatedBy = job.InitiatedBy,
        TargetSnapshotId = job.TargetSnapshotId,
        ImportBatchId = job.ImportBatchId,
        Parameters = job.Parameters,
        CorrelationId = job.CorrelationId,
        Sequence = chunk.Sequence,
        Membership = chunk.Membership,
        ItemCount = chunk.ItemCount,
        EstimatedBytes = chunk.EstimatedBytes,
        IdempotencyKey = chunk.IdempotencyKey,
        AttemptCount = chunk.AttemptCount,
        MaxAttempts = chunk.MaxAttempts,
        LeaseExpiresAt = chunk.LeaseExpiresAt!.Value,
    };

    private static void ValidateClaim(string workerId, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerId.Length, MaxWorkerIdLength, nameof(workerId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
    }

    private static void ValidateItemResults(IReadOnlyList<JobItemResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var result in results)
        {
            if (result is null || string.IsNullOrWhiteSpace(result.ReasonCode) || result.ReasonCode.Length > JobItemResult.MaxReasonCodeLength
                || (result.DocumentId is null && result.RowNo is null) || !Enum.IsDefined(result.Kind))
            {
                throw new ArgumentException(
                    "Every item result needs a kind, a reason code of 1-100 characters and a document id or row number.", nameof(results));
            }
        }
    }

    private static void AddClaimParameters(NpgsqlCommand command, string workerId, TimeSpan leaseDuration)
    {
        command.Parameters.AddWithValue("worker", workerId);
        command.Parameters.AddWithValue("lease", leaseDuration);
        command.Parameters.AddWithValue("sources", JobSql.Sources(JobChunkTrigger.Claim));
    }

    private static void AddLease(NpgsqlCommand command, ChunkLease lease)
    {
        command.Parameters.AddWithValue("ws", lease.WorkspaceId);
        command.Parameters.AddWithValue("chunk", lease.ChunkId);
        command.Parameters.AddWithValue("token", lease.LeaseToken);
    }

    private static void AddStuckParameters(NpgsqlCommand command, Guid workspaceId, TimeSpan grace)
    {
        command.Parameters.AddWithValue("ws", workspaceId);
        command.Parameters.AddWithValue("grace", grace);
        command.Parameters.AddWithValue("open", JobSql.Sources(JobChunkTrigger.Cancel).Where(s => s != (short)JobChunkStatus.Running).ToArray());
    }
}
