using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.Data.Search;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// #186: the query bar's per-user, per-workspace history through the real API host and the PostgreSQL PDP: last 50
/// distinct valid queries, newest first, kept server-side (so a new session sees them), invisible to other users,
/// other workspaces, non-members and the read-only database role (Q-16).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class QueryHistoryApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_keeps_the_last_50_distinct_valid_queries_newest_first_per_user_and_workspace()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var other = await db.Core.CreateWorkspaceAsync();
        var alice = await db.CreateUserAsync();
        var bob = await db.CreateUserAsync();
        var outsider = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, alice);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, bob);
        await db.AssignAsync(other, WorkspaceRole.Reviewer, alice);
        var url = $"/api/v1/workspaces/{ws}/query-history";

        await using (var factory = Factory(db))
        {
            using var client = factory.CreateClient();
            for (var i = 1; i <= 55; i++)
            {
                (await RecordAsync(client, url, alice, $"term{i}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            }

            (await RecordAsync(client, url, alice, "  term30 \n")).StatusCode.Should().Be(HttpStatusCode.NoContent, "re-running moves it to the top");
            (await RecordAsync(client, url, bob, "privileged AND counsel")).StatusCode.Should().Be(HttpStatusCode.NoContent);

            var invalid = await (await RecordAsync(client, url, alice, "contract AND")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
            invalid.GetProperty("queryErrors")[0].GetProperty("code").GetString().Should().Be("SYNTAX_ERROR");
            await (await RecordAsync(client, url, alice, "   ")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
            await (await SendAsync(client, HttpMethod.Post, url, alice, """{"query":{"match_all":{}}}""")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad-request");
        }

        // A new host and client: nothing lives in the API process or the browser, so a later session sees the same history.
        await using (var factory = Factory(db))
        {
            using var client = factory.CreateClient();
            var entries = await ListAsync(client, url, alice);
            entries.Select(e => e.Query).Should().HaveCount(50);
            entries.Select(e => e.Query).Should().StartWith(["term30", "term55", "term54"]);
            entries.Select(e => e.Query).Should().OnlyHaveUniqueItems().And.NotContain(["term1", "term5", "contract AND"]);
            entries.Select(e => e.Query).Should().Contain("term7", "the oldest kept entry after trimming to 50");
            entries.Select(e => e.RanAt).Should().BeInDescendingOrder();

            (await ListAsync(client, url, bob)).Select(e => e.Query).Should().Equal("privileged AND counsel");
            (await ListAsync(client, $"/api/v1/workspaces/{other}/query-history", alice)).Should().BeEmpty();

            await (await SendAsync(client, HttpMethod.Get, url, outsider)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
            await (await RecordAsync(client, url, outsider, "planted")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        }

        (await db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.query_history WHERE query_text = 'planted'")).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_records_never_leave_more_than_50_entries_and_the_read_only_role_cannot_read_query_text()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var user = Guid.CreateVersion7();
        var store = new QueryHistoryStore(db.AppDataSource);
        var start = DateTimeOffset.UtcNow;

        await Parallel.ForEachAsync(Enumerable.Range(1, 80), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct }, async (i, ct) =>
            await store.RecordAsync(ws, user, $"q{i}", start.AddMilliseconds(i), ct));

        var entries = await store.ListAsync(ws, user, Ct);
        entries.Should().HaveCount(50);
        entries[0].QueryText.Should().Be("q80");
        (await db.ScalarAsync<long>("SELECT count(*) FROM opportunity.query_history")).Should().Be(50);

        var readOnly = await CoreSchemaDatabase.CreateLoginAsync(db.ConnectionString, "opportunity_readonly");
        await using var connection = new NpgsqlConnection(readOnly);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT query_text FROM opportunity.query_history", connection);
        var act = async () => await command.ExecuteScalarAsync(Ct);
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(db.Reader);
                services.AddSingleton<IAuditEventWriter>(new InMemoryAuditEventWriter());
            });
        });

    private static Task<HttpResponseMessage> RecordAsync(HttpClient client, string url, Guid user, string query) =>
        SendAsync(client, HttpMethod.Post, url, user, JsonSerializer.Serialize(new { query }));

    private static async Task<List<(string Query, DateTimeOffset RanAt)>> ListAsync(HttpClient client, string url, Guid user)
    {
        using var response = await SendAsync(client, HttpMethod.Get, url, user);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        return [.. body.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => (e.GetProperty("query").GetString()!, e.GetProperty("ranAt").GetDateTimeOffset()))];
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
