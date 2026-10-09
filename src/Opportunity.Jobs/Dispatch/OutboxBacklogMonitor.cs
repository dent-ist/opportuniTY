using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;

namespace Opportunity.Jobs.Dispatch;

/// <summary>SearchOutbox backlog of one lane over all workspaces.</summary>
/// <param name="Unapplied">Rows not yet Applied (<c>opportunity.outbox.pending</c>).</param>
/// <param name="OldestAge">Age of the oldest row not yet dispatched (<c>opportunity.outbox.oldest_age</c>); zero when none.</param>
public sealed record OutboxLaneSample(MessageLane Lane, long Unapplied, TimeSpan OldestAge);

/// <summary>Pipeline backlog over all workspaces, summed per attribute set; ages are of the oldest row (zero when none).</summary>
public sealed record PipelineBacklogSample(
    IReadOnlyList<(IndexChunkTaskStatus Status, MessageLane Lane, long Count, TimeSpan OldestAge)> IndexTasks,
    IReadOnlyList<(JobType JobType, JobChunkStatus Status, long Count, TimeSpan OldestAge)> JobChunks,
    IReadOnlyList<(JobType JobType, long Count)> ActiveJobs)
{
    /// <summary>Statuses an IndexChunkTask reports in (every status but Applied).</summary>
    public static IReadOnlyList<IndexChunkTaskStatus> TaskStatuses { get; } =
        [IndexChunkTaskStatus.Pending, IndexChunkTaskStatus.Dispatched, IndexChunkTaskStatus.Running, IndexChunkTaskStatus.RetryWait, IndexChunkTaskStatus.Failed];

    /// <summary>The lanes of IndexChunkTasks.</summary>
    public static IReadOnlyList<MessageLane> TaskLanes { get; } = [MessageLane.SecurityBulk, MessageLane.Bulk];

    /// <summary>Open job chunk statuses.</summary>
    public static IReadOnlyList<JobChunkStatus> ChunkStatuses { get; } =
        [JobChunkStatus.Pending, JobChunkStatus.Dispatched, JobChunkStatus.Running, JobChunkStatus.RetryWait];

    /// <summary>
    /// Sums per-workspace backlogs into a full grid (every status × lane and job type × status, zeros included), so a
    /// drained backlog reports 0 instead of a series that goes stale.
    /// </summary>
    public static PipelineBacklogSample From(IEnumerable<PipelineBacklog> backlogs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(backlogs);
        var tasks = TaskStatuses.SelectMany(s => TaskLanes.Select(l => (s, l))).ToDictionary(k => k, _ => (Count: 0L, Oldest: (DateTimeOffset?)null));
        var types = Enum.GetValues<JobType>();
        var chunks = types.SelectMany(t => ChunkStatuses.Select(s => (t, s))).ToDictionary(k => k, _ => (Count: 0L, Oldest: (DateTimeOffset?)null));
        var jobs = types.ToDictionary(t => t, _ => 0L);
        foreach (var backlog in backlogs)
        {
            foreach (var task in backlog.IndexTasks)
            {
                if (tasks.TryGetValue((task.Status, task.Lane), out var current))
                {
                    tasks[(task.Status, task.Lane)] = (current.Count + task.Count, Earliest(current.Oldest, task.OldestSince));
                }
            }

            foreach (var chunk in backlog.JobChunks)
            {
                if (chunks.TryGetValue((chunk.JobType, chunk.Status), out var current))
                {
                    chunks[(chunk.JobType, chunk.Status)] = (current.Count + chunk.Count, Earliest(current.Oldest, chunk.OldestSince));
                }
            }

            foreach (var job in backlog.ActiveJobs)
            {
                jobs[job.JobType] = jobs.GetValueOrDefault(job.JobType) + job.Count;
            }
        }

        return new PipelineBacklogSample(
            [.. tasks.Select(t => (t.Key.s, t.Key.l, t.Value.Count, Age(t.Value.Oldest, now)))],
            [.. chunks.Select(c => (c.Key.t, c.Key.s, c.Value.Count, Age(c.Value.Oldest, now)))],
            [.. jobs.Select(j => (j.Key, j.Value))]);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private static TimeSpan Age(DateTimeOffset? since, DateTimeOffset now) => since is { } at && at < now ? now - at : TimeSpan.Zero;
}

/// <summary>
/// Samples the SearchOutbox per lane over every workspace (ADR-005 P10: one workspace transaction each) and serves the
/// <c>opportunity.outbox.pending</c> and <c>opportunity.outbox.oldest_age</c> gauges (ADR-017 §5) from the last sample.
/// The same pass samples the IndexChunkTask, job chunk and job backlog (<see cref="PipelineBacklogSample"/>) for the
/// <c>opportunity.index.chunk_tasks</c>, <c>opportunity.index.chunk_task.oldest_age</c>, <c>opportunity.job.chunk.*</c>
/// and <c>opportunity.jobs.active</c> gauges (E19-T05).
/// When the oldest undispatched row passes <see cref="OutboxDispatcherOptions.OutboxAgeAlertThreshold"/> it logs an
/// error (the in-process twin of the ADR-017 §6 "outbox age" alert rule), again every minute while it lasts, and once
/// more when the lane recovers. Every dispatcher instance samples; dashboards take the maximum per lane.
/// </summary>
public sealed partial class OutboxBacklogMonitor
{
    private static readonly MessageLane[] Lanes = [MessageLane.Security, MessageLane.Interactive];
    private static readonly TimeSpan RepeatAlertAfter = TimeSpan.FromMinutes(1);

    private readonly ISearchWorkMaintenance _maintenance;
    private readonly OutboxDispatcherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxBacklogMonitor> _logger;
    private readonly Dictionary<MessageLane, DateTimeOffset> _alertLoggedAt = [];
    private volatile IReadOnlyList<OutboxLaneSample> _current = [.. Lanes.Select(l => new OutboxLaneSample(l, 0, TimeSpan.Zero))];
    private volatile PipelineBacklogSample _pipeline = PipelineBacklogSample.From([], DateTimeOffset.UnixEpoch);

    public OutboxBacklogMonitor(
        ISearchWorkMaintenance maintenance, OutboxDispatcherOptions options, TimeProvider time, ILogger<OutboxBacklogMonitor> logger,
        OpportunityMetrics? metrics = null)
    {
        _maintenance = maintenance;
        _options = options;
        _time = time;
        _logger = logger;
        metrics?.Gauge(OpportunityMetricCatalog.OutboxPending, () => Current.Select(s => new Measurement<long>(s.Unapplied, Lane(s.Lane))));
        metrics?.Gauge(OpportunityMetricCatalog.OutboxOldestAge, () => Current.Select(s => new Measurement<double>(s.OldestAge.TotalSeconds, Lane(s.Lane))));
        metrics?.Gauge(OpportunityMetricCatalog.IndexChunkTasks, () => Pipeline.IndexTasks.Select(t => new Measurement<long>(t.Count, Status(t.Status), Lane(t.Lane))));
        metrics?.Gauge(OpportunityMetricCatalog.IndexChunkTaskOldestAge, () => Pipeline.IndexTasks.Select(t => new Measurement<double>(t.OldestAge.TotalSeconds, Status(t.Status), Lane(t.Lane))));
        metrics?.Gauge(OpportunityMetricCatalog.JobChunkBacklog, () => Pipeline.JobChunks.Select(c => new Measurement<long>(c.Count, JobTypeTag(c.JobType), Status(c.Status))));
        metrics?.Gauge(OpportunityMetricCatalog.JobChunkOldestAge, () => Pipeline.JobChunks.Select(c => new Measurement<double>(c.OldestAge.TotalSeconds, JobTypeTag(c.JobType), Status(c.Status))));
        metrics?.Gauge(OpportunityMetricCatalog.JobsActive, () => Pipeline.ActiveJobs.Select(j => new Measurement<long>(j.Count, JobTypeTag(j.JobType))));
    }

    /// <summary>The last sample, one entry per outbox lane.</summary>
    public IReadOnlyList<OutboxLaneSample> Current => _current;

    /// <summary>The last pipeline backlog sample (all zeros before the first).</summary>
    public PipelineBacklogSample Pipeline => _pipeline;

    /// <summary>Lanes whose oldest undispatched row is past the alert threshold in the last sample.</summary>
    public IReadOnlyList<MessageLane> Alerting => [.. Current.Where(s => s.OldestAge > _options.OutboxAgeAlertThreshold).Select(s => s.Lane)];

    public async Task<IReadOnlyList<OutboxLaneSample>> SampleAsync(IReadOnlyList<Guid> workspaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var unapplied = Lanes.ToDictionary(l => l, _ => 0L);
        var oldest = new Dictionary<MessageLane, DateTimeOffset>();
        var pipeline = new List<PipelineBacklog>(workspaces.Count);
        foreach (var workspaceId in workspaces)
        {
            pipeline.Add(await _maintenance.GetPipelineBacklogAsync(workspaceId, cancellationToken).ConfigureAwait(false));
            foreach (var lane in await _maintenance.GetOutboxLaneBacklogAsync(workspaceId, cancellationToken).ConfigureAwait(false))
            {
                unapplied[lane.Lane] = unapplied.GetValueOrDefault(lane.Lane) + lane.Unapplied;
                if (lane.OldestUndispatchedCommittedAt is { } committed
                    && (!oldest.TryGetValue(lane.Lane, out var previous) || committed < previous))
                {
                    oldest[lane.Lane] = committed;
                }
            }
        }

        var now = _time.GetUtcNow();
        var sample = unapplied.Keys.Order()
            .Select(lane => new OutboxLaneSample(
                lane, unapplied[lane], oldest.TryGetValue(lane, out var at) && at < now ? now - at : TimeSpan.Zero))
            .ToList();
        _current = sample;
        _pipeline = PipelineBacklogSample.From(pipeline, now);
        Alert(sample, now);
        return sample;
    }

    private void Alert(IReadOnlyList<OutboxLaneSample> sample, DateTimeOffset now)
    {
        foreach (var lane in sample)
        {
            var late = lane.OldestAge > _options.OutboxAgeAlertThreshold;
            var logged = _alertLoggedAt.TryGetValue(lane.Lane, out var at);
            var name = DispatchMetrics.LaneName(lane.Lane);
            if (late && (!logged || now - at >= RepeatAlertAfter))
            {
                var age = lane.OldestAge.TotalSeconds;
                var threshold = _options.OutboxAgeAlertThreshold.TotalSeconds;
                LogOutboxLate(_logger, name, age, threshold, lane.Unapplied);
                _alertLoggedAt[lane.Lane] = now;
            }
            else if (!late && logged)
            {
                LogOutboxRecovered(_logger, name);
                _alertLoggedAt.Remove(lane.Lane);
            }
        }
    }

    private static KeyValuePair<string, object?> Lane(MessageLane lane) => new(TelemetryAttributes.Lane, DispatchMetrics.LaneName(lane));

    private static KeyValuePair<string, object?> Status<TStatus>(TStatus status)
        where TStatus : struct, Enum => new(TelemetryAttributes.Status, status.ToString());

    private static KeyValuePair<string, object?> JobTypeTag(JobType type) => new(TelemetryAttributes.JobType, type.ToString());

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox lane {Lane}: the oldest undispatched row is {AgeSeconds:F0} s old (alert above {ThresholdSeconds:F0} s); {Unapplied} rows are not applied")]
    private static partial void LogOutboxLate(ILogger logger, string lane, double ageSeconds, double thresholdSeconds, long unapplied);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox lane {Lane} is back under the age alert threshold")]
    private static partial void LogOutboxRecovered(ILogger logger, string lane);
}
