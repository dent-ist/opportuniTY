using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opportunity.Application.Jobs;

namespace Opportunity.Jobs;

/// <summary>Lease timing of ADR-010 §3 (initial values).</summary>
public sealed class JobLeaseOptions
{
    /// <summary>Length of a claim's lease, and of each extension.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a running worker extends its lease.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Hard cap on one chunk's runtime, enforced by the worker watchdog.</summary>
    public TimeSpan MaxChunkRuntime { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A lease is reclaimed only once it expired more than this long ago.</summary>
    public TimeSpan SweepGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Jobs per workspace handled in one sweep call.</summary>
    public int SweepBatchSize { get; init; } = 100;
}

/// <summary>
/// The lease sweeper (ADR-010 §3.3): returns chunks whose lease expired to Pending, fails them when their attempts are
/// used up, and cancels them when their job is cancelling. Visits workspaces one at a time (ADR-005 P10), each in its
/// own workspace transaction (RLS). Runs in the dispatcher host.
/// </summary>
public sealed partial class JobLeaseSweeper(IJobChunkRepository chunks, JobLeaseOptions options, ILogger<JobLeaseSweeper> logger)
{
    public async Task<LeaseRecoveryResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        var total = LeaseRecoveryResult.None;
        foreach (var workspaceId in await chunks.GetWorkspacesToSweepAsync(cancellationToken).ConfigureAwait(false))
        {
            LeaseRecoveryResult batch;
            do
            {
                batch = await chunks.RecoverExpiredLeasesAsync(workspaceId, options.SweepGrace, options.SweepBatchSize, cancellationToken)
                    .ConfigureAwait(false);
                total = total.Add(batch);
            }
            while (batch.Total >= options.SweepBatchSize);
        }

        if (total.Total > 0)
        {
            LogRecovered(logger, total.ReturnedToPending, total.Failed, total.Cancelled);
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease sweeper returned {Pending} chunks to Pending, failed {Failed} and cancelled {Cancelled}")]
    private static partial void LogRecovered(ILogger logger, int pending, int failed, int cancelled);
}

/// <summary>Runs <see cref="JobLeaseSweeper"/> every <see cref="JobLeaseOptions.SweepInterval"/>.</summary>
public sealed partial class JobLeaseSweeperService(JobLeaseSweeper sweeper, JobLeaseOptions options, ILogger<JobLeaseSweeperService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval);
        do
        {
            try
            {
                await sweeper.SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Lease sweep failed; retrying at the next interval")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}

public static class JobLeaseSweeperRegistration
{
    /// <summary>Registers the sweeper loop. The host must also register <see cref="IJobChunkRepository"/>.</summary>
    public static IServiceCollection AddJobLeaseSweeper(this IServiceCollection services, JobLeaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new JobLeaseOptions());
        services.TryAddSingleton<JobLeaseSweeper>();
        services.AddHostedService<JobLeaseSweeperService>();
        return services;
    }
}
