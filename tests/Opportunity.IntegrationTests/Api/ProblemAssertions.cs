using System.Net;
using System.Text.Json;
using AwesomeAssertions;

namespace Opportunity.IntegrationTests.Api;

internal static class ProblemAssertions
{
    /// <summary>Asserts the ADR-019 §2.4 problem shape and returns the parsed body for further checks.</summary>
    public static async Task<JsonElement> ShouldBeProblemAsync(
        this HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        var problem = document.RootElement.Clone();

        problem.GetProperty("type").GetString().Should().Be($"urn:opportunity:problem:{code}");
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("status").GetInt32().Should().Be((int)status);
        problem.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        problem.GetProperty("traceId").GetString().Should().MatchRegex("^[0-9a-f]{32}$");
        problem.TryGetProperty("exception", out _).Should().BeFalse();
        return problem;
    }
}
