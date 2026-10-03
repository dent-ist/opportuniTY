using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Telemetry;
using Opportunity.Data.Coding;

namespace Opportunity.Data.Audit;

public sealed class PartitionMaintenanceOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>ADR-013 §1.1: audit partitions exist at least 3 months ahead.</summary>
    public int AuditMonthsAhead { get; init; } = 3;

    /// <summary>The V0004 horizon for coding-event provenance partitions (ADR-005 R2).</summary>
    public int CodingEventMonthsAhead { get; init; } = 24;

    /// <summary>Below this many months of audit partitions ahead, every run logs an error (a missing partition fails writes).</summary>
    public int AuditAlertMonthsAhead { get; init; } = 1;
}

/// <param name="AuditCreated">Audit partitions created by this run.</param>
/// <param name="CodingEventCreated">Coding-event partitions created by this run.</param>
/// <param name="AuditHorizon">First UTC month without an audit partition after the newest one.</param>
public sealed record PartitionMaintenanceResult(int AuditCreated, int CodingEventCreated, DateOnly? AuditHorizon);

/// <summary>
/// Creates the monthly partitions of the time-partitioned append-only tables ahead of time (ADR-005 R2, ADR-013 §1.1)
/// through their <c>SECURITY DEFINER</c> functions, so no runtime role needs DDL. Idempotent and safe to run from
/// several replicas: the functions serialize on an advisory lock.
/// </summary>
public sealed class PartitionMaintenance(NpgsqlDataSource dataSource, PartitionMaintenanceOptions options)
{
    public async Task<PartitionMaintenanceResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await WorkspaceTransaction.BeginInstallationAsync(dataSource, cancellationToken).ConfigureAwait(false);
        var codingEvent = await CodingRepository.EnsureEventPartitionsAsync(tx, options.CodingEventMonthsAhead, cancellationToken)
            .ConfigureAwait(false);
        await using var command = tx.Command(
            "SELECT audit.ensure_partitions(now() + make_interval(months => @audit)), audit.partition_horizon()");
        command.Parameters.AddWithValue("audit", options.AuditMonthsAhead);
        PartitionMaintenanceResult result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = new PartitionMaintenanceResult(
                reader.GetInt32(0),
                codingEvent,
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1));
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}

/// <summary>Runs <see cref="PartitionMaintenance"/> at start and every <see cref="PartitionMaintenanceOptions.Interval"/>.</summary>
public sealed partial class PartitionMaintenanceService(
    IServiceProvider services, PartitionMaintenanceOptions options, TimeProvider time, ILogger<PartitionMaintenanceService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PartitionMaintenance maintenance;
        try
        {
            maintenance = services.GetRequiredService<PartitionMaintenance>();
        }
        catch (InvalidOperationException ex)
        {
            // No PostgreSQL configured for this host (e.g. a test host without a database): nothing to maintain.
            LogDisabled(logger, ex.Message);
            return;
        }

        using var timer = new PeriodicTimer(options.Interval, time);
        do
        {
            try
            {
                var result = await maintenance.EnsureAsync(stoppingToken).ConfigureAwait(false);
                if (result.AuditCreated + result.CodingEventCreated > 0)
                {
                    LogCreated(logger, result.AuditCreated, result.CodingEventCreated, result.AuditHorizon);
                }

                var alertAt = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddMonths(options.AuditAlertMonthsAhead);
                if (result.AuditHorizon is not { } horizon || horizon <= alertAt)
                {
                    LogHorizonLow(logger, result.AuditHorizon);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Partition maintenance created {Audit} audit and {CodingEvent} coding-event partitions; audit covered until {Horizon}")]
    private static partial void LogCreated(ILogger logger, int audit, int codingEvent, DateOnly? horizon);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit partitions end at {Horizon}; audit writes, and the actions they record, fail beyond it")]
    private static partial void LogHorizonLow(ILogger logger, DateOnly? horizon);

    [LoggerMessage(Level = LogLevel.Error, Message = "Partition maintenance failed; retrying at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Partition maintenance disabled: {Reason}")]
    private static partial void LogDisabled(ILogger logger, string reason);
}

public static class AuditStoreRegistration
{
    /// <summary>
    /// The PostgreSQL audit store: <see cref="IAuditEventWriter"/> and <see cref="IAuditEventReader"/>. The data source is
    /// resolved lazily. Register before <c>AddOpportunityAuthentication</c>, which requires a writer.
    /// </summary>
    public static IServiceCollection AddPostgresAuditStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAuditEventWriter>(sp => new PostgresAuditEventWriter(
            new Lazy<NpgsqlDataSource>(sp.GetRequiredService<NpgsqlDataSource>), sp.GetService<OpportunityMetrics>()));
        services.TryAddSingleton<IAuditEventReader>(sp => new PostgresAuditEventReader(sp.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }

    /// <summary>The partition maintenance loop (<see cref="PartitionMaintenanceService"/>); hosted by the dispatcher.</summary>
    public static IServiceCollection AddPartitionMaintenance(this IServiceCollection services, PartitionMaintenanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new PartitionMaintenanceOptions());
        services.TryAddSingleton<PartitionMaintenance>();
        services.AddHostedService<PartitionMaintenanceService>();
        return services;
    }
}
