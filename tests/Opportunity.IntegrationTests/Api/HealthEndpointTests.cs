using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;

namespace Opportunity.IntegrationTests.Api;

public sealed class HealthEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Live_and_ready_are_ok_when_dependencies_are_up()
    {
        using var client = _factory.CreateClient();

        var live = await client.GetAsync("/health/live", Ct);
        var ready = await client.GetAsync("/health/ready", Ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("status").GetString().Should().Be("healthy");
        body.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).Should().Contain("fake-dependency");
    }

    [Fact]
    public async Task Dependency_down_fails_ready_but_not_live_and_does_not_leak_details()
    {
        using var client = _factory.CreateClient();
        _factory.Dependency.State = DependencyState.Down;

        var live = await client.GetAsync("/health/live", Ct);
        var ready = await client.GetAsync("/health/ready", Ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var text = await ready.Content.ReadAsStringAsync(Ct);
        text.Should().Contain("\"unhealthy\"").And.NotContain("secret-host");
    }

    [Fact]
    public async Task Hung_dependency_fails_ready_within_the_check_timeout()
    {
        using var client = _factory.CreateClient();
        _factory.Dependency.State = DependencyState.Hung;

        var stopwatch = Stopwatch.StartNew();
        var ready = await client.GetAsync("/health/ready", Ct);
        stopwatch.Stop();

        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        (await client.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
