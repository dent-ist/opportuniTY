using AwesomeAssertions;

using Npgsql;

using Opportunity.Testing.Postgres;

namespace Opportunity.IntegrationTests.Containers;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresFixtureTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Round_trips_a_row_in_an_isolated_database()
    {
        await using var database = await fixture.CreateDatabaseAsync(Ct);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);

        await ExecuteAsync(connection, "CREATE TABLE item (id int PRIMARY KEY, name text NOT NULL)");
        await ExecuteAsync(connection, "INSERT INTO item VALUES (1, 'alpha')");

        (await ScalarAsync(connection, "SELECT name FROM item WHERE id = 1")).Should().Be("alpha");
    }

    [Fact]
    public async Task Databases_are_isolated_per_test_and_dropped_on_dispose()
    {
        string firstName;
        await using (var first = await fixture.CreateDatabaseAsync(Ct))
        {
            firstName = first.Name;
            await using var second = await fixture.CreateDatabaseAsync(Ct);
            second.Name.Should().NotBe(first.Name);

            await using (var a = new NpgsqlConnection(first.ConnectionString))
            {
                await a.OpenAsync(Ct);
                await ExecuteAsync(a, "CREATE TABLE only_in_first (id int)");
            }

            await using var b = new NpgsqlConnection(second.ConnectionString);
            await b.OpenAsync(Ct);
            (await ScalarAsync(b, "SELECT to_regclass('only_in_first')::text")).Should().BeNull();
        }

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync(Ct);
        await using var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", admin);
        exists.Parameters.AddWithValue("name", firstName);
        ((long)(await exists.ExecuteScalarAsync(Ct))!).Should().Be(0);
    }

    [Fact]
    public async Task Latency_toxic_slows_queries_and_lifting_it_restores_speed()
    {
        await using var database = await fixture.CreateDatabaseAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        await using var connection = new NpgsqlConnection(NoPooling(database.ConnectionStringVia(proxy)));
        await connection.OpenAsync(Ct);

        await using (await proxy.AddLatencyAsync(Timing.InjectedLatency, cancellationToken: Ct))
        {
            (await Timing.MeasureAsync(() => ScalarAsync(connection, "SELECT 1"))).Should().BeGreaterThanOrEqualTo(Timing.MinimumObservedLatency);
        }

        (await Timing.MeasureAsync(() => ScalarAsync(connection, "SELECT 1"))).Should().BeLessThan(Timing.MinimumObservedLatency);
    }

    [Fact]
    public async Task Cut_link_fails_queries_and_restoring_it_recovers()
    {
        await using var database = await fixture.CreateDatabaseAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        var connectionString = NoPooling(database.ConnectionStringVia(proxy));
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await proxy.CutAsync(Ct);

            var query = () => ScalarAsync(connection, "SELECT 1");
            await query.Should().ThrowAsync<NpgsqlException>();
        }

        var reconnect = async () =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
        };
        await reconnect.Should().ThrowAsync<NpgsqlException>();

        await proxy.RestoreAsync(Ct);
        await using var recovered = new NpgsqlConnection(connectionString);
        await recovered.OpenAsync(Ct);
        (await ScalarAsync(recovered, "SELECT 42")).Should().Be(42);
    }

    [Fact]
    public async Task Timeout_toxic_makes_commands_time_out()
    {
        await using var database = await fixture.CreateDatabaseAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        var connectionString = new NpgsqlConnectionStringBuilder(NoPooling(database.ConnectionStringVia(proxy)))
        {
            CommandTimeout = 1,
            CancellationTimeout = -1,
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        await using (await proxy.AddTimeoutAsync(TimeSpan.Zero, cancellationToken: Ct))
        {
            var query = () => ScalarAsync(connection, "SELECT 1");
            (await query.Should().ThrowAsync<NpgsqlException>()).Which.InnerException.Should().BeOfType<TimeoutException>();
        }

        await using var recovered = new NpgsqlConnection(connectionString);
        await recovered.OpenAsync(Ct);
        (await ScalarAsync(recovered, "SELECT 1")).Should().Be(1);
    }

    [Fact]
    public async Task Reset_peer_toxic_breaks_the_connection()
    {
        await using var database = await fixture.CreateDatabaseAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        var connectionString = NoPooling(database.ConnectionStringVia(proxy));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        await using (await proxy.AddResetPeerAsync(cancellationToken: Ct))
        {
            var query = () => ScalarAsync(connection, "SELECT 1");
            await query.Should().ThrowAsync<NpgsqlException>();
        }

        await using var recovered = new NpgsqlConnection(connectionString);
        await recovered.OpenAsync(Ct);
        (await ScalarAsync(recovered, "SELECT 1")).Should().Be(1);
    }

    private static string NoPooling(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 5 }.ConnectionString;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }
}
