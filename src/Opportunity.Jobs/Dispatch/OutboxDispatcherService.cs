using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;

namespace Opportunity.Jobs.Dispatch;

/// <summary>
/// Hosts <see cref="OutboxDispatcher"/>. Its dependencies are resolved when the host starts, so a dispatcher host without
/// PostgreSQL or RabbitMQ configured logs that dispatching is disabled instead of failing to start (like the other
/// dispatcher-host loops).
/// </summary>
public sealed partial class OutboxDispatcherService(IServiceProvider services, ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OutboxDispatcher dispatcher;
        try
        {
            dispatcher = services.GetRequiredService<OutboxDispatcher>();
        }
        catch (InvalidOperationException ex)
        {
            LogDisabled(logger, ex.Message);
            return;
        }

        await dispatcher.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatcher disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);
}

public static class OutboxDispatcherRegistration
{
    /// <summary>
    /// Registers the outbox dispatcher and its hosted loop. The host must also register the search work store (with its
    /// wake-up listener), <see cref="IJobChunkDispatchRepository"/> and an <see cref="IMessagePublisher"/>.
    /// </summary>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services, OutboxDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new OutboxDispatcherOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetService<OpportunityMetrics>() is { } metrics
            ? new DispatchMetricsHolder(new DispatchMetrics(metrics, sp.GetRequiredService<TimeProvider>()))
            : new DispatchMetricsHolder(null));
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<OutboxDispatcherOptions>();
            return new SearchWorkRelay(
                sp.GetRequiredService<ISearchOutboxRepository>(),
                sp.GetRequiredService<IIndexChunkTaskRepository>(),
                sp.GetRequiredService<IMessagePublisher>(),
                new SearchWorkRelayOptions { Owner = o.Owner, BatchSize = o.BatchSize, ClaimDuration = o.ClaimDuration, TaskRetryDelay = o.RetryDelay },
                sp.GetRequiredService<DispatchMetricsHolder>().Metrics);
        });
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<OutboxDispatcherOptions>();
            return new JobChunkRelay(
                sp.GetRequiredService<IJobChunkDispatchRepository>(),
                sp.GetRequiredService<IMessagePublisher>(),
                new JobChunkRelayOptions { Owner = o.Owner, ClaimDuration = o.ClaimDuration, RetryDelay = o.RetryDelay, Limits = o.JobChunkLimits },
                sp.GetRequiredService<DispatchMetricsHolder>().Metrics);
        });
        // OpportunityMetrics is optional: without telemetry registration the gauges are simply not created.
        services.TryAddSingleton<OutboxBacklogMonitor>();
        services.TryAddSingleton<OutboxDispatcher>();
        services.AddHostedService<OutboxDispatcherService>();
        return services;
    }

    private sealed record DispatchMetricsHolder(DispatchMetrics? Metrics);
}
