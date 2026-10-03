using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

using Opportunity.Hosting.Health;
using Opportunity.Hosting.Workers;
using Opportunity.Migrator;

namespace Opportunity.IntegrationTests.Migrations;

/// <summary>API and worker readiness follow the schema version once <c>ConnectionStrings:App</c> is configured.</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class HostSchemaReadinessTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_is_not_ready_until_the_migrator_has_run()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting($"ConnectionStrings:{PostgresReadiness.ConnectionStringName}", connectionString));
        using var client = factory.CreateClient();

        var before = await client.GetAsync("/health/ready", Ct);
        await MigrateAsync(connectionString);
        var after = await client.GetAsync("/health/ready", Ct);

        before.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CheckNamesAsync(after)).Should().Contain(PostgresReadiness.SchemaCheckName);
        (await client.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Worker_is_not_ready_until_the_migrator_has_run()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var builder = OpportunityWorkerHost.CreateBuilder(
            [$"--ConnectionStrings:{PostgresReadiness.ConnectionStringName}={connectionString}"], WorkerTypes.AllKeyword);
        builder.WebHost.UseTestServer();
        await using var app = OpportunityWorkerHost.Build(builder.AddWorkerModules());
        await app.StartAsync(Ct);
        try
        {
            using var client = app.GetTestClient();

            var before = await client.GetAsync("/health/ready", Ct);
            await MigrateAsync(connectionString);
            var after = await client.GetAsync("/health/ready", Ct);

            before.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            after.StatusCode.Should().Be(HttpStatusCode.OK);
            (await CheckNamesAsync(after)).Should().Contain(PostgresReadiness.SchemaCheckName);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Without_a_connection_string_no_schema_check_is_registered()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready", Ct);

        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CheckNamesAsync(ready)).Should().NotContain(PostgresReadiness.SchemaCheckName);
    }

    private static async Task MigrateAsync(string connectionString) =>
        (await MigratorApp.RunAsync([$"--ConnectionStrings:{MigratorApp.ConnectionStringName}={connectionString}"], cancellationToken: Ct))
            .Should().Be(MigratorApp.ExitSuccess);

    private static async Task<List<string?>> CheckNamesAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. body.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString())];
    }
}
