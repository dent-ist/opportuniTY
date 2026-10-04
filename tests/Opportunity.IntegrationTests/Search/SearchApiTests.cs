using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>E07-T05 through the real API host: the endpoints the query bar and document list call.</summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class SearchApiTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Search_and_page_through_the_api_with_bound_handles()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var other = await h.WorkspaceAsync();
        var alice = await h.MemberAsync(ws);
        var bob = await h.MemberAsync(ws);
        await h.Db.AssignAsync(other, Core.Security.WorkspaceRole.Reviewer, alice);
        for (var i = 1; i <= 3; i++)
        {
            await h.DocumentAsync(ws, $"API-{i:000}", "termination of the supply contract");
        }

        var audit = new InMemoryAuditEventWriter();
        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", openSearch.BaseAddress.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Db.Reader);
                services.AddSingleton<IAuditEventWriter>(audit);
            });
        });
        using var client = factory.CreateClient();

        using var first = await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", alice,
            """{"query":"contract termination","pageSize":2,"sort":[{"field":"controlNumber","direction":"asc"}]}""");
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        using var page1 = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
        var root = page1.RootElement;
        root.GetProperty("normalized").GetString().Should().Be("contract AND termination");
        root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("controlNumber").GetString()).Should().Equal("API-001", "API-002");
        root.GetProperty("items")[0].GetProperty("snippets")[0].GetProperty("highlights").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("items")[0].TryGetProperty("text", out _).Should().BeFalse("responses carry grid fields and snippets, never full text");
        root.GetProperty("total").GetProperty("relation").GetString().Should().Be("eq");
        root.GetProperty("freshness").GetProperty("servedGeneration").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("freshness").GetProperty("current").GetBoolean().Should().BeTrue("no search work is pending (Q-10)");
        var searchId = root.GetProperty("searchId").GetString()!;
        var cursor = root.GetProperty("nextCursor").GetString()!;
        audit.Events.Should().Contain(e => e.Action == "Executed" && e.RestrictedDetails!["query"] == "contract termination");

        var pageUrl = $"/api/v1/workspaces/{ws}/searches/{searchId}/pages?cursor={cursor}";
        using (var next = await SendAsync(client, HttpMethod.Get, pageUrl, alice))
        {
            next.StatusCode.Should().Be(HttpStatusCode.OK);
            using var page2 = JsonDocument.Parse(await next.Content.ReadAsStringAsync(Ct));
            page2.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("controlNumber").GetString()).Should().Equal("API-003");
            page2.RootElement.GetProperty("page").GetProperty("isLast").GetBoolean().Should().BeTrue();
        }

        await (await SendAsync(client, HttpMethod.Get, pageUrl, bob)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{other}/searches/{searchId}/pages?cursor={cursor}", alice))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{searchId}/pages?cursor={Guid.NewGuid():N}", alice))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{searchId}/pages", alice))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        using var invalid = await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", alice, """{"query":"workspaceId:x OR ("}""");
        var problem = await invalid.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
        problem.GetProperty("queryErrors")[0].GetProperty("span").GetProperty("start").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        using var unknownField = await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", alice, $$"""{"query":"workspaceId:{{other}}"}""");
        (await unknownField.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query"))
            .GetProperty("queryErrors")[0].GetProperty("code").GetString().Should().Be("UNKNOWN_FIELD");
        await (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", alice, """{"query":"a","pageSize":501}"""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Planner_limits_and_binding_errors_are_400_with_positioned_messages()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var alice = await h.MemberAsync(ws);
        await h.DocumentAsync(ws, "LIM-001", "anchor contract");

        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", openSearch.BaseAddress.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("Search:MaxQueryClauses", "3");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Db.Reader);
                services.AddSingleton<IAuditEventWriter>(new InMemoryAuditEventWriter());
            });
        });
        using var client = factory.CreateClient();

        foreach (var (query, code) in new[]
        {
            ("a OR b OR c OR d", "TOO_MANY_CLAUSES"),
            ("*tract", "LEADING_WILDCARD"),
            ("custodain:smith", "UNKNOWN_FIELD"),
            ("(aaa* OR bbb* OR ccc* OR ddd* OR eee* OR fff* OR ggg* OR hhh* OR iii*) W/3 x", "TOO_MANY_WILDCARDS"),
        })
        {
            using var response = await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", alice,
                JsonSerializer.Serialize(new { query }));
            var errors = (await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query")).GetProperty("queryErrors");
            var error = errors.EnumerateArray().Should().Contain(e => e.GetProperty("code").GetString() == code, query).Subject;
            error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
            error.GetProperty("span").GetProperty("end").GetInt32().Should().BeGreaterThan(0);
        }

        // The query bar's validate endpoint binds against the workspace too.
        using var validation = await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/query-validations", alice,
            """{"query":"contract AND filetyp:email"}""");
        validation.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await validation.Content.ReadAsStringAsync(Ct));
        result.RootElement.GetProperty("valid").GetBoolean().Should().BeFalse();
        var unknown = result.RootElement.GetProperty("errors")[0];
        unknown.GetProperty("code").GetString().Should().Be("UNKNOWN_FIELD");
        unknown.GetProperty("span").GetProperty("start").GetInt32().Should().Be(13);
        unknown.GetProperty("expected").EnumerateArray().Select(e => e.GetString()).Should().Contain("filetype");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Guid user, string? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }
}
