using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace Opportunity.IntegrationTests.Api;

public sealed class ApiConventionsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Json_uses_camelCase_string_enums_and_utc_millisecond_timestamps()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/workspaces/ws-1/test-jobs")
        {
            Content = JsonContent.Create(new { name = "x" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request, Ct);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var job = body.RootElement;
        job.GetProperty("workspaceId").GetString().Should().Be("ws-1");
        job.GetProperty("status").GetString().Should().Be("queued");
        job.GetProperty("createdAt").GetString().Should().Be("2026-10-02T14:03:22.123Z");
    }

    [Fact]
    public async Task Collections_use_the_cursor_page_shape()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/workspaces/ws-1/test-items?limit=2", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var page = body.RootElement;
        page.GetProperty("items").GetArrayLength().Should().Be(2);
        page.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        page.GetProperty("total").GetProperty("value").GetInt64().Should().Be(10_000);
        page.GetProperty("total").GetProperty("relation").GetString().Should().Be("gte");
    }

    [Fact]
    public async Task Versioned_mutation_requires_matching_if_match()
    {
        using var client = factory.CreateClient();
        const string path = "/api/v1/workspaces/ws-1/test-versioned";

        var missing = await client.PutAsync(path, content: null, Ct);
        await missing.ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");

        using var stale = new HttpRequestMessage(HttpMethod.Put, path);
        stale.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        await (await client.SendAsync(stale, Ct)).ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");

        using var current = new HttpRequestMessage(HttpMethod.Put, path);
        current.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        var ok = await client.SendAsync(current, Ct);
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ok.Headers.ETag!.Tag.Should().Be("\"4\"");
    }

    [Fact]
    public async Task Correlation_id_is_echoed()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workspaces/ws-1/test-items");
        request.Headers.Add("X-Correlation-Id", "client-abc-123");

        var response = await client.SendAsync(request, Ct);

        response.Headers.GetValues("X-Correlation-Id").Should().Equal("client-abc-123");
    }

    [Fact]
    public async Task OpenApi_document_is_3_1_and_covers_only_v1_routes()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var document = body.RootElement;
        document.GetProperty("openapi").GetString().Should().StartWith("3.1");

        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.Should().Contain("/api/v1/workspaces/{workspaceId}/test-jobs");
        paths.Should().OnlyContain(p => p.StartsWith("/api/v1/", StringComparison.Ordinal));

        var parameters = document.GetProperty("paths").GetProperty("/api/v1/workspaces/{workspaceId}/test-jobs")
            .GetProperty("post").GetProperty("parameters").EnumerateArray();
        parameters.Should().Contain(p =>
            p.GetProperty("name").GetString() == "Idempotency-Key"
            && p.GetProperty("in").GetString() == "header"
            && p.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void Invalid_configuration_fails_at_start_up()
    {
        using var misconfigured = factory.WithWebHostBuilder(b => b.UseSetting("Idempotency:Retention", "01:00:00"));

        var start = () => misconfigured.CreateClient();

        start.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Retention");
    }
}
