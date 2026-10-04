using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Opportunity.Application.Search;

namespace Opportunity.IntegrationTests.Api;

/// <summary>The validate endpoint the query bar uses (E07-T06): AST or positioned errors; raw DSL is never accepted.</summary>
public sealed class QueryValidationEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Path = "/api/v1/workspaces/0199a8a0-0000-7000-8000-0000000000a1/query-validations";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_query_returns_the_ast_and_the_normalized_interpretation()
    {
        var body = await ValidateAsync("contract termination");

        body.GetProperty("valid").GetBoolean().Should().BeTrue();
        body.GetProperty("astVersion").GetInt32().Should().Be(1);
        body.GetProperty("normalized").GetString().Should().Be("contract AND termination");
        var root = body.GetProperty("ast").GetProperty("root");
        root.GetProperty("kind").GetString().Should().Be("and");
        root.GetProperty("children")[1].GetProperty("span").GetProperty("start").GetInt32().Should().Be(9);
        body.GetProperty("errors").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Malformed_query_returns_200_with_a_positioned_error_and_expected_tokens()
    {
        var body = await ValidateAsync("contract AND");

        body.GetProperty("valid").GetBoolean().Should().BeFalse();
        body.GetProperty("ast").ValueKind.Should().Be(JsonValueKind.Null);
        var error = body.GetProperty("errors")[0];
        error.GetProperty("code").GetString().Should().Be("SYNTAX_ERROR");
        error.GetProperty("span").GetProperty("start").GetInt32().Should().Be(12);
        error.GetProperty("span").GetProperty("end").GetInt32().Should().Be(12);
        error.GetProperty("expected").EnumerateArray().Select(e => e.GetString()).Should().Contain("term");
    }

    [Fact]
    public async Task Lower_case_operator_is_valid_with_a_warning()
    {
        var body = await ValidateAsync("contract and termination");

        body.GetProperty("valid").GetBoolean().Should().BeTrue();
        body.GetProperty("warnings")[0].GetProperty("code").GetString().Should().Be("LOWERCASE_OPERATOR");
    }

    [Fact]
    public async Task Dsl_text_is_a_parse_error_not_a_query()
    {
        var body = await ValidateAsync("{\"query\":{\"match_all\":{}}}");

        body.GetProperty("valid").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Dsl_object_instead_of_query_text_is_rejected()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("""{"query":{"match_all":{}}}""", Encoding.UTF8, "application/json");

        var response = await client.PostAsync(Path, content, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad-request");
    }

    [Fact]
    public async Task Missing_query_is_a_validation_problem()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path, new { }, Ct);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").TryGetProperty("query", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Too_long_query_reports_the_limit()
    {
        var body = await ValidateAsync(new string('a', 10_001));

        body.GetProperty("errors")[0].GetProperty("code").GetString().Should().Be("QUERY_TOO_LONG");
    }

    /// <summary>
    /// Parse-level behaviour: this host has no catalogue database or OpenSearch, so the planner's binder is removed
    /// (binding errors through the API are covered by <c>SearchApiTests</c>).
    /// </summary>
    private async Task<JsonElement> ValidateAsync(string query)
    {
        using var parseOnly = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IQueryBinder>()));
        using var client = parseOnly.CreateClient();
        var response = await client.PostAsJsonAsync(Path, new { query }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }
}
