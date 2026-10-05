using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Search;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Search.Indexing;

namespace Opportunity.Search.Freshness;

/// <summary>Settings of the visible-watermark ticker (section <c>Search:Watermark</c>).</summary>
public sealed class SearchWatermarkOptions
{
    public const string SectionName = "Search:Watermark";

    /// <summary>Tick period: refresh observation and the per-workspace lag sample (ADR-001 §7.3/§7.4: 1 s).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the list of workspaces is re-read.</summary>
    public TimeSpan WorkspaceRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (Interval <= TimeSpan.Zero || WorkspaceRefreshInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}: Interval and WorkspaceRefreshInterval must be positive.");
        }
    }
}

/// <summary>
/// Runs <see cref="SearchWatermarkAdvancer"/> every <see cref="SearchWatermarkOptions.Interval"/> over every workspace
/// (in the dispatcher host). Its dependencies are resolved when the host starts, so a host without PostgreSQL or
/// OpenSearch logs that the ticker is disabled instead of failing to start.
/// </summary>
public sealed partial class SearchWatermarkService(
    IServiceProvider services, SearchWatermarkOptions options, TimeProvider time, ILogger<SearchWatermarkService> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SearchWatermarkAdvancer advancer;
        ISearchWorkMaintenance maintenance;
        try
        {
            advancer = services.GetRequiredService<SearchWatermarkAdvancer>();
            maintenance = services.GetRequiredService<ISearchWorkMaintenance>();
        }
        catch (InvalidOperationException ex)
        {
            LogDisabled(logger, ex.Message);
            return;
        }

        IReadOnlyList<Guid> workspaces = [];
        var workspacesDue = DateTimeOffset.MinValue;
        var errorLoggedAt = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(options.Interval, time);
        do
        {
            try
            {
                var now = time.GetUtcNow();
                if (now >= workspacesDue)
                {
                    workspaces = await maintenance.GetWorkspacesAsync(stoppingToken).ConfigureAwait(false);
                    workspacesDue = now + options.WorkspaceRefreshInterval;
                }

                await advancer.TickAsync(workspaces, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A database or OpenSearch outage fails every tick; the watermark simply waits.
                var now = time.GetUtcNow();
                if (now - errorLoggedAt >= ErrorLogInterval)
                {
                    errorLoggedAt = now;
                    LogTickFailed(logger, ex);
                }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search watermark ticker disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search watermark tick failed; retried at the next tick (further failures are logged at most every 30 s)")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}

public static class SearchWatermarkRegistration
{
    /// <summary>
    /// Registers the visible-watermark ticker (E07-T08): <see cref="SearchWatermarkAdvancer"/> over the OpenSearch
    /// refresher and its hosted loop. The host also registers <see cref="ISearchWatermarkStore"/>,
    /// <see cref="ISearchWorkMaintenance"/>, index management (<c>AddOpenSearchIndexManagement</c>) and its
    /// <c>IIndexPlacementStore</c>; <see cref="OpportunityMetrics"/> is optional (gauges).
    /// </summary>
    public static IServiceCollection AddSearchWatermarkTicker(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new SearchWatermarkOptions();
        configuration?.GetSection(SearchWatermarkOptions.SectionName).Bind(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISearchIndexRefresher>(sp => new OpenSearchIndexRefresher(
            sp.GetRequiredService<IIndexManager>(), sp.GetRequiredService<OpenSearchConnection>(),
            sp.GetRequiredService<ILogger<OpenSearchIndexRefresher>>()));
        services.TryAddSingleton(sp => new SearchWatermarkAdvancer(
            sp.GetRequiredService<ISearchWatermarkStore>(), sp.GetRequiredService<ISearchIndexRefresher>(),
            sp.GetRequiredService<ILogger<SearchWatermarkAdvancer>>(), sp.GetService<OpportunityMetrics>()));
        services.AddHostedService<SearchWatermarkService>();
        return services;
    }
}
