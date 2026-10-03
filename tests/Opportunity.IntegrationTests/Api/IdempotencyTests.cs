using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;

namespace Opportunity.IntegrationTests.Api;

public sealed class IdempotencyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage CreateJob(string workspaceId, string? key, string name = "first")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/workspaces/{workspaceId}/test-jobs")
        {
            Content = JsonContent.Create(new { name }),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    [Fact]
    public async Task Job_creation_returns_202_with_job_location()
    {
        using var client = factory.CreateClient();
        using var request = CreateJob("ws-1", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var job = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var jobId = job.RootElement.GetProperty("id").GetString();
        response.Headers.Location!.OriginalString.Should().Be($"/api/v1/workspaces/ws-1/jobs/{jobId}");
    }

    [Fact]
    public async Task Replay_with_same_key_and_body_returns_the_original_job()
    {
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        using var firstRequest = CreateJob("ws-1", key);
        var first = await client.SendAsync(firstRequest, Ct);
        var createdAfterFirst = factory.Jobs.Created;
        using var secondRequest = CreateJob("ws-1", key);
        var second = await client.SendAsync(secondRequest, Ct);

        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.Headers.Location.Should().Be(first.Headers.Location);
        (await second.Content.ReadAsStringAsync(Ct)).Should().Be(await first.Content.ReadAsStringAsync(Ct));
        second.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        factory.Jobs.Created.Should().Be(createdAfterFirst, "the replay must not create a second job");
    }

    [Fact]
    public async Task Same_key_with_different_body_returns_422()
    {
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        using var firstRequest = CreateJob("ws-1", key, "first");
        (await client.SendAsync(firstRequest, Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var secondRequest = CreateJob("ws-1", key, "different");
        var second = await client.SendAsync(secondRequest, Ct);

        await second.ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency-key-reuse");
    }

    [Fact]
    public async Task Keys_are_scoped_per_workspace()
    {
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        using var firstRequest = CreateJob("ws-1", key);
        var first = await client.SendAsync(firstRequest, Ct);
        using var secondRequest = CreateJob("ws-2", key);
        var second = await client.SendAsync(secondRequest, Ct);

        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
        second.Headers.Location.Should().NotBe(first.Headers.Location);
    }

    [Fact]
    public async Task Missing_key_returns_400()
    {
        using var client = factory.CreateClient();
        using var request = CreateJob("ws-1", key: null);

        var response = await client.SendAsync(request, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "idempotency-key-missing");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("non-ascii-\u00e9")]
    [InlineData("kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk")]
    public async Task Invalid_key_returns_400(string key)
    {
        using var client = factory.CreateClient();
        using var request = CreateJob("ws-1", key);

        var response = await client.SendAsync(request, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "idempotency-key-invalid");
    }

    [Fact]
    public async Task Server_error_is_not_recorded_so_the_same_key_can_be_retried()
    {
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        factory.Jobs.FailNext = true;

        using var firstRequest = CreateJob("ws-1", key);
        var first = await client.SendAsync(firstRequest, Ct);
        using var retryRequest = CreateJob("ws-1", key);
        var retry = await client.SendAsync(retryRequest, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retry.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
    }

    [Fact]
    public async Task Client_error_is_not_recorded_so_a_corrected_retry_is_not_rejected()
    {
        using var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        using var first = InvalidJson(key);
        (await client.SendAsync(first, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var corrected = CreateJob("ws-1", key);
        var retry = await client.SendAsync(corrected, Ct);

        retry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retry.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
    }

    private static HttpRequestMessage InvalidJson(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/workspaces/ws-1/test-jobs")
        {
            Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
}
