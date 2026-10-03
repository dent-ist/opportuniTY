using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Opportunity.Application.Bootstrap;
using Opportunity.Data.Migrations;
using Opportunity.Messaging;
using Opportunity.Search;
using Opportunity.Storage;

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

        // Infrastructure bootstrap steps owned by their modules; each runs only when its dependency is configured.
        string? configurationError = null;
        void Register(bool configured, Action register)
        {
            if (!configured || configurationError is not null)
            {
                return;
            }

            try
            {
                register();
            }
            catch (InvalidOperationException ex)
            {
                configurationError = ex.Message;
            }
        }

        var configuration = builder.Configuration;
        Register(
            !string.IsNullOrWhiteSpace(configuration.GetConnectionString(OpenSearchOptions.ConnectionStringName))
                || configuration.GetSection(OpenSearchOptions.SectionName).GetValue<string>(nameof(OpenSearchOptions.Endpoint)) is { Length: > 0 },
            () => builder.Services.AddOpenSearchIndexTemplateBootstrap(OpenSearchOptions.Bind(configuration)));
        Register(
            !string.IsNullOrWhiteSpace(configuration.GetConnectionString(RabbitMqOptions.ConnectionStringName)),
            () => builder.Services.AddRabbitMqTopologyBootstrap(RabbitMqOptions.Bind(configuration)));
        Register(
            configuration.GetSection(ObjectStorageOptions.SectionName).Exists(),
            () =>
            {
                var storage = new ObjectStorageOptions();
                configuration.GetSection(ObjectStorageOptions.SectionName).Bind(storage);
                storage.Validate();
                builder.Services.AddObjectStorage(storage);
            });

        configureServices?.Invoke(builder.Services);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Opportunity.Migrator");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LogMissingConnectionString(logger, ConnectionStringName);
            return ExitConfigurationError;
        }

        if (configurationError is not null)
        {
            LogInvalidConfiguration(logger, configurationError);
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

        IInfrastructureBootstrapStep[] steps;
        try
        {
            steps = [.. host.Services.GetServices<IInfrastructureBootstrapStep>().OrderBy(s => s.Order)];
        }
#pragma warning disable CA1031 // A step that cannot even be constructed (bad provider settings) is a bootstrap failure.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, "(construction)", ex);
            return ExitBootstrapFailed;
        }

        foreach (var step in steps)
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

    [LoggerMessage(Level = LogLevel.Critical, Message = "Invalid configuration: {Error}")]
    private static partial void LogInvalidConfiguration(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running bootstrap step {Step}")]
    private static partial void LogRunningStep(ILogger logger, string step);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Bootstrap step {Step} failed")]
    private static partial void LogStepFailed(ILogger logger, string step, Exception exception);
}
