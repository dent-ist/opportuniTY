using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Opportunity.Api.Conventions;
using Opportunity.Application.Telemetry;
using Opportunity.Hosting.Health;
using Opportunity.IntegrationTests.Api;

namespace Opportunity.IntegrationTests.Telemetry;

/// <summary>
/// The real API host with telemetry switched on and in-memory exporters behind the production pipeline
/// (scrubbing processor included). Optionally points <c>ConnectionStrings:App</c> at a test database.
/// </summary>
public sealed class TelemetryApiFactory(string? connectionString = null, bool recordSqlStatements = false)
    : WebApplicationFactory<Program>
{
    public TelemetryCapture Capture { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Telemetry:Enabled", "true");
        builder.UseSetting("Telemetry:RecordSqlStatements", recordSqlStatements ? "true" : "false");
        if (connectionString is not null)
        {
            builder.UseSetting($"ConnectionStrings:{PostgresReadiness.ConnectionStringName}", connectionString);
        }

        builder.UsePlaceholderAuthenticationSettings();
        builder.ConfigureTestServices(services =>
        {
            services.AddTestUserAuthentication();
            services.AddSingleton<IApiEndpointModule, TelemetryProbeEndpoints>();
            Capture.AddTo(services);
        });
    }
}

internal sealed class TelemetryProbeEndpoints : IApiEndpointModule
{
    public const string SqlLiteral = "sql-needle-literal";

    public void MapEndpoints(ApiRouteGroups routes)
    {
        routes.MemberOnly().MapGet("/telemetry-probe", (string? q, ILogger<TelemetryProbeEndpoints> logger) =>
        {
            TestLog.ProbeReturned(logger, 3);
            return TypedResults.Ok();
        });

        routes.MemberOnly().MapGet("/telemetry-sql", async (string? q, NpgsqlDataSource dataSource) =>
        {
            await using var command = dataSource.CreateCommand($"SELECT '{SqlLiteral}' WHERE $1 IS NOT NULL");
            command.Parameters.AddWithValue(q ?? string.Empty);
            await command.ExecuteScalarAsync();
            return TypedResults.Ok();
        });
    }
}

public sealed class ApiTelemetryTests : IAsyncLifetime
{
    // Search text, credentials and cookies a client sends; none of it may reach telemetry.
    private const string SearchText = "privileged-merger-term";
    private const string BearerToken = "secret-bearer-token";
    private const string CookieValue = "secret-session-cookie";

    private readonly TelemetryApiFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Api_request_produces_a_server_span_with_expected_attributes_continuing_the_callers_trace()
    {
        var workspaceId = Guid.NewGuid();
        var callerTrace = ActivityTraceId.CreateRandom();
        var callerSpan = ActivitySpanId.CreateRandom();

        var response = await SendProbeAsync(workspaceId, callerTrace, callerSpan, "corr-telemetry-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var span = await _factory.Capture.WaitForSpanAsync(s => s.TraceId == callerTrace && s.Kind == ActivityKind.Server, Ct);
        span.ParentSpanId.Should().Be(callerSpan);
        span.GetTagItem("http.request.method").Should().Be("GET");
        span.GetTagItem("http.route").Should().Be("/api/v1/workspaces/{workspaceId}/telemetry-probe");
        span.GetTagItem("http.response.status_code").Should().Be(200);
        span.GetTagItem("url.path").Should().Be($"/api/v1/workspaces/{workspaceId}/telemetry-probe");
        span.GetTagItem(TelemetryAttributes.WorkspaceId).Should().Be(workspaceId.ToString());
        span.GetTagItem(TelemetryAttributes.CorrelationId).Should().Be("corr-telemetry-1");
    }

    [Fact]
    public async Task Api_spans_carry_no_query_string_headers_or_client_identity()
    {
        var trace = ActivityTraceId.CreateRandom();

        await SendProbeAsync(Guid.NewGuid(), trace, ActivitySpanId.CreateRandom(), "corr-telemetry-2");

        var span = await _factory.Capture.WaitForSpanAsync(s => s.TraceId == trace && s.Kind == ActivityKind.Server, Ct);
        span.TagObjects.Select(t => t.Key).Should().NotContain(
            ["url.query", "client.address", "user_agent.original", "http.request.header.authorization", "http.request.header.cookie"]);
        span.TagObjects.Select(t => t.Key).Should().NotContain(k => k.StartsWith("http.request.header.") || k.StartsWith("network.peer."));
        AssertNoSensitiveText(_factory.Capture.TextOf(trace));
    }

    [Fact]
    public async Task Logs_carry_trace_id_correlation_and_workspace_and_no_query_text()
    {
        var workspaceId = Guid.NewGuid();
        var trace = ActivityTraceId.CreateRandom();

        await SendProbeAsync(workspaceId, trace, ActivitySpanId.CreateRandom(), "corr-telemetry-3");

        await _factory.Capture.WaitForSpanAsync(s => s.TraceId == trace && s.Kind == ActivityKind.Server, Ct);
        var log = _factory.Capture.Logs.Should().ContainSingle(l => l.TraceId == trace && l.Message.StartsWith("Probe returned")).Subject;
        log.Scope(LogScopeKeys.CorrelationId).Should().Be("corr-telemetry-3");
        log.Scope(LogScopeKeys.WorkspaceId).Should().Be(workspaceId.ToString());
        var everything = _factory.Capture.Logs.Where(l => l.TraceId == trace).SelectMany(l => l.Text()).ToList();
        AssertNoSensitiveText(everything);
    }

    [Fact]
    public async Task Resource_identifies_the_service_and_http_metrics_are_exported()
    {
        await SendProbeAsync(Guid.NewGuid(), ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), "corr-telemetry-4");

        var resource = _factory.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
        resource["service.name"].Should().Be("opportunity-api");
        resource["service.namespace"].Should().Be("opportunity");
        resource.Should().ContainKeys("service.version", "service.instance.id", "deployment.environment.name");

        _factory.Services.GetRequiredService<MeterProvider>().ForceFlush().Should().BeTrue();
        _factory.Capture.Metrics.Snapshot().Select(m => m.Name).Should().Contain("http.server.request.duration");
    }

    [Fact]
    public async Task Health_probes_are_not_traced()
    {
        using var client = _factory.CreateClient();
        var trace = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");

        (await client.SendAsync(request, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.Delay(200, Ct);

        _factory.Capture.SpansOf(trace).Should().BeEmpty();
    }

    [Fact]
    public async Task Telemetry_is_off_by_default()
    {
        await using var plain = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UsePlaceholderAuthenticationSettings());
        using var client = plain.CreateClient();
        (await client.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        plain.Services.GetService<TracerProvider>().Should().BeNull();
        plain.Services.GetService<MeterProvider>().Should().BeNull();
        plain.Services.GetRequiredService<OpportunityMetrics>().Should().NotBeNull("instruments stay injectable as no-ops");
    }

    internal static void AssertNoSensitiveText(IEnumerable<string> text)
    {
        var all = string.Join("\n", text);
        all.Should().NotContain(SearchText).And.NotContain(BearerToken).And.NotContain(CookieValue)
            .And.NotContain(TelemetryProbeEndpoints.SqlLiteral);
    }

    private async Task<HttpResponseMessage> SendProbeAsync(
        Guid workspaceId, ActivityTraceId trace, ActivitySpanId parent, string correlationId, string endpoint = "telemetry-probe")
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/workspaces/{workspaceId}/{endpoint}?q={SearchText}&limit=10");
        request.Headers.Add("traceparent", $"00-{trace}-{parent}-01");
        request.Headers.Add("X-Correlation-Id", correlationId);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {BearerToken}");
        request.Headers.TryAddWithoutValidation("Cookie", $"session={CookieValue}");
        request.Headers.TryAddWithoutValidation("baggage", $"search={SearchText}");
        return await client.SendAsync(request, Ct);
    }

    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string path, ActivityTraceId trace, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        return await client.SendAsync(request, cancellationToken);
    }
}
