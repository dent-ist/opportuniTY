using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Bootstrap;
using Opportunity.Migrator;

namespace Opportunity.IntegrationTests.Migrations;

/// <summary>
/// The migrator registers the OpenSearch template and object-storage bootstrap steps only when configured, rejects bad
/// settings with exit code 2 and maps a failing step to exit code 3 (E04-T01 / #39, E07-T01 / #63).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class MigratorBootstrapWiringTests(MigrationPostgresFixture postgres)
{
    // Never reached: the configuration error stops the run before the database is touched.
    private const string UnreachableDatabase = "--ConnectionStrings:Migrator=Host=127.0.0.1;Port=1;Database=none;Username=none";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string[], string[]> Configurations => new()
    {
        { [], [] },
        { ["--ConnectionStrings:OpenSearch=http://localhost:9200"], ["IndexTemplateBootstrapStep"] },
        { ["--ObjectStorage:Provider=S3", "--ObjectStorage:S3:ServiceUrl=http://localhost:8333", "--ObjectStorage:S3:Bucket=opportunity"],
            ["ObjectStoreBootstrapStep"] },
        { ["--ConnectionStrings:OpenSearch=http://localhost:9200", "--ObjectStorage:Provider=FileSystem", "--ObjectStorage:FileSystem:RootPath=/tmp/x"],
            ["IndexTemplateBootstrapStep", "ObjectStoreBootstrapStep"] },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task Bootstrap_steps_are_registered_only_for_configured_dependencies(string[] args, string[] expected)
    {
        var steps = new List<string>();

        // No migrator connection string: the run stops with a configuration error before touching any dependency.
        var exit = await MigratorApp.RunAsync(
            args,
            services => steps.AddRange(services
                .Where(d => d.ServiceType == typeof(IInfrastructureBootstrapStep))
                .Select(d => (d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)!.Name)),
            Ct);

        exit.Should().Be(MigratorApp.ExitConfigurationError);
        steps.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("--OpenSearch:IndexPrefix=Not-Lowercase")]
    [InlineData("--OpenSearch:Placement:DedicatedBytes=60000000000")]
    public async Task Invalid_opensearch_settings_are_a_configuration_error(string setting)
    {
        (await MigratorApp.RunAsync([UnreachableDatabase, "--ConnectionStrings:OpenSearch=http://localhost:9200", setting], cancellationToken: Ct))
            .Should().Be(MigratorApp.ExitConfigurationError);
    }

    [Fact]
    public async Task Incomplete_object_storage_settings_are_a_configuration_error()
    {
        (await MigratorApp.RunAsync([UnreachableDatabase, "--ObjectStorage:Provider=S3"], cancellationToken: Ct))
            .Should().Be(MigratorApp.ExitConfigurationError);
    }

    [Fact]
    public async Task An_unreachable_opensearch_fails_the_bootstrap_with_exit_code_3_after_migrating()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var exit = await MigratorApp.RunAsync(
            [
                $"--ConnectionStrings:{MigratorApp.ConnectionStringName}={connectionString}",
                "--ConnectionStrings:OpenSearch=http://127.0.0.1:1",
                "--OpenSearch:RequestTimeout=00:00:05",
            ],
            cancellationToken: Ct);

        exit.Should().Be(MigratorApp.ExitBootstrapFailed);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT to_regclass('opportunity.workspace_index_placement') IS NOT NULL", connection);
        ((bool)(await command.ExecuteScalarAsync(Ct))!).Should().BeTrue("migrations run before any bootstrap step");
        NpgsqlConnection.ClearPool(connection);
    }
}
