using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Testing.Postgres;

namespace Opportunity.IntegrationTests.Telemetry;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresTelemetryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Npgsql_spans_are_children_of_the_request_and_carry_no_sql_text_or_parameters()
    {
        await using var database = await postgres.CreateDatabaseAsync(Ct);
        await using var factory = new TelemetryApiFactory(database.ConnectionString);

        var (server, npgsql, trace) = await RunSqlProbeAsync(factory);

        npgsql.Should().HaveCount(2).And.OnlyContain(s => s.ParentSpanId == server.SpanId);
        npgsql.Select(s => s.DisplayName).Should().Contain(["postgresql", $"CONNECT {database.Name}"]);
        npgsql.Should().OnlyContain(s =>
            (string?)s.GetTagItem("db.system.name") == "postgresql" && (string?)s.GetTagItem("db.namespace") == database.Name);
        npgsql.SelectMany(s => s.TagObjects).Select(t => t.Key)
            .Should().NotContain(["db.query.text", "db.statement", "db.npgsql.data_source"]);
        ApiTelemetryTests.AssertNoSensitiveText(factory.Capture.TextOf(trace));
        string.Join("\n", factory.Capture.TextOf(trace)).Should().NotContain("Password").And.NotContain("SELECT");
    }

    [Fact]
    public async Task Sql_text_is_exported_only_when_explicitly_enabled_and_never_with_parameter_values()
    {
        await using var database = await postgres.CreateDatabaseAsync(Ct);
        await using var factory = new TelemetryApiFactory(database.ConnectionString, recordSqlStatements: true);

        var (_, npgsql, trace) = await RunSqlProbeAsync(factory);

        npgsql.Select(s => s.GetTagItem("db.query.text") as string).Should().Contain(text =>
            text != null && text.Contains(TelemetryProbeEndpoints.SqlLiteral));
        string.Join("\n", factory.Capture.TextOf(trace)).Should().NotContain("privileged-merger-term", "parameter values are never recorded");
    }

    private static async Task<(Activity Server, List<Activity> Npgsql, ActivityTraceId Trace)> RunSqlProbeAsync(TelemetryApiFactory factory)
    {
        using var client = factory.CreateClient();
        var trace = ActivityTraceId.CreateRandom();

        var response = await ApiTelemetryTests.SendAsync(
            client, $"/api/v1/workspaces/{Guid.NewGuid()}/telemetry-sql?q=privileged-merger-term", trace, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var server = await factory.Capture.WaitForSpanAsync(s => s.TraceId == trace && s.Kind == ActivityKind.Server, Ct);
        return (server, [.. factory.Capture.SpansOf(trace).Where(s => s.Source.Name == "Npgsql")], trace);
    }
}
