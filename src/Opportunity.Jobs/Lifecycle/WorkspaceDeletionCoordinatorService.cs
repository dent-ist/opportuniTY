using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Keys;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;

using Opportunity.Application.Workspaces.Deletion;

namespace Opportunity.Jobs.Lifecycle;

/// <summary>
/// Runs the workspace deletion coordinator every <see cref="WorkspaceDeletionOptions.PollInterval"/> (in the indexing
/// worker host, which reaches PostgreSQL, OpenSearch, object storage and the key provider). Several replicas are safe:
/// each run is driven by the holder of its lease. Dependencies are resolved when the host starts, so a host without one
/// of the stores logs that the coordinator is disabled.
/// </summary>
public sealed partial class WorkspaceDeletionCoordinatorService(
    IServiceProvider services, WorkspaceDeletionOptions options, TimeProvider time, ILogger<WorkspaceDeletionCoordinatorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkspaceDeletionCoordinator coordinator;
        try
        {
            coordinator = services.GetRequiredService<WorkspaceDeletionCoordinator>();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workspace deletion coordinator disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Workspace deletion coordinator pass failed; retried at the next pass")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}

public static class WorkspaceDeletionCoordinatorRegistration
{
    /// <summary>
    /// The coordinator without its hosted loop (tests drive its passes). The host also registers
    /// <see cref="IWorkspaceDeletionStore"/>, <see cref="IWorkspaceSearchPurge"/>, <see cref="IObjectStore"/> and
    /// <see cref="IWorkspaceCryptoShredder"/>; an <see cref="ISigningKeyProvider"/> signs certificates when present.
    /// </summary>
    public static IServiceCollection AddWorkspaceDeletionCoordinatorCore(this IServiceCollection services, WorkspaceDeletionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new WorkspaceDeletionCoordinator(
            sp.GetRequiredService<IWorkspaceDeletionStore>(), sp.GetRequiredService<IWorkspaceSearchPurge>(), sp.GetRequiredService<IObjectStore>(),
            sp.GetRequiredService<IWorkspaceCryptoShredder>(), sp.GetRequiredService<WorkspaceDeletionOptions>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WorkspaceDeletionCoordinator>>(), sp.GetService<ISigningKeyProvider>(),
            sp.GetService<IBeforeWorkspaceDeletion>()));
        return services;
    }

    /// <summary>The coordinator and its hosted loop (E20-T02).</summary>
    public static IServiceCollection AddWorkspaceDeletionCoordinator(this IServiceCollection services, WorkspaceDeletionOptions options)
    {
        services.AddWorkspaceDeletionCoordinatorCore(options);
        services.AddHostedService<WorkspaceDeletionCoordinatorService>();
        return services;
    }
}
