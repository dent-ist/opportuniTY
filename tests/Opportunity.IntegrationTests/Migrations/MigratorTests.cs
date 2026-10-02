using AwesomeAssertions;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

using Opportunity.Data.Migrations;
using Opportunity.Migrator;

namespace Opportunity.IntegrationTests.Migrations;

[Collection(MigrationPostgresGroup.Name)]
public sealed class MigratorTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrator_applies_all_scripts_to_an_empty_database_and_exits_0()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var exitCode = await RunMigratorAppAsync(connectionString);

        exitCode.Should().Be(MigratorApp.ExitSuccess);
        var history = await ReadHistoryAsync(connectionString);
        history.Select(h => h.Version).Should().Equal(MigrationCatalog.Scripts.Select(s => s.Version));
        history.Select(h => h.Checksum).Should().Equal(MigrationCatalog.Scripts.Select(s => s.Checksum));
        (await ScalarAsync<bool>(connectionString, "SELECT to_regnamespace('opportunity') IS NOT NULL")).Should().BeTrue();
        (await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM pg_roles WHERE rolname IN ('opportunity_app', 'opportunity_readonly') AND NOT rolcanlogin AND NOT rolbypassrls"))
            .Should().Be(2);
    }

    [Fact]
    public async Task Running_the_migrator_twice_is_a_no_op_that_exits_0()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        (await RunMigratorAppAsync(connectionString)).Should().Be(MigratorApp.ExitSuccess);
        var before = await ReadHistoryAsync(connectionString);

        (await RunMigratorAppAsync(connectionString)).Should().Be(MigratorApp.ExitSuccess);
        var result = await NewMigrator(connectionString).MigrateAsync(Ct);

        result.WasNoOp.Should().BeTrue();
        result.EndVersion.Should().Be(MigrationCatalog.LatestVersion);
        (await ReadHistoryAsync(connectionString)).Should().Equal(before);
    }

    [Fact]
    public async Task Previous_version_database_is_upgraded_with_only_the_new_scripts()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await NewMigrator(connectionString).MigrateAsync(Ct);
        var next = MigrationScript.Create(
            $"V{MigrationCatalog.LatestVersion + 1:D4}__add_sample.sql",
            "CREATE TABLE opportunity.sample (workspace_id uuid NOT NULL, id bigint NOT NULL, PRIMARY KEY (workspace_id, id));");

        var result = await NewMigrator(connectionString, [.. MigrationCatalog.Scripts, next]).MigrateAsync(Ct);

        result.StartVersion.Should().Be(MigrationCatalog.LatestVersion);
        result.Applied.Should().Equal(next);
        (await ScalarAsync<bool>(connectionString, "SELECT to_regclass('opportunity.sample') IS NOT NULL")).Should().BeTrue();
    }

    [Fact]
    public async Task Edited_applied_script_is_rejected_by_checksum()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await NewMigrator(connectionString).MigrateAsync(Ct);
        var original = MigrationCatalog.Scripts[0];
        var tampered = MigrationScript.Create(original.Name, original.Sql + "\nSELECT 1;\n");

        var act = () => NewMigrator(connectionString, [tampered, .. MigrationCatalog.Scripts.Skip(1)]).MigrateAsync(Ct);

        (await act.Should().ThrowAsync<MigrationChecksumMismatchException>())
            .WithMessage($"*{original.Name}*modified after it was applied*");
    }

    [Fact]
    public async Task Migrator_app_exits_non_zero_when_a_recorded_checksum_does_not_match()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        (await RunMigratorAppAsync(connectionString)).Should().Be(MigratorApp.ExitSuccess);
        await ExecuteAsync(connectionString, $"UPDATE {SchemaHistory.Table} SET checksum = 'tampered' WHERE version = 1");

        (await RunMigratorAppAsync(connectionString)).Should().Be(MigratorApp.ExitMigrationFailed);
    }

    [Fact]
    public async Task Concurrent_migrators_apply_each_script_exactly_once()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        // A slow script keeps the lock held long enough for the other runners to queue behind it.
        var slow = MigrationScript.Create(
            $"V{MigrationCatalog.LatestVersion + 1:D4}__slow.sql",
            "SELECT pg_sleep(1); CREATE TABLE opportunity.counter (workspace_id uuid PRIMARY KEY);");
        IReadOnlyList<MigrationScript> scripts = [.. MigrationCatalog.Scripts, slow];
        var options = new MigratorOptions { LockPollInterval = TimeSpan.FromMilliseconds(50) };

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => NewMigrator(connectionString, scripts, options).MigrateAsync(Ct), Ct)));

        results.Sum(r => r.Applied.Count).Should().Be(scripts.Count);
        results.Count(r => r.WasNoOp).Should().Be(3);
        results.Should().OnlyContain(r => r.EndVersion == slow.Version);
        (await ReadHistoryAsync(connectionString)).Select(h => h.Version).Should().Equal(scripts.Select(s => s.Version));
    }

    [Fact]
    public async Task Failing_transactional_script_is_rolled_back_and_not_recorded()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var broken = MigrationScript.Create(
            $"V{MigrationCatalog.LatestVersion + 1:D4}__broken.sql",
            "CREATE TABLE opportunity.half (workspace_id uuid PRIMARY KEY);\nSELECT 1 / 0;");

        var act = () => NewMigrator(connectionString, [.. MigrationCatalog.Scripts, broken]).MigrateAsync(Ct);

        await act.Should().ThrowAsync<MigrationException>().WithMessage("*broken*22012*");
        (await ScalarAsync<bool>(connectionString, "SELECT to_regclass('opportunity.half') IS NULL")).Should().BeTrue();
        (await ReadHistoryAsync(connectionString)).Select(h => h.Version).Should().Equal(MigrationCatalog.Scripts.Select(s => s.Version));
    }

    [Fact]
    public async Task Non_transactional_script_can_build_an_index_concurrently()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var table = MigrationScript.Create(
            $"V{MigrationCatalog.LatestVersion + 1:D4}__table.sql",
            "CREATE TABLE opportunity.doc (workspace_id uuid NOT NULL, id bigint NOT NULL, title text, PRIMARY KEY (workspace_id, id));");
        var index = MigrationScript.Create(
            $"V{MigrationCatalog.LatestVersion + 2:D4}__index.sql",
            $"""
            {MigrationScript.NoTransactionDirective}
            DROP INDEX CONCURRENTLY IF EXISTS opportunity.ix_doc_title;
            {MigrationScript.StatementBreakDirective}
            CREATE INDEX CONCURRENTLY ix_doc_title ON opportunity.doc (workspace_id, title);
            """);

        await NewMigrator(connectionString, [.. MigrationCatalog.Scripts, table, index]).MigrateAsync(Ct);

        index.IsTransactional.Should().BeFalse();
        (await ScalarAsync<bool>(connectionString,
            "SELECT indisvalid FROM pg_index WHERE indexrelid = 'opportunity.ix_doc_title'::regclass")).Should().BeTrue();
        (await ReadHistoryAsync(connectionString)).Last().Version.Should().Be(index.Version);
    }

    [Theory]
    [InlineData("CREATE TABLE opportunity.bad (id bigint PRIMARY KEY, workspace_id uuid NOT NULL);", "*opportunity.bad*leads with 'id'*")]
    [InlineData("CREATE TABLE opportunity.bad (workspace_id uuid NOT NULL);", "*opportunity.bad*no primary key*")]
    [InlineData("CREATE TABLE opportunity.bad (id bigint, workspace_id uuid, PRIMARY KEY (id, workspace_id)) PARTITION BY HASH (id);", "*opportunity.bad*")]
    public async Task Tenant_key_lint_rejects_tables_whose_primary_key_does_not_lead_with_workspace_id(string sql, string expected)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var script = MigrationScript.Create($"V{MigrationCatalog.LatestVersion + 1:D4}__bad.sql", sql);

        var act = () => NewMigrator(connectionString, [.. MigrationCatalog.Scripts, script]).MigrateAsync(Ct);

        await act.Should().ThrowAsync<MigrationException>().WithMessage(expected);
        (await ScalarAsync<bool>(connectionString, "SELECT to_regclass('opportunity.bad') IS NULL")).Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_key_lint_accepts_tenant_partitioned_and_global_tables()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var script = MigrationScript.Create(
            $"V{MigrationCatalog.LatestVersion + 1:D4}__good.sql",
            """
            CREATE TABLE opportunity.tenant (workspace_id uuid NOT NULL, id bigint NOT NULL, PRIMARY KEY (workspace_id, id));
            CREATE TABLE opportunity.part (workspace_id uuid NOT NULL, id bigint NOT NULL, PRIMARY KEY (workspace_id, id))
                PARTITION BY HASH (workspace_id);
            CREATE TABLE opportunity.part_0 PARTITION OF opportunity.part FOR VALUES WITH (MODULUS 2, REMAINDER 0);
            CREATE TABLE opportunity.part_1 PARTITION OF opportunity.part FOR VALUES WITH (MODULUS 2, REMAINDER 1);
            CREATE TABLE opportunity.installation_setting (key text PRIMARY KEY, value text);
            COMMENT ON TABLE opportunity.installation_setting IS '@global installation-wide settings';
            """);

        var result = await NewMigrator(connectionString, [.. MigrationCatalog.Scripts, script]).MigrateAsync(Ct);

        result.EndVersion.Should().Be(script.Version);
    }

    [Fact]
    public async Task Migrator_refuses_a_database_newer_than_its_scripts()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await NewMigrator(connectionString).MigrateAsync(Ct);
        await ExecuteAsync(connectionString,
            $"INSERT INTO {SchemaHistory.Table} (version, description, script_name, checksum, transactional, execution_ms) VALUES (9999, 'future', 'V9999__future.sql', 'x', true, 0)");

        var act = () => NewMigrator(connectionString).MigrateAsync(Ct);

        await act.Should().ThrowAsync<MigrationException>().WithMessage("*9999*newer than this build*");
    }

    [Fact]
    public async Task Migrator_refuses_a_pending_script_older_than_the_database_version()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var later = MigrationScript.Create($"V{MigrationCatalog.LatestVersion + 2:D4}__later.sql", "SELECT 1;");
        await NewMigrator(connectionString, [.. MigrationCatalog.Scripts, later]).MigrateAsync(Ct);
        var lateArrival = MigrationScript.Create($"V{MigrationCatalog.LatestVersion + 1:D4}__late_arrival.sql", "SELECT 1;");

        var act = () => NewMigrator(connectionString, [.. MigrationCatalog.Scripts, lateArrival, later]).MigrateAsync(Ct);

        await act.Should().ThrowAsync<MigrationException>()
            .WithMessage($"*{lateArrival.Name}*older than the database version {later.Version}*");
    }

    [Fact]
    public async Task Readiness_check_is_unhealthy_until_the_schema_reaches_the_expected_version()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var context = new HealthCheckContext();

        (await new SchemaVersionHealthCheck(dataSource).CheckHealthAsync(context, Ct)).Status.Should().Be(HealthStatus.Unhealthy);

        await NewMigrator(connectionString).MigrateAsync(Ct);

        (await new SchemaVersionHealthCheck(dataSource).CheckHealthAsync(context, Ct)).Status.Should().Be(HealthStatus.Healthy);
        (await new SchemaVersionHealthCheck(dataSource, MigrationCatalog.LatestVersion + 1).CheckHealthAsync(context, Ct))
            .Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task App_role_has_dml_but_no_ddl_and_can_read_the_schema_version()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await NewMigrator(connectionString).MigrateAsync(Ct);
        await ExecuteAsync(connectionString,
            "CREATE TABLE opportunity.probe (workspace_id uuid NOT NULL, id bigint NOT NULL, PRIMARY KEY (workspace_id, id))");
        var appConnectionString = await CreateLoginAsync(connectionString, "opportunity_app");
        var readonlyConnectionString = await CreateLoginAsync(connectionString, "opportunity_readonly");

        await ExecuteAsync(appConnectionString, "INSERT INTO opportunity.probe VALUES (gen_random_uuid(), 1)");
        (await ScalarAsync<long>(appConnectionString, "SELECT count(*) FROM opportunity.probe")).Should().Be(1);
        (await ScalarAsync<long>(readonlyConnectionString, "SELECT count(*) FROM opportunity.probe")).Should().Be(1);
        await using var appDataSource = NpgsqlDataSource.Create(appConnectionString);
        (await SchemaHistory.GetCurrentVersionAsync(appDataSource, Ct)).Should().Be(MigrationCatalog.LatestVersion);

        await AssertPermissionDenied(appConnectionString, "CREATE TABLE opportunity.sneaky (workspace_id uuid PRIMARY KEY)");
        await AssertPermissionDenied(appConnectionString, "CREATE TABLE public.sneaky (workspace_id uuid PRIMARY KEY)");
        await AssertPermissionDenied(appConnectionString, "TRUNCATE opportunity.probe");
        await AssertPermissionDenied(appConnectionString, $"DELETE FROM {SchemaHistory.Table}");
        await AssertPermissionDenied(readonlyConnectionString, "INSERT INTO opportunity.probe VALUES (gen_random_uuid(), 2)");
    }

    [Fact]
    public async Task Migrator_app_exits_with_configuration_error_without_a_connection_string()
    {
        (await MigratorApp.RunAsync([], cancellationToken: Ct)).Should().Be(MigratorApp.ExitConfigurationError);
    }

    [Fact]
    public void Script_parsing_normalizes_line_endings_and_detects_directives()
    {
        var lf = MigrationScript.Create("V0042__some_change.sql", "﻿SELECT 1;\nSELECT 2;\n");
        var crlf = MigrationScript.Create("V0042__some_change.sql", "SELECT 1;\r\nSELECT 2;\r\n");

        lf.Version.Should().Be(42);
        lf.Description.Should().Be("some change");
        lf.Checksum.Should().Be(crlf.Checksum);
        lf.IsTransactional.Should().BeTrue();
        MigrationScript.Create("V7__x.sql", $"{MigrationScript.NoTransactionDirective}\nSELECT 1;").IsTransactional.Should().BeFalse();
        FluentActions.Invoking(() => MigrationScript.Create("0001_bad.sql", "")).Should().Throw<MigrationException>();
        FluentActions.Invoking(() => MigrationCatalog.Order([lf, crlf])).Should().Throw<MigrationException>().WithMessage("*Duplicate*42*");
    }

    private static PostgresMigrator NewMigrator(
        string connectionString, IEnumerable<MigrationScript>? scripts = null, MigratorOptions? options = null) =>
        new(NpgsqlDataSource.Create(connectionString), scripts, options);

    private static Task<int> RunMigratorAppAsync(string connectionString) =>
        MigratorApp.RunAsync([$"--ConnectionStrings:{MigratorApp.ConnectionStringName}={connectionString}"], cancellationToken: Ct);

    private static async Task<string> CreateLoginAsync(string connectionString, string groupRole)
    {
        var login = "login_" + Guid.NewGuid().ToString("N")[..12];
        const string password = "test-only-password";
        await ExecuteAsync(connectionString, $"CREATE ROLE {login} LOGIN PASSWORD '{password}' IN ROLE {groupRole}");
        return new NpgsqlConnectionStringBuilder(connectionString) { Username = login, Password = password, Pooling = false }
            .ConnectionString;
    }

    private static async Task AssertPermissionDenied(string connectionString, string sql)
    {
        var act = () => ExecuteAsync(connectionString, sql);
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<HistoryRow>> ReadHistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT version, checksum, applied_at FROM {SchemaHistory.Table} ORDER BY version", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<HistoryRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return rows;
    }

    private sealed record HistoryRow(int Version, string Checksum, DateTimeOffset AppliedAt);
}
