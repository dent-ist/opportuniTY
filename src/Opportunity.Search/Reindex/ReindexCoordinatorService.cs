using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Jobs;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Reindex;
using Opportunity.Application.Workspaces;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;

namespace Opportunity.Search.Reindex;

/// <summary>
/// Runs the reindex coordinator (<c>ReindexCoordinator</c>) every <see cref="ReindexOptions.PollInterval"/> in the indexing
/// worker host. Several replicas are safe: each run is driven by the holder of its lease. Dependencies are resolved when
/// the host starts, so a host without PostgreSQL or OpenSearch logs that the coordinator is disabled.
/// </summary>
public sealed partial class ReindexCoordinatorService(
    IServiceProvider services, ReindexOptions options, TimeProvider time, ILogger<ReindexCoordinatorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReindexCoordinator coordinator;
        try
        {
            coordinator = services.GetRequiredService<ReindexCoordinator>();
        }
        catch (InvalidOperationException ex)
        {
            LogDisabled(logger, ex.Message);
            return;
        }

        using var timer = new PeriodicTimer(options.PollInterval, time);
        do
        {
            try
            {
                await coordinator.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A database outage fails every pass; the runs simply wait.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPassFailed(logger, ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reindex coordinator disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reindex coordinator pass failed; retried at the next pass")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}

public static class ReindexCoordinatorRegistration
{
    /// <summary>
    /// Registers the reindex coordinator (E07-T11) and its hosted loop. The host also registers <see cref="IReindexStore"/>,
    /// <see cref="IJobRepository"/>, <see cref="IJobChunkRepository"/>, <see cref="ISearchFreshnessReader"/>,
    /// <see cref="IPreservationLockGuard"/>, index
    /// management and the projection pipeline (the chunk index worker registration brings the last two).
    /// </summary>
    public static IServiceCollection AddReindexCoordinator(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ReindexOptions();
        configuration?.GetSection(ReindexOptions.SectionName).Bind(options);
        return services.AddReindexCoordinator(options);
    }

    /// <summary>The coordinator without its hosted loop (tests drive its steps).</summary>
    public static IServiceCollection AddReindexCoordinatorCore(this IServiceCollection services, ReindexOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ReindexValidator(
            sp.GetRequiredService<IReindexStore>(), sp.GetRequiredService<OpenSearchConnection>(), sp.GetRequiredService<IProjectionService>(),
            sp.GetRequiredService<ISearchFreshnessReader>(), sp.GetRequiredService<ReindexOptions>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ReindexValidator>>()));
        services.TryAddSingleton(sp => new ReindexCoordinator(
            sp.GetRequiredService<IReindexStore>(), sp.GetRequiredService<IJobRepository>(), sp.GetRequiredService<IJobChunkRepository>(),
            sp.GetRequiredService<IIndexManager>(), sp.GetRequiredService<IProjectionService>(), sp.GetRequiredService<ReindexValidator>(),
            sp.GetRequiredService<IPreservationLockGuard>(), sp.GetRequiredService<ReindexOptions>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ReindexCoordinator>>()));
        return services;
    }

    public static IServiceCollection AddReindexCoordinator(this IServiceCollection services, ReindexOptions options)
    {
        services.AddReindexCoordinatorCore(options);
        services.AddHostedService<ReindexCoordinatorService>();
        return services;
    }
}
