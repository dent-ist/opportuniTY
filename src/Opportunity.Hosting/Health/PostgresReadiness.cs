using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Opportunity.Data.Migrations;

namespace Opportunity.Hosting.Health;

/// <summary>
/// PostgreSQL wiring shared by API and worker hosts. When <c>ConnectionStrings:App</c> is configured, the hosts get an
/// <see cref="NpgsqlDataSource"/> and a readiness check that stays unhealthy until the migrator has brought the schema
/// up to the version this build expects (<see cref="SchemaVersionHealthCheck"/>). Hosts never migrate themselves.
/// </summary>
public static class PostgresReadiness
{
    /// <summary>Runtime login (member of <c>opportunity_app</c>): <c>ConnectionStrings__App</c>.</summary>
    public const string ConnectionStringName = "App";

    public const string SchemaCheckName = "postgres-schema";

    public static IServiceCollection AddOpportunityPostgres(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolved lazily, so configuration added after registration (tests, late providers) is honoured.
        services.TryAddSingleton(sp => NpgsqlDataSource.Create(
            sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName) is { Length: > 0 } connectionString
                ? connectionString
                : throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings__{ConnectionStringName}).")));
        services.AddSingleton<IConfigureOptions<HealthCheckServiceOptions>, AddSchemaCheckWhenConfigured>();
        return services;
    }

    private sealed class AddSchemaCheckWhenConfigured(IConfiguration configuration) : IConfigureOptions<HealthCheckServiceOptions>
    {
        public void Configure(HealthCheckServiceOptions options)
        {
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName)))
            {
                return;
            }

            options.Registrations.Add(new HealthCheckRegistration(
                SchemaCheckName,
                sp => new SchemaVersionHealthCheck(sp.GetRequiredService<NpgsqlDataSource>()),
                failureStatus: null,
                tags: [HealthTags.Ready]));
        }
    }
}
