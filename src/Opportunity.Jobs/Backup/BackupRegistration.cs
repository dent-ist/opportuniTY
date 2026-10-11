using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Backup;
using Opportunity.Application.Keys;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;

namespace Opportunity.Jobs.Backup;

/// <summary>
/// Runs the search snapshot job (ADR-016 §3.2) every <see cref="SearchSnapshotOptions.Interval"/> in the dispatcher host.
/// Several dispatcher replicas are tolerated: a run is skipped while a snapshot newer than most of the interval exists.
/// </summary>
public sealed partial class SearchSnapshotJobService(
    IServiceProvider services, SearchSnapshotOptions options, TimeProvider time, ILogger<SearchSnapshotJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SearchSnapshotService job;
        ISearchSnapshotStore store;
        try
        {
            job = services.GetRequiredService<SearchSnapshotService>();
            store = services.GetRequiredService<ISearchSnapshotStore>();
        }
        catch (InvalidOperationException ex)
        {
            LogDisabled(logger, ex.Message);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                var newest = await store.ListAsync(1, stoppingToken).ConfigureAwait(false);
                if (newest.Count > 0 && time.GetUtcNow() - newest[0].RequestedAt < options.Interval * 0.9)
                {
                    continue;
                }

                var run = await job.TakeAsync(stoppingToken).ConfigureAwait(false);
                LogTaken(logger, run.Record.Name, run.Outcome.State, run.Record.Workspaces, run.Outcome.Indices.Count, run.Pruned.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A store outage fails this run; the next tick retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search snapshot job disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Search snapshot {Snapshot}: {State}, {Workspaces} workspace anchor(s), {Indices} index(es); {Pruned} expired snapshot(s) deleted")]
    private static partial void LogTaken(ILogger logger, string snapshot, SearchSnapshotState state, int workspaces, int indices, int pruned);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search snapshot failed; retried at the next interval (alert SearchSnapshotStale)")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

public static class BackupRegistration
{
    /// <summary>
    /// The snapshot job (<see cref="SearchSnapshotService"/>). The host also registers <see cref="ISearchSnapshotStore"/>
    /// (Data) and <see cref="ISearchSnapshotRepository"/> (Search).
    /// </summary>
    public static IServiceCollection AddSearchSnapshotJobCore(this IServiceCollection services, SearchSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new SearchSnapshotService(
            sp.GetRequiredService<ISearchSnapshotStore>(), sp.GetRequiredService<ISearchSnapshotRepository>(),
            sp.GetRequiredService<SearchSnapshotOptions>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>The snapshot job and its hourly loop (dispatcher host, when <see cref="SearchSnapshotOptions.Enabled"/>).</summary>
    public static IServiceCollection AddSearchSnapshotJob(this IServiceCollection services, SearchSnapshotOptions options)
    {
        services.AddSearchSnapshotJobCore(options);
        services.AddHostedService<SearchSnapshotJobService>();
        return services;
    }

    /// <summary>
    /// The restore tool (<see cref="RestoreService"/>, ADR-016 §8). The host registers the stores (Data), the search side
    /// (Search), the audit chain, key management and, for the object gates and the signed record, object storage; an
    /// <see cref="IObjectVersionRecovery"/> and <see cref="ISearchSnapshotRepository"/> are used when present.
    /// </summary>
    public static IServiceCollection AddRestoreTooling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new RestoreService(
            sp.GetRequiredService<IRestoreStore>(),
            sp.GetRequiredService<ISearchSnapshotStore>(),
            sp.GetRequiredService<ISearchRestoreChecks>(),
            sp.GetRequiredService<IWorkspaceSearchPurge>(),
            sp.GetRequiredService<IAuditEventWriter>(),
            sp.GetRequiredService<IAuditChainSealer>(),
            sp.GetRequiredService<IAuditChainReader>(),
            sp.GetRequiredService<ISigningKeyProvider>(),
            sp.GetRequiredService<IKeyEncryptionKeyProvider>(),
            sp.GetRequiredService<IWorkspaceDataKeyStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IObjectStore>(),
            sp.GetService<IObjectVersionRecovery>(),
            sp.GetService<ISearchSnapshotRepository>()));
        return services;
    }
}
