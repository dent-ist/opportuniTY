using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// Per-worker-type liveness signals: started, last heartbeat and last consumed message. Exposed as metrics
/// (<c>opportunity.worker.heartbeat.age</c>, <c>opportunity.worker.last_consumed.age</c>) for alerting; consumers call
/// <see cref="RecordConsumed"/> after each handled message.
/// </summary>
public sealed class WorkerStatus
{
    public const string MeterName = "Opportunity.Workers";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _heartbeats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public WorkerStatus(IReadOnlyList<string> enabledTypes, TimeProvider time, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        EnabledTypes = enabledTypes;
        _time = time;

        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(
            "opportunity.worker.heartbeat.age",
            () => Ages(_heartbeats),
            unit: "s",
            description: "Seconds since the worker type last reported a heartbeat.");
        meter.CreateObservableGauge(
            "opportunity.worker.last_consumed.age",
            () => Ages(_consumed),
            unit: "s",
            description: "Seconds since the worker type last consumed a message.");
    }

    public IReadOnlyList<string> EnabledTypes { get; }

    public void RecordHeartbeat(string workerType) => _heartbeats[workerType] = _time.GetUtcNow();

    public void RecordConsumed(string workerType) => _consumed[workerType] = _time.GetUtcNow();

    public DateTimeOffset? LastHeartbeat(string workerType) =>
        _heartbeats.TryGetValue(workerType, out var at) ? at : null;

    private IEnumerable<Measurement<double>> Ages(ConcurrentDictionary<string, DateTimeOffset> source)
    {
        var now = _time.GetUtcNow();
        return source.Select(entry => new Measurement<double>(
            (now - entry.Value).TotalSeconds,
            new KeyValuePair<string, object?>("worker.type", entry.Key)));
    }
}

/// <summary>Placeholder module service: reports heartbeats until the module's real consumers land.</summary>
internal sealed class WorkerHeartbeatService(
    string workerType,
    WorkerStatus status,
    IOptions<WorkerHostOptions> options,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.RecordHeartbeat(workerType);
        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                status.RecordHeartbeat(workerType);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}

/// <summary>Ready once every enabled worker type has reported at least one heartbeat.</summary>
internal sealed class WorkerModulesReadyCheck(WorkerStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var waiting = status.EnabledTypes.Where(t => status.LastHeartbeat(t) is null).ToList();
        return Task.FromResult(waiting.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Waiting for worker types to start: {string.Join(", ", waiting)}"));
    }
}
