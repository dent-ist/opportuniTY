using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Opportunity.Application.Bootstrap;
using Opportunity.Data.Migrations;

namespace Opportunity.Migrator;

/// <summary>
/// One-shot migrator: applies PostgreSQL migrations under the advisory lock, then runs every registered
/// <see cref="IInfrastructureBootstrapStep"/>, then exits. Never run from API or worker startup.
/// </summary>
public static partial class MigratorApp
{
    public const string ConnectionStringName = "Migrator";

    public const int ExitSuccess = 0;
    public const int ExitMigrationFailed = 1;
    public const int ExitConfigurationError = 2;
    public const int ExitBootstrapFailed = 3;

    /// <param name="args">Command-line configuration, e.g. <c>--ConnectionStrings:Migrator=...</c>.</param>
    /// <param name="configureServices">Extra registrations (bootstrap steps owned by infrastructure modules, tests).</param>
    public static async Task<int> RunAsync(
        string[] args, Action<IServiceCollection>? configureServices = null, CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);

        var options = new MigratorOptions();
        builder.Configuration.GetSection(MigratorOptions.SectionName).Bind(options);

        builder.Services.AddSingleton(options);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        }

        builder.Services.AddSingleton(sp => new PostgresMigrator(
            sp.GetRequiredService<NpgsqlDataSource>(),
            options: options,
            logger: sp.GetRequiredService<ILogger<PostgresMigrator>>()));
        configureServices?.Invoke(builder.Services);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Opportunity.Migrator");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LogMissingConnectionString(logger, ConnectionStringName);
            return ExitConfigurationError;
        }

        try
        {
            await host.Services.GetRequiredService<PostgresMigrator>().MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MigrationException or NpgsqlException)
        {
            LogMigrationFailed(logger, ex);
            return ExitMigrationFailed;
        }

        foreach (var step in host.Services.GetServices<IInfrastructureBootstrapStep>().OrderBy(s => s.Order))
        {
            try
            {
                LogRunningStep(logger, step.Name);
                await step.RunAsync(cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Any failing step must turn into a non-zero exit code, not a crash dump.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogStepFailed(logger, step.Name, ex);
                return ExitBootstrapFailed;
            }
        }

        return ExitSuccess;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Connection string '{Name}' is not configured (ConnectionStrings__{Name})")]
    private static partial void LogMissingConnectionString(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running bootstrap step {Step}")]
    private static partial void LogRunningStep(ILogger logger, string step);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Bootstrap step {Step} failed")]
    private static partial void LogStepFailed(ILogger logger, string step, Exception exception);
}
