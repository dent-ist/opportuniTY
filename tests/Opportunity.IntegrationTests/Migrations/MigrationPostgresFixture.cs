using Npgsql;

using Testcontainers.PostgreSql;

namespace Opportunity.IntegrationTests.Migrations;

/// <summary>
/// Minimal PostgreSQL container for the migrator suite; each test gets its own empty database.
/// Image version comes from <c>versions.env</c> (POSTGRES). To be folded into the shared fixture library.
/// </summary>
public sealed class MigrationPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string AdminConnectionString => _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Container not started.");

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder($"postgres:{ReadPostgresVersion()}").Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty database and returns a superuser connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "mig_" + Guid.NewGuid().ToString("N")[..16];
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ConnectionString;
    }

    private static string ReadPostgresVersion()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "versions.env");
            if (File.Exists(file))
            {
                return File.ReadLines(file)
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith("POSTGRES=", StringComparison.Ordinal))
                    .Select(l => l["POSTGRES=".Length..])
                    .Single();
            }
        }

        throw new InvalidOperationException("versions.env not found above " + AppContext.BaseDirectory);
    }
}

[CollectionDefinition(Name)]
public sealed class MigrationPostgresGroup : ICollectionFixture<MigrationPostgresFixture>
{
    public const string Name = "Migrations PostgreSQL";
}
