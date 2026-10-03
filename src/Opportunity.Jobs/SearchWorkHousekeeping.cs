using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.SearchWork;

namespace Opportunity.Jobs;

/// <summary>Timing of search work housekeeping (ADR-001 §1 R4, §6.3; initial values).</summary>
public sealed class SearchWorkHousekeepingOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Applied rows are kept at least this long; their day partition is dropped afterwards.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(3);

    /// <summary>Day partitions are created this far ahead; inserts beyond the horizon would fail.</summary>
    public TimeSpan PartitionHorizon { get; init; } = TimeSpan.FromDays(31);

    /// <summary>Partition maintenance runs at most this often (recovery runs every <see cref="Interval"/>).</summary>
    public TimeSpan PartitionInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>A Dispatched outbox row or task not applied/leased within this time is published again.</summary>
    public TimeSpan DispatchedTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A task lease is reclaimed only once it expired more than this long ago.</summary>
    public TimeSpan LeaseGrace { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Keeps the search work tables healthy: returns lost work to Pending (duplicates are harmless), creates day partitions
/// ahead, and drops expired partitions whose rows are all Applied. A partition that still holds un-applied rows past the
/// retention window is kept and logged as an error (the alert of ADR-001 R4). Runs in the dispatcher host.
/// </summary>
public sealed partial class SearchWorkHousekeeper(
    ISearchWorkMaintenance maintenance, SearchWorkHousekeepingOptions options, TimeProvider time, ILogger<SearchWorkHousekeeper> logger)
{
    private DateTimeOffset _partitionsDueAt = DateTimeOffset.MinValue;

    public async Task<SearchWorkRecovery> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var total = new SearchWorkRecovery(0, 0, 0);
        foreach (var workspaceId in await maintenance.GetWorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            var recovered = await maintenance.RecoverAsync(workspaceId, options.DispatchedTimeout, options.LeaseGrace, cancellationToken)
                .ConfigureAwait(false);
            total = new SearchWorkRecovery(
                total.OutboxRedispatched + recovered.OutboxRedispatched,
                total.TasksLeaseExpired + recovered.TasksLeaseExpired,
                total.TasksRedispatched + recovered.TasksRedispatched);
        }

        if (total.Total > 0)
        {
            LogRecovered(logger, total.OutboxRedispatched, total.TasksLeaseExpired, total.TasksRedispatched);
        }

        return total;
    }

    public async Task<IReadOnlyList<SearchWorkPartition>> MaintainPartitionsAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await maintenance.EnsurePartitionsAsync(now + options.PartitionHorizon, cancellationToken).ConfigureAwait(false);
        var examined = await maintenance.DropExpiredPartitionsAsync(now - options.Retention, cancellationToken).ConfigureAwait(false);
        foreach (var kept in examined.Where(p => !p.Dropped))
        {
            LogPartitionKept(logger, kept.Partition, kept.UnappliedRows);
        }

        return examined;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (time.GetUtcNow() >= _partitionsDueAt)
        {
            await MaintainPartitionsAsync(cancellationToken).ConfigureAwait(false);
            _partitionsDueAt = time.GetUtcNow() + options.PartitionInterval;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Search work recovery returned {Outbox} outbox rows, {Leases} expired task leases and {Tasks} unleased tasks to Pending")]
    private static partial void LogRecovered(ILogger logger, int outbox, int leases, int tasks);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Search work partition {Partition} is past retention but holds {Rows} rows that are not Applied; it is kept")]
    private static partial void LogPartitionKept(ILogger logger, string partition, long rows);
}

/// <summary>
/// Runs <see cref="SearchWorkHousekeeper"/> at start and every <see cref="SearchWorkHousekeepingOptions.Interval"/>. The
/// housekeeper is resolved lazily, so a host without PostgreSQL logs that housekeeping is disabled instead of failing.
/// </summary>
public sealed partial class SearchWorkHousekeepingService(
    IServiceProvider services, SearchWorkHousekeepingOptions options, ILogger<SearchWorkHousekeepingService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SearchWorkHousekeeper housekeeper;
        try
        {
            housekeeper = services.GetRequiredService<SearchWorkHousekeeper>();
        }
        catch (InvalidOperationException ex)
        {
            LogDisabled(logger, ex.Message);
            return;
        }

        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                await housekeeper.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Search work housekeeping failed; retrying at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Search work housekeeping disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);
}

public static class SearchWorkHousekeepingRegistration
{
    /// <summary>Registers the housekeeping loop. The host must also register <see cref="ISearchWorkMaintenance"/>.</summary>
    public static IServiceCollection AddSearchWorkHousekeeping(this IServiceCollection services, SearchWorkHousekeepingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new SearchWorkHousekeepingOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SearchWorkHousekeeper>();
        services.AddHostedService<SearchWorkHousekeepingService>();
        return services;
    }
}
