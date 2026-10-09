using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Keys;

namespace Opportunity.Data.Audit;

/// <summary>
/// Runs <see cref="IAuditChainSealer"/> every <see cref="AuditChainOptions.SealInterval"/> and takes scheduled
/// checkpoints every <see cref="AuditChainOptions.CheckpointInterval"/> (ADR-013 §3.2, §3.4); hosted by the dispatcher.
/// Several replicas may run it: chains are sealed by whichever holds the chain's advisory lock.
/// </summary>
public sealed partial class AuditChainSealerService(
    IServiceProvider services, AuditChainOptions options, TimeProvider time, ILogger<AuditChainSealerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IAuditChainSealer sealer;
        try
        {
            sealer = services.GetRequiredService<IAuditChainSealer>();
        }
        catch (InvalidOperationException ex)
        {
            // No sealer login configured (ConnectionStrings:AuditSealer): events stay unsealed until one is.
            LogDisabled(logger, ex.Message);
            return;
        }

        var nextCheckpoint = time.GetUtcNow() + options.CheckpointInterval;
        using var timer = new PeriodicTimer(options.SealInterval, time);
        do
        {
            try
            {
                var result = await sealer.SealAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                if (result.Sealed > 0)
                {
                    LogSealed(logger, result.Sealed, result.Chains);
                }

                if (time.GetUtcNow() >= nextCheckpoint)
                {
                    nextCheckpoint = time.GetUtcNow() + options.CheckpointInterval;
                    await sealer.CheckpointAsync(AuditCheckpointReason.Scheduled, cancellationToken: stoppingToken).ConfigureAwait(false);
                }
            }
            catch (AuditChainBrokenException ex)
            {
                LogBroken(logger, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audit sealer chained {Count} events in {Chains} chains")]
    private static partial void LogSealed(ILogger logger, long count, int chains);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Audit hash chain broken; no checkpoint was signed over it. Run `audit verify` (docs/operations/audit-chain.md)")]
    private static partial void LogBroken(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit sealing failed; retrying at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit sealing disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);
}

public static class AuditChainRegistration
{
    /// <summary>
    /// <see cref="IAuditChainSealer"/> and <see cref="PostgresAuditChainVerifier"/> over the sealer login's data source
    /// (resolved lazily; <paramref name="sealerDataSource"/> throws <see cref="InvalidOperationException"/> when no sealer
    /// login is configured). Needs <see cref="ISigningKeyProvider"/>; uses <see cref="IAuditEventWriter"/> when present.
    /// </summary>
    public static IServiceCollection AddAuditChain(
        this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> sealerDataSource, AuditChainOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sealerDataSource);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new AuditChainOptions());
        services.TryAddSingleton(sp => new AuditChainDataSource(sealerDataSource(sp)));
        services.TryAddSingleton<IAuditChainSealer>(sp => new PostgresAuditChainSealer(
            sp.GetRequiredService<AuditChainDataSource>(),
            sp.GetRequiredService<ISigningKeyProvider>(),
            sp.GetService<IAuditEventWriter>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuditChainOptions>(),
            sp.GetService<ILogger<PostgresAuditChainSealer>>()));
        services.TryAddSingleton(sp => new PostgresAuditChainVerifier(sp.GetRequiredService<AuditChainDataSource>()));
        return services;
    }

    /// <summary>The sealing loop (<see cref="AuditChainSealerService"/>); hosted by the dispatcher.</summary>
    public static IServiceCollection AddAuditChainSealing(
        this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> sealerDataSource, AuditChainOptions? options = null)
    {
        services.AddAuditChain(sealerDataSource, options);
        services.AddHostedService<AuditChainSealerService>();
        return services;
    }
}
