using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Search.Writing;

namespace Opportunity.Search.Workers;

/// <summary>
/// The chunk index worker (E07-T04, baseline §21 steps 1–6, ADR-001, ADR-010): consumes <see cref="IndexChunkTaskMessage"/>
/// from the security-bulk (L2) and bulk (L3) lanes. Per delivery:
/// <list type="number">
/// <item>Lease the task under RLS with the hinted workspace — the lease is the inbox (ADR-010 §5.3). An invisible task,
/// or one whose job or idempotency key disagrees with the envelope, is rejected (<c>Integrity.MessageRejected</c>, reason <c>EnvelopeMismatch</c>
/// audit event, dead-lettered). Every other unleasable outcome (duplicate delivery, settled, lease held, not due,
/// workspace not Active, attempts exhausted) is acked and dropped.</item>
/// <item>Resolve the membership from authoritative state in keyset pages (<see cref="IIndexTaskMembershipReader"/>):
/// import rows without a SnapshotId, explicit ids of relationship fix-ups, reindex key ranges.</item>
/// <item>Per page, read the current projection inputs and DocumentVersions in one snapshot and stream the projections
/// (<see cref="IProjectionService.BuildEachAsync"/>), one document's text at a time.</item>
/// <item>Write them through <see cref="IProjectionIndexWriter"/>: byte-bounded <c>_bulk</c> sub-requests,
/// <c>version_type=external</c>, so a stale write is a 409 no-op and never overwrites a newer interactive edit. Fence
/// F2 (lease token + workspace Active) is checked before every request and no write is sent from a read older than
/// <see cref="ChunkIndexWorkerOptions.MaxReadToWriteAge"/>.</item>
/// <item>Items that failed transiently are re-read and re-sent alone after a backoff (longer after a 429); the task is
/// Applied only when every document is applied or a version-conflict no-op. Otherwise the attempt is recorded in
/// PostgreSQL (RetryWait with backoff, or Failed) and the delivery is acked.</item>
/// </list>
/// Backpressure (ADR-010 §6): the dispatcher stops dispatching a job's chunks while more than 50 of its tasks (4 for
/// security-affecting jobs) are un-applied, so this worker's completion rate is what paces bulk commits; it slows down
/// under OpenSearch 429s instead of failing fast, and the small lane prefetch keeps at most a few tasks per consumer.
/// </summary>
public sealed partial class ChunkIndexTaskConsumer : IMessageHandler<IndexChunkTaskMessage>
{
    public const string RejectionReason = MessageRejectionReasons.EnvelopeMismatch;

    private readonly IIndexChunkTaskRepository _tasks;
    private readonly IIndexTaskMembershipReader _membership;
    private readonly IProjectionService _projections;
    private readonly IProjectionIndexWriter _writer;
    private readonly IAuditEventWriter _audit;
    private readonly ChunkIndexWorkerOptions _options;
    private readonly IMessageProcessingMeter _meter;
    private readonly TimeProvider _time;
    private readonly ILogger<ChunkIndexTaskConsumer> _logger;
    private readonly OpportunityMetrics? _metrics;
#if OPPORTUNITY_FAILPOINTS
    private readonly IFaultInjector? _faults;
#endif

    public ChunkIndexTaskConsumer(
        IIndexChunkTaskRepository tasks,
        IIndexTaskMembershipReader membership,
        IProjectionService projections,
        IProjectionIndexWriter writer,
        IAuditEventWriter audit,
        ChunkIndexWorkerOptions options,
        IMessageProcessingMeter meter,
        TimeProvider time,
        ILogger<ChunkIndexTaskConsumer> logger,
        OpportunityMetrics? metrics = null
#if OPPORTUNITY_FAILPOINTS
        , IFaultInjector? faults = null
#endif
        )
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _membership = membership ?? throw new ArgumentNullException(nameof(membership));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
#if OPPORTUNITY_FAILPOINTS
        _faults = faults;
#endif
        options.Validate();
    }

    public async Task HandleAsync(IndexChunkTaskMessage payload, ReceivedMessage message, CancellationToken cancellationToken)
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
    private async Task<string?> ProcessAsync(IndexChunkTaskMessage payload, ReceivedMessage message, CancellationToken cancellationToken)
    {
        var envelope = message.Envelope;
        if (envelope.WorkspaceId is not { } workspaceId || workspaceId == Guid.Empty)
        {
            await RejectAsync(payload, message, "workspaceId", "The envelope names no workspace.").ConfigureAwait(false);
            return RejectionReason;
        }

        // A failure to reach PostgreSQL escapes: the transport retries the delivery (ADR-010 §7.1).
        var leased = await _tasks.LeaseAsync(workspaceId, payload.TaskId, _options.WorkerId, _options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (leased.Outcome == IndexTaskLeaseOutcome.NotFound)
        {
            // Invisible under RLS with the hinted workspace: a foreign workspace, a forged or a stale message.
            await RejectAsync(payload, message, "workspaceId", "No such index task in the hinted workspace.").ConfigureAwait(false);
            return RejectionReason;
        }

        if (!leased.Leased)
        {
            LogNotLeased(_logger, payload.TaskId, leased.Outcome);
            var exhausted = leased.Outcome == IndexTaskLeaseOutcome.AttemptsExhausted;
            RecordAttempt(leased.Task?.Lane ?? message.Queue.Lane, exhausted ? Outcomes.Failed : Outcomes.Skipped,
                exhausted ? nameof(ChunkErrorClass.AttemptsExhausted) : null, started: null);
            return exhausted ? nameof(ChunkErrorClass.AttemptsExhausted) : null;
        }

        var task = leased.Task!;
        var lease = leased.Lease!;
        if (envelope.JobId is { } jobId && jobId != task.JobId)
        {
            await RejectAsync(payload, message, "jobId", "The envelope's job disagrees with the task row.", lease).ConfigureAwait(false);
            return RejectionReason;
        }

        if (!string.Equals(envelope.IdempotencyKey, task.IdempotencyKey, StringComparison.Ordinal))
        {
            await RejectAsync(payload, message, "idempotencyKey", "The envelope's idempotency key disagrees with the task row.", lease)
                .ConfigureAwait(false);
            return RejectionReason;
        }

#if OPPORTUNITY_FAILPOINTS
        await HitAsync(Failpoints.IndexTaskAfterLease, message, task, 1, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(task, lease, message, _time.GetTimestamp(), cancellationToken).ConfigureAwait(false);
#else
        return await ExecuteAsync(task, lease, message, _time.GetTimestamp(), cancellationToken).ConfigureAwait(false);
#endif
    }

    private async Task<string?> ExecuteAsync(
        IndexChunkTaskInfo task, IndexTaskLease lease, ReceivedMessage message, long started, CancellationToken cancellationToken)
    {
        var fence = new FenceState();
        IndexRun? run = null;
        Exception? failure = null;
        var timedOut = false;
        using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var stopHeartbeat = new CancellationTokenSource())
        {
            execution.CancelAfter(_options.MaxTaskRuntime);
            var heartbeat = HeartbeatAsync(lease, fence, execution, stopHeartbeat.Token);
            try
            {
                run = await RunAsync(task, lease, message, fence, execution.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Every failure is classified and recorded in PostgreSQL (the retry ledger).
#if OPPORTUNITY_FAILPOINTS
            catch (Exception ex) when (ex is not SimulatedCrashException)
#else
            catch (Exception ex)
#endif
#pragma warning restore CA1031
            {
                failure = ex;
                timedOut = execution.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    && fence.Value == IndexTaskRenewal.Renewed;
            }
            finally
            {
                await stopHeartbeat.CancelAsync().ConfigureAwait(false);
                await heartbeat.ConfigureAwait(false);
            }
        }

        if (failure is not null)
        {
            return await SettleFailureAsync(task, lease, failure, fence.Value, timedOut, started, cancellationToken).ConfigureAwait(false);
        }

        if (run!.Permanent.Count > 0)
        {
            var (id, reason) = run.Permanent.First();
            LogItemsRejected(_logger, task.TaskId, run.Permanent.Count, id, reason);
            return await RecordFailureAsync(task, lease, ChunkError.Permanent(
                "BulkItemsRejected", $"{run.Permanent.Count} document(s) were rejected by OpenSearch; first {id}: {reason}"), started).ConfigureAwait(false);
        }

        if (run.Retry.Count > 0)
        {
            return await RecordFailureAsync(task, lease, ChunkError.Transient(
                "BulkItemsFailed", $"{run.Retry.Count} document(s) still failed after {_options.MaxRetryRounds} retry rounds: {run.LastTransientError}"), started)
                .ConfigureAwait(false);
        }

        // Not the delivery's token: once every document is written, a shutdown must not abandon the completion.
        if (await _tasks.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false))
        {
            LogApplied(_logger, task.TaskId, task.Kind, run.Applied, run.Stale);
            RecordAttempt(task.Lane, Outcomes.Applied, null, started);
#if OPPORTUNITY_FAILPOINTS
            await HitAsync(Failpoints.IndexTaskAfterApplied, message, task, 1, cancellationToken).ConfigureAwait(false);
#endif
            return null;
        }

        LogLeaseLost(_logger, task.TaskId, lease.LeaseToken);
        RecordAttempt(task.Lane, Outcomes.Skipped, nameof(IndexTaskRenewal.LeaseLost), started);
        return null;
    }

    /// <summary>Resolves the membership page by page, writes it, then retries the transiently failed documents.</summary>
    private async Task<IndexRun> RunAsync(
        IndexChunkTaskInfo task, IndexTaskLease lease, ReceivedMessage message, FenceState fence, CancellationToken cancellationToken)
    {
        var run = new IndexRun(task, lease, message, fence);
        Guid? after = null;
        while (true)
        {
            var page = await _membership.ReadPageAsync(task.WorkspaceId, task.Membership, after, _options.ReadPageSize, cancellationToken)
                .ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            after = page[^1];
            await IndexAsync(run, page, cancellationToken).ConfigureAwait(false);
        }

        for (var round = 1; round <= _options.MaxRetryRounds && run.Retry.Count > 0; round++)
        {
            await Task.Delay(RetryDelay(round, run.Throttled), _time, cancellationToken).ConfigureAwait(false);
            run.Throttled = false;
            var ids = run.Retry.ToList();
            run.Retry.Clear();
            LogRetryRound(_logger, task.TaskId, round, ids.Count);
            foreach (var page in ids.Chunk(_options.ReadPageSize))
            {
                await IndexAsync(run, page, cancellationToken).ConfigureAwait(false);
            }
        }

        return run;
    }

    /// <summary>
    /// Reads <paramref name="ids"/> in one snapshot and writes them as their projections stream in. Documents the
    /// snapshot could not deliver within the read-to-write age are re-read in a fresh snapshot.
    /// </summary>
    private async Task IndexAsync(IndexRun run, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var remaining = ids;
        while (remaining.Count > 0)
        {
            var readAt = _time.GetTimestamp();
            var buffer = new List<ProjectionDocument>();
            long estimate = 0;
            var consumed = 0;
            var written = 0;
            var staleFrom = -1;
            await foreach (var document in _projections.BuildEachAsync(run.Task.WorkspaceId, remaining, cancellationToken).ConfigureAwait(false))
            {
                if (_time.GetElapsedTime(readAt) > _options.MaxReadToWriteAge)
                {
                    staleFrom = consumed - buffer.Count;
                    break;
                }

                buffer.Add(document);
                consumed++;
                estimate += EstimateBytes(document);
                if (estimate >= _writer.Options.MaxRequestBytes || buffer.Count >= _writer.Options.MaxRequestActions)
                {
                    if (!await FlushAsync(run, buffer, readAt, cancellationToken).ConfigureAwait(false))
                    {
                        staleFrom = consumed - buffer.Count;
                        break;
                    }

                    written += buffer.Count;
                    buffer.Clear();
                    estimate = 0;
                }
            }

            if (staleFrom < 0 && buffer.Count > 0)
            {
                if (await FlushAsync(run, buffer, readAt, cancellationToken).ConfigureAwait(false))
                {
                    written += buffer.Count;
                }
                else
                {
                    staleFrom = consumed - buffer.Count;
                }
            }

            if (staleFrom < 0)
            {
                return;
            }

            LogStaleRead(_logger, run.Task.TaskId, remaining.Count - staleFrom);
            var rest = remaining.Skip(staleFrom).ToList();
            if (written == 0)
            {
                // Not even one document fit in the age budget: hand it to the retry rounds rather than loop.
                run.Retry.Add(rest[0]);
                run.LastTransientError = "read_to_write_age_exceeded";
                rest.RemoveAt(0);
            }

            remaining = rest;
        }
    }

    /// <returns>False when the snapshot is too old to write from (nothing was sent).</returns>
    private async Task<bool> FlushAsync(IndexRun run, List<ProjectionDocument> buffer, long readAt, CancellationToken cancellationToken)
    {
        // Fence F2 before every OpenSearch request: lease still ours, workspace still Active (ADR-001 §4 R5).
        var renewal = await _tasks.RenewLeaseAsync(run.Lease, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        run.Fence.Observe(renewal);
        if (renewal != IndexTaskRenewal.Renewed)
        {
            throw new IndexTaskFencedException(renewal);
        }

        if (_time.GetElapsedTime(readAt) > _options.MaxReadToWriteAge)
        {
            return false;
        }

#if OPPORTUNITY_FAILPOINTS
        var request = ++run.Requests;
        await HitAsync(Failpoints.IndexTaskBeforeBulk, run.Message, run.Task, request, cancellationToken).ConfigureAwait(false);
#endif
        // A reindex backfill writes the rebuild target only (ADR-001 §7.5); every other task writes every target.
        var scope = run.Task.Kind == IndexTaskKind.Reindex ? ProjectionWriteScope.RebuildTarget : ProjectionWriteScope.AllTargets;
        var report = await _writer.WriteAsync(run.Task.WorkspaceId, buffer, scope, cancellationToken).ConfigureAwait(false);
#if OPPORTUNITY_FAILPOINTS
        await HitAsync(Failpoints.IndexTaskAfterBulk, run.Message, run.Task, request, cancellationToken).ConfigureAwait(false);
#endif
        run.Throttled |= report.Throttled;
        int applied = 0, stale = 0, transient = 0, permanent = 0;
        foreach (var result in report.Documents)
        {
            switch (result.Status)
            {
                case ProjectionWriteStatus.Applied:
                    applied++;
                    break;
                case ProjectionWriteStatus.StaleNoOp:
                    stale++;
                    break;
                case ProjectionWriteStatus.Transient:
                    transient++;
                    run.Retry.Add(result.DocumentId);
                    run.LastTransientError = result.Error;
                    break;
                default:
                    permanent++;
                    run.Permanent[result.DocumentId] = result.Error ?? "rejected";
                    break;
            }
        }

        run.Applied += applied;
        run.Stale += stale;
        RecordItems(run.Task.Lane, applied, stale, transient, permanent);
        return true;
    }

    private async Task<string?> SettleFailureAsync(
        IndexChunkTaskInfo task, IndexTaskLease lease, Exception failure, IndexTaskRenewal observed, bool timedOut, long started,
        CancellationToken cancellationToken)
    {
        var fence = failure is IndexTaskFencedException fenced ? fenced.Renewal : observed;
        if (fence != IndexTaskRenewal.Renewed && failure is IndexTaskFencedException or OperationCanceledException)
        {
            // Lease lost: another worker owns the task. Workspace not Active: the work is dropped (ADR-001 §4 R5) and the
            // lease left to expire; a later lease attempt sees the workspace and acks.
            LogFenced(_logger, task.TaskId, fence);
            RecordAttempt(task.Lane, Outcomes.Skipped, fence.ToString(), started);
            return null;
        }

        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // Shutdown: give the task back without charging the attempt, then let the transport requeue the delivery.
            try
            {
                await _tasks.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best effort: the lease expires and recovery returns the task anyway.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReleaseFailed(_logger, task.TaskId, ex);
            }

            RecordAttempt(task.Lane, Outcomes.Released, null, started);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        var error = timedOut && failure is OperationCanceledException
            ? ChunkError.Transient("TaskRuntimeExceeded", $"The index task ran longer than {_options.MaxTaskRuntime}.")
            : Classify(failure);
        LogTaskFailed(_logger, task.TaskId, error.Class, error.Code, failure);
        return await RecordFailureAsync(task, lease, error, started).ConfigureAwait(false);
    }

    /// <summary>Records the failed attempt in PostgreSQL; if that fails the exception escapes to the transport retry.</summary>
    private async Task<string> RecordFailureAsync(IndexChunkTaskInfo task, IndexTaskLease lease, ChunkError error, long started)
    {
        var result = await _tasks.FailAsync(lease, error, CancellationToken.None).ConfigureAwait(false);
        var outcome = result.Outcome switch
        {
            IndexTaskFailureOutcome.RetryScheduled => Outcomes.Retry,
            IndexTaskFailureOutcome.Failed => Outcomes.Failed,
            _ => Outcomes.Skipped,
        };
        RecordAttempt(task.Lane, outcome, error.Class.ToString(), started);
        return error.Class.ToString();
    }

    /// <summary>ADR-010 §7 error classes for exceptions escaping the indexing of a task.</summary>
    internal static ChunkError Classify(Exception exception) => exception switch
    {
        OpenSearchRequestException os when os.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)os.Status >= 500
            => ChunkError.Transient("OpenSearchUnavailable", Describe(exception)),
        OpenSearchRequestException => ChunkError.Permanent("OpenSearchRejected", Describe(exception)),
        NotSupportedException => ChunkError.Permanent("MembershipNotResolvable", Describe(exception)),
        DbException db => db.IsTransient ? ChunkError.Transient(exception.GetType().Name, Describe(exception)) : ChunkError.Permanent(exception.GetType().Name, Describe(exception)),
        TimeoutException or IOException or HttpRequestException or OperationCanceledException => ChunkError.Transient(exception.GetType().Name, Describe(exception)),
        ArgumentException or FormatException or InvalidDataException or JsonException or InvalidCastException or KeyNotFoundException
            => ChunkError.Permanent(exception.GetType().Name, Describe(exception)),
        _ => ChunkError.Transient(exception.GetType().Name, Describe(exception)),
    };

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>Bytes a projection will roughly take in a bulk body: its text plus a fixed allowance for the rest.</summary>
    private static long EstimateBytes(ProjectionDocument document)
    {
        long bytes = 0;
        foreach (var write in document.Writes)
        {
            bytes += 4_096;
            if (write.Body?["text"] is JsonValue text && text.TryGetValue<string>(out var value))
            {
                bytes += value.Length;
            }
        }

        return bytes;
    }

    private TimeSpan RetryDelay(int round, bool throttled)
    {
        var exponent = Math.Min(round - 1, 16);
        var seconds = Math.Min(_options.RetryBaseDelay.TotalSeconds * Math.Pow(2, exponent), _options.RetryMaxDelay.TotalSeconds);
        var delay = TimeSpan.FromSeconds(seconds * (0.8 + (0.4 * Random.Shared.NextDouble())));
        return throttled && delay < _options.ThrottleDelay ? _options.ThrottleDelay : delay;
    }

    /// <summary>Extends the lease every heartbeat interval; a lost lease or inactive workspace cancels the work.</summary>
    private async Task HeartbeatAsync(IndexTaskLease lease, FenceState fence, CancellationTokenSource execution, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                IndexTaskRenewal renewal;
                try
                {
                    renewal = await _tasks.RenewLeaseAsync(lease, _options.LeaseDuration, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep working: the fence before each write is authoritative, and the lease covers a short outage.
                    LogHeartbeatFailed(_logger, lease.TaskId, ex);
                    continue;
                }

                fence.Observe(renewal);
                if (renewal != IndexTaskRenewal.Renewed)
                {
                    await execution.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The task finished.
        }
    }

    /// <summary>ADR-015 D9.3: installation-level audit event, then dead-letter; a lease the check needed is given back.</summary>
    private async Task RejectAsync(IndexChunkTaskMessage payload, ReceivedMessage message, string field, string detail, IndexTaskLease? release = null)
    {
        var envelope = message.Envelope;
        LogRejected(_logger, payload.TaskId, envelope.WorkspaceId, field);
        await _audit.WriteAsync(
            MessageRejection.AuditEvent(
                envelope,
                message.Queue,
                MessageRejectionReasons.EnvelopeMismatch,
                _options.WorkerId,
                "IndexChunkTask",
                payload.TaskId.ToString(),
                _time.GetUtcNow(),
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["mismatch"] = field }),
            CancellationToken.None).ConfigureAwait(false);

        if (release is not null)
        {
            await _tasks.ReleaseAsync(release, CancellationToken.None).ConfigureAwait(false);
        }

        RecordAttempt(message.Queue.Lane, Outcomes.Rejected, RejectionReason, started: null);
        throw new PermanentMessageException($"{RejectionReason} ({field}): {detail}");
    }

    private void RecordAttempt(MessageLane lane, string outcome, string? errorType, long? started)
    {
        if (_metrics is null)
        {
            return;
        }

        var tags = new TagList { { TelemetryAttributes.Lane, DispatchMetrics.LaneName(lane) }, { TelemetryAttributes.Outcome, outcome } };
        if (errorType is not null)
        {
            tags.Add(TelemetryAttributes.ErrorType, errorType);
        }

        _metrics.Counter(OpportunityMetricCatalog.IndexChunkTaskAttempts).Add(1, tags);
        if (started is { } at)
        {
            _metrics.Histogram(OpportunityMetricCatalog.IndexChunkTaskDuration).Record(
                _time.GetElapsedTime(at).TotalSeconds,
                new TagList { { TelemetryAttributes.Lane, DispatchMetrics.LaneName(lane) }, { TelemetryAttributes.Outcome, outcome } });
        }
    }

    private void RecordItems(MessageLane lane, int applied, int stale, int transient, int permanent)
    {
        if (_metrics is null)
        {
            return;
        }

        var laneName = DispatchMetrics.LaneName(lane);
        var items = _metrics.Counter(OpportunityMetricCatalog.IndexBulkItems);
        foreach (var (outcome, count) in new[] { ("applied", applied), ("stale", stale), ("transient", transient), ("permanent", permanent) })
        {
            if (count > 0)
            {
                items.Add(count, new TagList { { TelemetryAttributes.Lane, laneName }, { TelemetryAttributes.Outcome, outcome } });
            }
        }

        if (stale > 0)
        {
            _metrics.Counter(OpportunityMetricCatalog.SearchStaleVersionRejections).Add(stale, new TagList { { TelemetryAttributes.Lane, laneName } });
        }
    }

    /// <summary>Values of the <c>opportunity.outcome</c> attribute on <c>opportunity.index.chunk_task.attempts</c>.</summary>
    public static class Outcomes
    {
        public const string Applied = "applied";
        public const string Retry = "retry";
        public const string Failed = "failed";

        /// <summary>Not leased or no longer owned: a duplicate, an early redelivery, a lost lease, an inactive workspace.</summary>
        public const string Skipped = "skipped";

        /// <summary>Stopped by shutdown: back to Pending without charging the attempt.</summary>
        public const string Released = "released";

        /// <summary>The envelope disagreed with PostgreSQL: dead-lettered and audited.</summary>
        public const string Rejected = "rejected";
    }

    /// <summary>Progress of one attempt.</summary>
    private sealed class IndexRun(IndexChunkTaskInfo task, IndexTaskLease lease, ReceivedMessage message, FenceState fence)
    {
        public IndexChunkTaskInfo Task => task;

        public IndexTaskLease Lease => lease;

        public ReceivedMessage Message => message;

        /// <summary><c>_bulk</c> requests sent so far in this attempt.</summary>
        public int Requests { get; set; }

        public FenceState Fence => fence;

        public long Applied { get; set; }

        public long Stale { get; set; }

        /// <summary>Documents to re-read and re-send in the next retry round.</summary>
        public HashSet<Guid> Retry { get; } = [];

        public Dictionary<Guid, string> Permanent { get; } = [];

        public string? LastTransientError { get; set; }

        public bool Throttled { get; set; }
    }

#if OPPORTUNITY_FAILPOINTS
    private ValueTask HitAsync(string failpoint, ReceivedMessage message, IndexChunkTaskInfo task, int sequence, CancellationToken cancellationToken) =>
        _faults?.HitAsync(failpoint, new FailpointContext(message, null)
        {
            WorkspaceId = task.WorkspaceId,
            Subject = task.TaskId.ToString("D"),
            Sequence = sequence,
        }, cancellationToken) ?? ValueTask.CompletedTask;
#endif

    /// <summary>The latest non-renewal seen by the heartbeat or a fence check.</summary>
    private sealed class FenceState
    {
        private int _value = (int)IndexTaskRenewal.Renewed;

        public IndexTaskRenewal Value => (IndexTaskRenewal)Volatile.Read(ref _value);

        public void Observe(IndexTaskRenewal renewal)
        {
            if (renewal != IndexTaskRenewal.Renewed)
            {
                Interlocked.CompareExchange(ref _value, (int)renewal, (int)IndexTaskRenewal.Renewed);
            }
        }
    }

    private sealed class IndexTaskFencedException(IndexTaskRenewal renewal) : Exception($"Index task fenced: {renewal}.")
    {
        public IndexTaskRenewal Renewal => renewal;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Index task {TaskId} not leased ({Outcome}); acking the delivery")]
    private static partial void LogNotLeased(ILogger logger, Guid taskId, IndexTaskLeaseOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index task {TaskId} ({Kind}) applied: {Applied} written, {Stale} already newer")]
    private static partial void LogApplied(ILogger logger, Guid taskId, Core.SearchWork.IndexTaskKind kind, long applied, long stale);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index task {TaskId}: retry round {Round} for {Count} document(s)")]
    private static partial void LogRetryRound(ILogger logger, Guid taskId, int round, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index task {TaskId}: {Count} document(s) re-read, their snapshot exceeded the read-to-write age")]
    private static partial void LogStaleRead(ILogger logger, Guid taskId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Index task {TaskId}: {Count} document(s) rejected permanently; first {DocumentId}: {Reason}")]
    private static partial void LogItemsRejected(ILogger logger, Guid taskId, int count, Guid documentId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejecting a message for index task {TaskId}: envelope workspace {WorkspaceId} disagrees with PostgreSQL ({Field})")]
    private static partial void LogRejected(ILogger logger, Guid taskId, Guid? workspaceId, string field);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Index task {TaskId} lost its lease (token {LeaseToken}); nothing recorded")]
    private static partial void LogLeaseLost(ILogger logger, Guid taskId, long leaseToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index task {TaskId} stopped at a fence: {Fence}")]
    private static partial void LogFenced(ILogger logger, Guid taskId, IndexTaskRenewal fence);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Index task {TaskId} failed ({ErrorClass} {ErrorCode})")]
    private static partial void LogTaskFailed(ILogger logger, Guid taskId, ChunkErrorClass errorClass, string errorCode, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release index task {TaskId} on shutdown; lease expiry will recover it")]
    private static partial void LogReleaseFailed(ILogger logger, Guid taskId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat for index task {TaskId} failed; continuing until the next one")]
    private static partial void LogHeartbeatFailed(ILogger logger, Guid taskId, Exception exception);
}
