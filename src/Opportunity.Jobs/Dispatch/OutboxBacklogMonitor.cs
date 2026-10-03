using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;

namespace Opportunity.Jobs.Dispatch;

/// <summary>SearchOutbox backlog of one lane over all workspaces.</summary>
/// <param name="Unapplied">Rows not yet Applied (<c>opportunity.outbox.pending</c>).</param>
/// <param name="OldestAge">Age of the oldest row not yet dispatched (<c>opportunity.outbox.oldest_age</c>); zero when none.</param>
public sealed record OutboxLaneSample(MessageLane Lane, long Unapplied, TimeSpan OldestAge);

/// <summary>
/// Samples the SearchOutbox per lane over every workspace (ADR-005 P10: one workspace transaction each) and serves the
/// <c>opportunity.outbox.pending</c> and <c>opportunity.outbox.oldest_age</c> gauges (ADR-017 §5) from the last sample.
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
    }

    /// <summary>The last sample, one entry per outbox lane.</summary>
    public IReadOnlyList<OutboxLaneSample> Current => _current;

    /// <summary>Lanes whose oldest undispatched row is past the alert threshold in the last sample.</summary>
    public IReadOnlyList<MessageLane> Alerting => [.. Current.Where(s => s.OldestAge > _options.OutboxAgeAlertThreshold).Select(s => s.Lane)];

    public async Task<IReadOnlyList<OutboxLaneSample>> SampleAsync(IReadOnlyList<Guid> workspaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var unapplied = Lanes.ToDictionary(l => l, _ => 0L);
        var oldest = new Dictionary<MessageLane, DateTimeOffset>();
        foreach (var workspaceId in workspaces)
        {
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

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox lane {Lane}: the oldest undispatched row is {AgeSeconds:F0} s old (alert above {ThresholdSeconds:F0} s); {Unapplied} rows are not applied")]
    private static partial void LogOutboxLate(ILogger logger, string lane, double ageSeconds, double thresholdSeconds, long unapplied);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox lane {Lane} is back under the age alert threshold")]
    private static partial void LogOutboxRecovered(ILogger logger, string lane);
}
