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
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Snapshots;

/// <summary>E10-T02 through the real API host: create (201 / 202), read, list, permissions and workspace binding.</summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class SnapshotApiTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_returns_the_frozen_count_and_generation_and_reads_are_bound_to_creator_and_workspace()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Search.WorkspaceAsync();
        var other = await h.Search.WorkspaceAsync();
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(ws, WorkspaceRole.Reviewer);
        var manager = await h.MemberAsync(ws, WorkspaceRole.ProductionManager);
        await h.Search.Db.AssignAsync(other, WorkspaceRole.QcReviewer, qc);
        for (var i = 1; i <= 4; i++)
        {
            await h.DocumentAsync(ws, $"API-{i:000}", i == 1 ? "settlement agreement draft" : "settlement agreement");
        }

        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Search.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", openSearch.BaseAddress.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Search.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("Snapshots:SynchronousMaxDocuments", "3");
            builder.UseSetting("Snapshots:PollInterval", "00:00:00.200");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Search.Db.Reader);
                services.AddSingleton<IAuditEventWriter>(new InMemoryAuditEventWriter());
            });
        });
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{ws}/snapshots";

        using var created = await SendAsync(client, HttpMethod.Post, url, qc, """{"purpose":"BulkCoding","query":"settlement draft"}""", "key-1");
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;
        root.GetProperty("status").GetString().Should().Be("ready");
        root.GetProperty("documentCount").GetInt64().Should().Be(1);
        root.GetProperty("searchGeneration").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("rootSha256").GetString().Should().HaveLength(64);
        root.GetProperty("source").GetProperty("normalizedQuery").GetString().Should().Be("settlement AND draft");
        var id = root.GetProperty("snapshotId").GetGuid();
        created.Headers.Location!.ToString().Should().Be($"{url}/{id}");

        // Too large for the request: 202 with the snapshot's Location, then Ready from the background materializer.
        using var accepted = await SendAsync(client, HttpMethod.Post, url, qc, """{"purpose":"BulkCoding","query":"settlement"}""", "key-2");
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
        var location = accepted.Headers.Location!.ToString();
        JsonElement ready = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var poll = await SendAsync(client, HttpMethod.Get, location, qc);
            ready = JsonDocument.Parse(await poll.Content.ReadAsStringAsync(Ct)).RootElement;
            if (ready.GetProperty("status").GetString() == "ready")
            {
                break;
            }

            await Task.Delay(100, Ct);
        }

        ready.GetProperty("status").GetString().Should().Be("ready");
        ready.GetProperty("documentCount").GetInt64().Should().Be(4);

        // Reads: the creator and Job.ViewAll; the query text only for the creator; never from another workspace.
        await (await SendAsync(client, HttpMethod.Get, $"{url}/{id}", reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using (var asManager = await SendAsync(client, HttpMethod.Get, $"{url}/{id}", manager))
        {
            asManager.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await asManager.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetProperty("source").GetProperty("query").ValueKind.Should().Be(JsonValueKind.Null);
        }

        await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{other}/snapshots/{id}", qc)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        using (var list = await SendAsync(client, HttpMethod.Get, url + "?limit=1", qc))
        {
            using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
            json.RootElement.GetProperty("nextCursor").GetString().Should().NotBeNull();
        }

        // Permissions and validation.
        await (await SendAsync(client, HttpMethod.Post, url, reviewer, """{"purpose":"BulkCoding","query":"settlement"}""", "key-3"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, url, qc, $$"""{"purpose":"BulkCoding","query":"x","documentIds":["{{Guid.NewGuid()}}"]}""", "key-4"))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, url, qc, """{"purpose":"BulkCoding","query":"a OR ("}""", "key-5"))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Guid user, string? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }
}
