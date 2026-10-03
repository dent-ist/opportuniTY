using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace Opportunity.Data.Migrations;

/// <summary>
/// Readiness check for API and worker hosts: unhealthy while the database schema is behind the version this build
/// expects. A newer schema is healthy, because expand/contract migrations keep version N-1 code working.
/// </summary>
public sealed class SchemaVersionHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _expectedVersion;

    public SchemaVersionHealthCheck(NpgsqlDataSource dataSource)
        : this(dataSource, MigrationCatalog.LatestVersion)
    {
    }

    public SchemaVersionHealthCheck(NpgsqlDataSource dataSource, int expectedVersion)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _expectedVersion = expectedVersion;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        int? current;
        try
        {
            current = await SchemaHistory.GetCurrentVersionAsync(_dataSource, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Cannot read the schema version.", ex);
        }

        var data = new Dictionary<string, object>
        {
            ["expectedVersion"] = _expectedVersion,
            ["currentVersion"] = current ?? 0,
        };

        return current >= _expectedVersion
            ? HealthCheckResult.Healthy($"Schema version {current}.", data)
            : HealthCheckResult.Unhealthy(
                $"Schema version {current?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} is behind " +
                $"the expected version {_expectedVersion}; run the migrator.",
                data: data);
    }
}
