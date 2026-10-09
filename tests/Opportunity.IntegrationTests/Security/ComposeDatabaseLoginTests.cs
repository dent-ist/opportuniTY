using System.Security.Cryptography;

using AwesomeAssertions;

using Npgsql;

using Testcontainers.PostgreSql;

namespace Opportunity.IntegrationTests.Security;

/// <summary>
/// E05-T07: the developer profile's PostgreSQL init scripts (deploy/docker-compose/postgres/init) run on a fresh
/// container exactly as Compose runs them on first start, and again as <c>./opportunity.sh up</c> does. Every runtime
/// component gets its own login: none is a superuser or bypasses RLS, the API and worker logins are members of
/// <c>opportunity_app</c> only, the metrics exporter's of <c>pg_monitor</c> only, and a re-run applies a changed
/// password. Passwords are random per run.
/// </summary>
public sealed class ComposeDatabaseLoginTests : IAsyncLifetime
{
    private readonly Dictionary<string, string> _passwords = new(StringComparer.Ordinal)
    {
        ["OPPORTUNITY_DB_OWNER_PASSWORD"] = Random(),
        ["OPPORTUNITY_DB_API_PASSWORD"] = Random(),
        ["OPPORTUNITY_DB_WORKER_PASSWORD"] = Random(),
        ["OPPORTUNITY_DB_MONITOR_PASSWORD"] = Random(),
    };

    private PostgreSqlContainer? _container;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = new PostgreSqlBuilder($"postgres:{Version()}")
            .WithBindMount(Path.Combine(RepositoryRoot(), "deploy", "docker-compose", "postgres", "init"), "/docker-entrypoint-initdb.d", DotNet.Testcontainers.Configurations.AccessMode.ReadOnly)
            .WithEnvironment("OPPORTUNITY_DB", "opportunity")
            .WithEnvironment("OPPORTUNITY_DB_OWNER_USER", "opportunity_owner");
        foreach (var (name, value) in _passwords)
        {
            builder = builder.WithEnvironment(name, value);
        }

        _container = builder.Build();
        await _container.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Every_component_gets_its_own_least_privileged_login_and_reruns_apply_new_passwords()
    {
        var roles = await ColumnAsync(Admin(),
            """
            SELECT r.rolname || '|' || r.rolcanlogin || '|' || r.rolsuper || '|' || r.rolbypassrls || '|' || r.rolcreaterole || '|' || r.rolcreatedb
                   || '|' || coalesce((SELECT string_agg(g.rolname, ',' ORDER BY g.rolname) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
                                      WHERE m.member = r.oid), '')
            FROM pg_roles r WHERE r.rolname IN ('opportunity_api', 'opportunity_worker', 'opportunity_monitor', 'opportunity_owner') ORDER BY r.rolname
            """);
        roles.Should().Equal(
            "opportunity_api|true|false|false|false|false|opportunity_app",
            "opportunity_monitor|true|false|false|false|false|pg_monitor",
            "opportunity_owner|true|false|false|false|false|",
            "opportunity_worker|true|false|false|false|false|opportunity_app");

        foreach (var (user, variable) in new[] { ("opportunity_api", "OPPORTUNITY_DB_API_PASSWORD"), ("opportunity_worker", "OPPORTUNITY_DB_WORKER_PASSWORD"), ("opportunity_monitor", "OPPORTUNITY_DB_MONITOR_PASSWORD") })
        {
            (await ColumnAsync(Login(user, _passwords[variable]), "SELECT current_user::text")).Should().Equal(user);
        }

        // ./opportunity.sh up runs the script again: idempotent, and a password changed in .env takes effect.
        var changed = Random();
        var rerun = await _container!.ExecAsync(
            ["env", $"OPPORTUNITY_DB_API_PASSWORD={changed}", "sh", "/docker-entrypoint-initdb.d/30-component-logins.sh"], Ct);
        rerun.ExitCode.Should().Be(0, rerun.Stderr);
        (await ColumnAsync(Login("opportunity_api", changed), "SELECT current_user::text")).Should().Equal("opportunity_api");
        var stale = () => ColumnAsync(Login("opportunity_api", _passwords["OPPORTUNITY_DB_API_PASSWORD"]), "SELECT 1::text");
        await stale.Should().ThrowAsync<PostgresException>();
    }

    private string Admin() => _container!.GetConnectionString();

    private string Login(string user, string password) =>
        new NpgsqlConnectionStringBuilder(Admin()) { Username = user, Password = password, Database = "opportunity", Pooling = false }.ConnectionString;

    private static async Task<List<string>> ColumnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static string Random() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string Version() =>
        File.ReadLines(Path.Combine(RepositoryRoot(), "versions.env")).Single(l => l.StartsWith("POSTGRES=", StringComparison.Ordinal))["POSTGRES=".Length..].Trim();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }
}
