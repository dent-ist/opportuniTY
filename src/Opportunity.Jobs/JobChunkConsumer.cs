using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif

namespace Opportunity.Jobs;

/// <summary>Identity of this worker process in chunk leases.</summary>
public sealed class JobChunkConsumerOptions
{
    public const int MaxWorkerIdLength = 200;

    /// <summary>Lease owner recorded on claimed chunks (diagnostics only; the fencing token is what counts).</summary>
    public string WorkerId { get; init; } = string.Create(
        CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
}

/// <summary>
/// The idempotent consumer for <see cref="JobChunkMessage"/> (E06-T05, ADR-010 §3/§5/§7/§9, ADR-015 D9). Per delivery:
/// <list type="number">
/// <item>Envelope validation: the hinted workspace and job must be present.</item>
/// <item>Claim under RLS with the hinted workspace — the claim is the inbox (conditional transition, ADR-010 §5.3).
/// An invisible row, or one whose job, operation, sequence or idempotency key disagrees with the envelope, is
/// rejected: <c>Integrity.MessageRejected</c> audit event (reason <c>EnvelopeMismatch</c>), dead-lettered, nothing written. Every other unclaimable
/// outcome (duplicate, settled, lease held, job paused or cancelling, workspace not Active) is acked and dropped.</item>
/// <item>The executor for the chunk's <see cref="ChunkOperationKind"/> runs with workspace, actor and parameters from
/// PostgreSQL, under a heartbeat that extends the lease and stops the work at fence F2.</item>
/// <item>The outcome is recorded in PostgreSQL (commit with fence F3, retry wait, failure with <c>LastError</c>, or
/// release at a fence) and only then does the handler return, so the transport acks after the commit.</item>
/// </list>
/// Transport-level retry happens only when PostgreSQL cannot be reached (the exception escapes). On shutdown the
/// chunk is released without charging the attempt and the delivery is requeued. Tracing: the transport's process span
/// covers this handler, so only the metrics part of worker telemetry is recorded here.
/// </summary>
public sealed partial class JobChunkConsumer : IMessageHandler<JobChunkMessage>
{
    public const string RejectionReason = MessageRejectionReasons.EnvelopeMismatch;

    private readonly IJobChunkRepository _chunks;
    private readonly Dictionary<ChunkOperationKind, IJobChunkExecutor> _executors;
    private readonly IAuditEventWriter _audit;
    private readonly JobLeaseOptions _lease;
    private readonly JobChunkConsumerOptions _options;
    private readonly IMessageProcessingMeter _meter;
    private readonly OpportunityMetrics? _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<JobChunkConsumer> _logger;
#if OPPORTUNITY_FAILPOINTS
    private readonly IFaultInjector? _faults;
#endif

    public JobChunkConsumer(
        IJobChunkRepository chunks,
        IEnumerable<IJobChunkExecutor> executors,
        IAuditEventWriter audit,
        JobLeaseOptions lease,
        JobChunkConsumerOptions options,
        IMessageProcessingMeter meter,
        TimeProvider time,
        ILogger<JobChunkConsumer> logger,
        OpportunityMetrics? metrics = null
#if OPPORTUNITY_FAILPOINTS
        , IFaultInjector? faults = null
#endif
        )
    {
        _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        ArgumentNullException.ThrowIfNull(executors);
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
#if OPPORTUNITY_FAILPOINTS
        _faults = faults;
#endif
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkerId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.WorkerId.Length, JobChunkConsumerOptions.MaxWorkerIdLength);

        _executors = [];
        foreach (var executor in executors)
        {
            if (!_executors.TryAdd(executor.OperationKind, executor))
            {
                throw new InvalidOperationException($"More than one executor is registered for {executor.OperationKind}.");
            }
        }
    }

    public async Task HandleAsync(JobChunkMessage payload, ReceivedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(message);

        using var measurement = _meter.Measure(message.Queue.WorkerType, message.Envelope.MessageType, message.Queue.Name);
        try
        {
            if (await ProcessAsync(payload, message, cancellationToken).ConfigureAwait(false) is { } errorType)
            {
                measurement.Fail(errorType);
            }
        }
        catch (Exception ex)
        {
            measurement.Fail(ex is PermanentMessageException ? RejectionReason : ex.GetType().Name);
            throw;
        }
    }

    /// <returns>The error class to report on the processing metrics, or null for success and harmless drops.</returns>
    private async Task<string?> ProcessAsync(JobChunkMessage payload, ReceivedMessage message, CancellationToken cancellationToken)
    {
        var envelope = message.Envelope;
        if (envelope.WorkspaceId is not { } workspaceId || workspaceId == Guid.Empty)
        {
            await RejectAsync(payload, message, "workspaceId", "The envelope names no workspace.").ConfigureAwait(false);
            return RejectionReason;
        }

        if (envelope.JobId is not { } jobId || jobId == Guid.Empty)
        {
            await RejectAsync(payload, message, "jobId", "The envelope names no job.").ConfigureAwait(false);
            return RejectionReason;
        }

#if OPPORTUNITY_FAILPOINTS
        await HitAsync(Failpoints.BeforeClaim, message, null, cancellationToken).ConfigureAwait(false);
#endif

        // A failure to reach PostgreSQL escapes: the transport retries the delivery (ADR-010 §7.1).
        var claim = await _chunks.ClaimAsync(workspaceId, payload.ChunkId, _options.WorkerId, _lease.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (claim.Outcome == ChunkClaimOutcome.NotFound)
        {
            // Invisible under RLS with the hinted workspace: a foreign workspace, a forged or a stale message.
            await RejectAsync(payload, message, "workspaceId", "No such chunk in the hinted workspace.").ConfigureAwait(false);
            return RejectionReason;
        }

        if (!claim.Claimed)
        {
            LogNotClaimed(_logger, payload.ChunkId, claim.Outcome);
            var exhausted = claim.Outcome == ChunkClaimOutcome.AttemptsExhausted;
            RecordChunk(jobType: null, exhausted ? ChunkOutcomes.Failed : ChunkOutcomes.Skipped,
                exhausted ? nameof(ChunkErrorClass.AttemptsExhausted) : null, started: null);
            return exhausted ? nameof(ChunkErrorClass.AttemptsExhausted) : null;
        }

        var chunk = claim.Chunk!;
        if (Mismatch(chunk, payload, message, workspaceId, jobId) is { } field)
        {
            await RejectAsync(payload, message, field, $"The envelope's {field} disagrees with the chunk row.", chunk.Lease)
                .ConfigureAwait(false);
            return RejectionReason;
        }

        var started = _time.GetTimestamp();
        if (!_executors.TryGetValue(chunk.OperationKind, out var executor))
        {
            // A routing or deployment error no retry fixes; visible to operators as a failed chunk.
            LogNoExecutor(_logger, chunk.OperationKind, message.Queue.Name);
            var error = ChunkError.Permanent("NoExecutor", $"No executor for {chunk.OperationKind} on queue {message.Queue.Name}.");
            return await RecordFailureAsync(chunk, error, started).ConfigureAwait(false);
        }

#if OPPORTUNITY_FAILPOINTS
        await HitAsync(Failpoints.AfterClaim, message, chunk.Lease, cancellationToken).ConfigureAwait(false);
#endif

        return await ExecuteAsync(executor, chunk, message, started, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ExecuteAsync(
        IJobChunkExecutor executor, ClaimedChunk chunk, ReceivedMessage message, long started, CancellationToken cancellationToken)
    {
        var fence = new FenceState();
        ChunkExecutionResult? result = null;
        Exception? failure = null;
        var timedOut = false;
        using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var stopHeartbeat = new CancellationTokenSource())
        {
            execution.CancelAfter(_lease.MaxChunkRuntime);
            var heartbeat = HeartbeatAsync(chunk.Lease, fence, execution, stopHeartbeat.Token);
            try
            {
                var context = new ChunkExecutionContext(chunk, message, ct => CheckFenceAsync(chunk.Lease, fence, ct));
                result = await executor.ExecuteAsync(context, execution.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"{executor.GetType().Name} returned no result.");
            }
#pragma warning disable CA1031 // Every executor failure is classified and recorded in PostgreSQL (the retry ledger).
#if OPPORTUNITY_FAILPOINTS
            catch (Exception ex) when (ex is not SimulatedCrashException)
#else
            catch (Exception ex)
#endif
#pragma warning restore CA1031
            {
                failure = ex;
                timedOut = execution.IsCancellationRequested && !cancellationToken.IsCancellationRequested && fence.Value == ChunkFence.Proceed;
            }
            finally
            {
                await stopHeartbeat.CancelAsync().ConfigureAwait(false);
                await heartbeat.ConfigureAwait(false);
            }
        }

        if (failure is not null)
        {
            return await SettleFailureAsync(chunk, failure, fence.Value, timedOut, started, cancellationToken).ConfigureAwait(false);
        }

        ChunkCommitResult commit;
        if (result!.CommitResult is { } committed)
        {
            commit = committed;
        }
        else
        {
#if OPPORTUNITY_FAILPOINTS
            await HitAsync(Failpoints.BeforeCommit, message, chunk.Lease, cancellationToken).ConfigureAwait(false);
#endif

            // Not the delivery's token: once the work is done, a shutdown must not abandon its commit.
            commit = await _chunks.CompleteAsync(chunk.Lease, result.Completion!, CancellationToken.None).ConfigureAwait(false);
        }

#if OPPORTUNITY_FAILPOINTS
        await HitAsync(Failpoints.AfterCommit, message, chunk.Lease, cancellationToken).ConfigureAwait(false);
#endif

        switch (commit.Outcome)
        {
            case ChunkCommitOutcome.Committed:
                RecordChunk(chunk.JobType, ChunkOutcomes.Committed, null, started);
                RecordJob(chunk.JobType, commit.JobStatus);
                return null;
            case ChunkCommitOutcome.LeaseLost:
                LogLeaseLost(_logger, chunk.Lease.ChunkId, chunk.Lease.LeaseToken);
                RecordChunk(chunk.JobType, ChunkOutcomes.Skipped, nameof(ChunkFence.LeaseLost), started);
                return null;
            default:
                // F3 stopped the commit: the chunk went back to Pending (job paused, workspace not Active) or was cancelled.
                LogCommitFenced(_logger, chunk.Lease.ChunkId, commit.Outcome);
                RecordChunk(chunk.JobType, ChunkOutcomes.Released, null, started);
                RecordJob(chunk.JobType, commit.JobStatus);
                return null;
        }
    }

    private async Task<string?> SettleFailureAsync(
        ClaimedChunk chunk, Exception failure, ChunkFence observed, bool timedOut, long started, CancellationToken cancellationToken)
    {
        var fence = failure is ChunkFencedException fenced ? fenced.Fence : observed;
        if (fence != ChunkFence.Proceed && failure is ChunkFencedException or OperationCanceledException)
        {
            if (fence == ChunkFence.LeaseLost)
            {
                // Another worker owns the chunk now; this attempt may write nothing more.
                LogLeaseLost(_logger, chunk.Lease.ChunkId, chunk.Lease.LeaseToken);
                RecordChunk(chunk.JobType, ChunkOutcomes.Skipped, nameof(ChunkFence.LeaseLost), started);
                return null;
            }

            var released = await _chunks.ReleaseAsync(chunk.Lease, CancellationToken.None).ConfigureAwait(false);
            LogFenced(_logger, chunk.Lease.ChunkId, fence, released);
            RecordChunk(chunk.JobType, ChunkOutcomes.Released, null, started);
            return null;
        }

        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // Shutdown: give the chunk back without charging the attempt, then let the transport requeue the delivery.
            try
            {
                await _chunks.ReleaseAsync(chunk.Lease, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best effort: the lease expires and the sweeper recovers the chunk anyway.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReleaseFailed(_logger, chunk.Lease.ChunkId, ex);
            }

            RecordChunk(chunk.JobType, ChunkOutcomes.Released, null, started);
            ExceptionDispatchInfo.Throw(failure);
        }

        var error = timedOut && failure is OperationCanceledException
            ? ChunkError.Transient("ChunkRuntimeExceeded", $"The chunk ran longer than {_lease.MaxChunkRuntime}.")
            : ChunkErrorClassifier.Classify(failure);
        LogExecutorFailed(_logger, chunk.Lease.ChunkId, error.Class, error.Code, failure);
        return await RecordFailureAsync(chunk, error, started).ConfigureAwait(false);
    }

    /// <summary>Records the failed attempt in PostgreSQL; if that fails the exception escapes to the transport retry.</summary>
    private async Task<string> RecordFailureAsync(ClaimedChunk chunk, ChunkError error, long started)
    {
        var result = await _chunks.FailAsync(chunk.Lease, error, CancellationToken.None).ConfigureAwait(false);
        var outcome = result.Outcome switch
        {
            ChunkFailureOutcome.RetryScheduled => ChunkOutcomes.Retry,
            ChunkFailureOutcome.Failed => ChunkOutcomes.Failed,
            ChunkFailureOutcome.Cancelled => ChunkOutcomes.Released,
            _ => ChunkOutcomes.Skipped,
        };
        RecordChunk(chunk.JobType, outcome, error.Class.ToString(), started);
        RecordJob(chunk.JobType, result.JobStatus);
        return error.Class.ToString();
    }

    private async Task<ChunkFence> CheckFenceAsync(ChunkLease lease, FenceState fence, CancellationToken cancellationToken)
    {
        var heartbeat = await _chunks.HeartbeatAsync(lease, _lease.LeaseDuration, cancellationToken).ConfigureAwait(false);
        fence.Observe(heartbeat.Fence);
        return heartbeat.Fence;
    }

    /// <summary>Extends the lease every heartbeat interval; a fence other than Proceed cancels the executor.</summary>
    private async Task HeartbeatAsync(ChunkLease lease, FenceState fence, CancellationTokenSource execution, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_lease.HeartbeatInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                ChunkFence observed;
                try
                {
                    observed = await CheckFenceAsync(lease, fence, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep working: fence F3 at commit is authoritative, and the lease covers a short outage.
                    LogHeartbeatFailed(_logger, lease.ChunkId, ex);
                    continue;
                }

                if (observed != ChunkFence.Proceed)
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The executor finished.
        }
    }

    private static string? Mismatch(ClaimedChunk chunk, JobChunkMessage payload, ReceivedMessage message, Guid workspaceId, Guid jobId)
    {
        if (chunk.Lease.WorkspaceId != workspaceId)
        {
            return "workspaceId";
        }

        if (chunk.Lease.JobId != jobId)
        {
            return "jobId";
        }

        if (!string.Equals(chunk.OperationKind.ToString(), payload.Operation.ToString(), StringComparison.Ordinal))
        {
            return "operation";
        }

        if (chunk.Sequence != payload.Sequence)
        {
            return "sequence";
        }

        return string.Equals(chunk.IdempotencyKey, message.Envelope.IdempotencyKey, StringComparison.Ordinal) ? null : "idempotencyKey";
    }

    /// <summary>
    /// ADR-015 D9.3: installation-level audit event, then dead-letter. Nothing of the chunk is written; a claim the
    /// check needed is given back (after the audit, so a failing release cannot drop the evidence).
    /// </summary>
    private async Task RejectAsync(
        JobChunkMessage payload, ReceivedMessage message, string field, string detail, ChunkLease? release = null)
    {
        var envelope = message.Envelope;
        LogRejected(_logger, payload.ChunkId, envelope.WorkspaceId, envelope.JobId, field);
        await _audit.WriteAsync(
            MessageRejection.AuditEvent(
                envelope,
                message.Queue,
                MessageRejectionReasons.EnvelopeMismatch,
                _options.WorkerId,
                "JobChunk",
                payload.ChunkId.ToString(),
                _time.GetUtcNow(),
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["mismatch"] = field }),
            CancellationToken.None).ConfigureAwait(false);

        if (release is not null)
        {
            await _chunks.ReleaseAsync(release, CancellationToken.None).ConfigureAwait(false);
        }

        RecordChunk(jobType: null, ChunkOutcomes.Rejected, RejectionReason, started: null);
        throw new PermanentMessageException($"{RejectionReason} ({field}): {detail}");
    }

    private void RecordChunk(JobType? jobType, string outcome, string? errorType, long? started)
    {
        if (_metrics is null)
        {
            return;
        }

        var tags = new TagList { { TelemetryAttributes.Outcome, outcome } };
        if (jobType is { } type)
        {
            tags.Add(TelemetryAttributes.JobType, type.ToString());
        }

        if (errorType is not null)
        {
            tags.Add(TelemetryAttributes.ErrorType, errorType);
        }

        _metrics.Counter(OpportunityMetricCatalog.JobChunks).Add(1, tags);
        if (started is { } at && jobType is { } durationType)
        {
            _metrics.Histogram(OpportunityMetricCatalog.JobChunkDuration).Record(
                _time.GetElapsedTime(at).TotalSeconds,
                new TagList { { TelemetryAttributes.JobType, durationType.ToString() }, { TelemetryAttributes.Outcome, outcome } });
        }
    }

    /// <summary>
    /// Job transitions this consumer causes: the claim required a Running job, so a status other than Running after
    /// the chunk settled is the transition (completion, circuit-breaker pause, or the cancel it finished).
    /// </summary>
    private void RecordJob(JobType jobType, JobStatus? status)
    {
        if (_metrics is null || status is null or JobStatus.Running or JobStatus.Cancelling)
        {
            return;
        }

        _metrics.Counter(OpportunityMetricCatalog.Jobs).Add(1, new TagList
        {
            { TelemetryAttributes.JobType, jobType.ToString() },
            { TelemetryAttributes.Status, status.Value.ToString() },
        });
    }

#if OPPORTUNITY_FAILPOINTS
    private ValueTask HitAsync(string failpoint, ReceivedMessage message, ChunkLease? lease, CancellationToken cancellationToken) =>
        _faults?.HitAsync(failpoint, new FailpointContext(message, lease), cancellationToken) ?? ValueTask.CompletedTask;
#endif

    /// <summary>The latest fence seen by a heartbeat or an executor's fence check.</summary>
    private sealed class FenceState
    {
        private int _value = (int)ChunkFence.Proceed;

        public ChunkFence Value => (ChunkFence)Volatile.Read(ref _value);

        public void Observe(ChunkFence fence)
        {
            if (fence != ChunkFence.Proceed)
            {
                Interlocked.CompareExchange(ref _value, (int)fence, (int)ChunkFence.Proceed);
            }
        }
    }

    /// <summary>Values of the <c>opportunity.outcome</c> attribute on <c>opportunity.job.chunks</c>.</summary>
    public static class ChunkOutcomes
    {
        public const string Committed = "committed";
        public const string Retry = "retry";
        public const string Failed = "failed";

        /// <summary>Not claimed or no longer owned: a duplicate, an early redelivery, a lost lease.</summary>
        public const string Skipped = "skipped";

        /// <summary>Stopped at a fence or by shutdown: back to Pending (no attempt charged) or cancelled.</summary>
        public const string Released = "released";

        /// <summary>The envelope disagreed with PostgreSQL: dead-lettered and audited.</summary>
        public const string Rejected = "rejected";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chunk {ChunkId} not claimed ({Outcome}); acking the delivery")]
    private static partial void LogNotClaimed(ILogger logger, Guid chunkId, ChunkClaimOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejecting a message for chunk {ChunkId}: envelope workspace {WorkspaceId} job {JobId} disagrees with PostgreSQL ({Field})")]
    private static partial void LogRejected(ILogger logger, Guid chunkId, Guid? workspaceId, Guid? jobId, string field);

    [LoggerMessage(Level = LogLevel.Error, Message = "No executor for {OperationKind} on {Queue}; failing the chunk")]
    private static partial void LogNoExecutor(ILogger logger, ChunkOperationKind operationKind, string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chunk {ChunkId} lost its lease (token {LeaseToken}); nothing recorded")]
    private static partial void LogLeaseLost(ILogger logger, Guid chunkId, long leaseToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chunk {ChunkId} stopped at a fence: {Fence}, {Release}")]
    private static partial void LogFenced(ILogger logger, Guid chunkId, ChunkFence fence, ChunkReleaseOutcome release);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chunk {ChunkId} was not committed: {Outcome}")]
    private static partial void LogCommitFenced(ILogger logger, Guid chunkId, ChunkCommitOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chunk {ChunkId} failed ({ErrorClass} {ErrorCode})")]
    private static partial void LogExecutorFailed(ILogger logger, Guid chunkId, ChunkErrorClass errorClass, string errorCode, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release chunk {ChunkId} on shutdown; the lease sweeper will recover it")]
    private static partial void LogReleaseFailed(ILogger logger, Guid chunkId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat for chunk {ChunkId} failed; continuing until the next one")]
    private static partial void LogHeartbeatFailed(ILogger logger, Guid chunkId, Exception exception);
}
