using System.Net;
using AwesomeAssertions;

namespace Opportunity.IntegrationTests.Api;

public sealed class ProblemDetailsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/v1/workspaces/ws-1/no-such-resource")]
    [InlineData("/api/v1/workspaces/ws-1/documents/doc-1")]
    [InlineData("/api/v1/no-such-resource")]
    [InlineData("/api/v2/workspaces/ws-1/test-items")]
    public async Task Unknown_routes_return_404_problem(string path)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Wrong_method_returns_405_problem()
    {
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/v1/workspaces/ws-1/test-items", Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.MethodNotAllowed, "method-not-allowed");
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_problem_without_internals()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/workspaces/ws-1/test-throws", Ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.InternalServerError, "internal-error");
        var text = problem.GetRawText();
        text.Should().NotContain("secret_table").And.NotContain("InvalidOperationException").And.NotContain("Opportunity.Secret");
    }

    [Fact]
    public async Task Validation_problem_lists_field_errors()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/workspaces/ws-1/test-items?limit=501", Ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").GetProperty("limit").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Unreadable_body_returns_400_problem()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/workspaces/ws-1/test-jobs")
        {
            Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad-request");
    }
}
