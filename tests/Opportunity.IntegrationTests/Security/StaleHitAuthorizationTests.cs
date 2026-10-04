using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.Storage;
using Opportunity.Storage.FileSystem;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Security;

/// <summary>
/// E05-T05 stale-projection scenario (baseline §24, Q-11, Q-12, Q-13), in a shared-index and a dedicated-index
/// placement: after a search, one hit is coded Attorneys' Eyes Only through the coding API and another is walled, while
/// no index worker runs (the projection still shows both unrestricted). Through the real API host: the next page and a
/// new search omit both (Q-12 post-filter), the viewer, review-mode prefetch, print, native download and coding answer
/// 404 exactly like a missing document in the same request after the commit, and a frozen set selected from the stale
/// index excludes them.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class StaleHitAuthorizationTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hits_that_became_restricted_or_walled_after_the_search_are_dropped_and_their_content_is_404_with_the_index_stale(bool dedicated)
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var root = Path.Combine(Path.GetTempPath(), "opp-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ws = await h.WorkspaceAsync(dedicated);
            var fields = await CodingApiHarness.WorkspaceAsync(h.Db.Core, ws);
            var reviewer = await h.MemberAsync(ws, WorkspaceRole.Reviewer);
            var privilege = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer);
            var manager = await h.MemberAsync(ws, WorkspaceRole.ProductionManager);
            var content = ContentDatabase.Over(h.Db, root, new FileSystemObjectStore(new FileSystemObjectStoreOptions { RootPath = root }));

            // X will be coded AEO (hidden from reviewers), Y walled for the reviewer and the production manager.
            var x = (await content.DocumentAsync(ws)).DocumentId;
            var y = (await content.DocumentAsync(ws)).DocumentId;
            await h.DocumentAsync(ws, "S-001", "merger memo", documentId: x);
            await h.DocumentAsync(ws, "S-002", "merger memo", documentId: y);
            for (var i = 3; i <= 6; i++)
            {
                await h.DocumentAsync(ws, $"S-{i:000}", "merger memo");
            }

            await using var factory = Factory(h, root);
            using var client = factory.CreateClient();
            var api = new Api(client);
            var docs = $"/api/v1/workspaces/{ws}/documents";

            var first = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", reviewer, HttpStatusCode.OK,
                new JsonObject { ["query"] = "merger", ["pageSize"] = 3, ["sort"] = new JsonArray(new JsonObject { ["field"] = "controlNumber" }) });
            ControlNumbers(first).Should().Equal("S-001", "S-002", "S-003");
            var searchId = first.GetProperty("searchId").GetString();
            var next = first.GetProperty("nextCursor").GetString();
            (await api.SendAsync(HttpMethod.Get, $"{docs}/{x}/text", reviewer)).StatusCode.Should().Be(HttpStatusCode.OK);

            // Security-affecting changes commit in PostgreSQL only: X through the coding API, Y as a wall (admin APIs: #51).
            (await api.SendAsync(HttpMethod.Put, $"{docs}/{x}/coding", privilege, new JsonObject
            {
                ["changes"] = new JsonArray(new JsonObject { ["fieldId"] = fields.Confidentiality, ["operation"] = "set", ["value"] = fields.AttorneysEyesOnly }),
            }, ifMatch: "*")).StatusCode.Should().Be(HttpStatusCode.OK);
            await h.Db.WallAsync(ws, [reviewer, manager], [], [y]);
            (await h.Db.Core.ScalarAsync<long>(
                "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc AND status <> 4", ("ws", ws), ("doc", x)))
                .Should().BeGreaterThan(0, "the projection of X is stale: its search work is still pending");

            // Q-12: the stale hits vanish from the pages of the existing search and from a new search (counts may be ≈).
            var again = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{searchId}/pages?page=1", reviewer, HttpStatusCode.OK);
            ControlNumbers(again).Should().Equal("S-003");
            var second = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/searches/{searchId}/pages?cursor={next}", reviewer, HttpStatusCode.OK);
            ControlNumbers(second).Should().Equal("S-004", "S-005", "S-006");
            var fresh = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", reviewer, HttpStatusCode.OK,
                new JsonObject { ["query"] = "merger", ["pageSize"] = 50 });
            ControlNumbers(fresh).Should().BeEquivalentTo(["S-003", "S-004", "S-005", "S-006"]);
            var text = fresh.GetRawText();
            text.Should().NotContain(x.ToString()).And.NotContain(y.ToString());

            // §24: every retrieval path answers 404, identical to a document that does not exist (no oracle).
            var unknown = Guid.CreateVersion7();
            foreach (var (user, document, path) in new[]
            {
                (reviewer, x, ""), (reviewer, x, "?purpose=prefetch"), (reviewer, x, "/text"), (reviewer, x, "/text?purpose=prefetch"),
                (reviewer, x, "/text/chunks/0"), (reviewer, x, "/text/chunks/0?purpose=prefetch"), (reviewer, x, "/pages"),
                (reviewer, x, "/pages?purpose=prefetch"), (reviewer, x, "/pages/1/image"), (reviewer, x, "/pages/1/image?purpose=prefetch"),
                (reviewer, x, "/pages/1/thumbnail?purpose=prefetch"), (reviewer, x, "/coding"),
                (reviewer, y, ""), (reviewer, y, "/text?purpose=prefetch"), (reviewer, y, "/pages/1/image?purpose=prefetch"), (reviewer, y, "/coding"),
                (manager, y, "/native"), (manager, y, "/pages/1/image?purpose=print"), (manager, y, "/text"),
            })
            {
                var denied = await api.ProblemAsync(HttpMethod.Get, $"{docs}/{document}{path}", user);
                var missing = await api.ProblemAsync(HttpMethod.Get, $"{docs}/{unknown}{path}", user);
                denied.Status.Should().Be(HttpStatusCode.NotFound, path);
                denied.Should().Be(missing, "{0}{1}: a hidden document is indistinguishable from a missing one", document, path);
            }

            (await api.SendAsync(HttpMethod.Post, $"{docs}/{x}/views", reviewer, new JsonObject())).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await api.SendAsync(HttpMethod.Get, $"{docs}/{x}/native", manager)).StatusCode.Should().Be(HttpStatusCode.OK, "AEO is cleared for Production Managers");

            // Every refused retrieval was audited before anything was returned.
            (await h.Db.Core.ScalarAsync<long>(
                "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Document' AND outcome = 'Denied' AND resource_id IN (@x, @y)",
                ("ws", ws), ("x", x.ToString()), ("y", y.ToString()))).Should().BeGreaterThanOrEqualTo(18, "17 content requests and the view beacon");

            // A frozen set selected from the stale index (export, production manager): the walled document is never included.
            var snapshot = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", manager, HttpStatusCode.Created,
                new JsonObject { ["purpose"] = "Export", ["query"] = "merger" });
            snapshot.GetProperty("documentCount").GetInt64().Should().Be(5);
            (await h.Db.Core.ScalarAsync<long>(
                "SELECT count(*) FROM opportunity.document_set_snapshot_page WHERE workspace_id = @ws AND snapshot_id = @s AND @y = ANY (document_ids)",
                ("ws", ws), ("s", snapshot.GetProperty("snapshotId").GetGuid()), ("y", y))).Should().Be(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static List<string> ControlNumbers(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("controlNumber").GetString()!)];

    private static WebApplicationFactory<Program> Factory(SearchHarness h, string root) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", h.Options.Endpoint!.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("OpenSearch:Search:FieldCatalogCacheTtl", "00:00:00");
            builder.UseSetting("ObjectStorage:Provider", "FileSystem");
            builder.UseSetting("ObjectStorage:FileSystem:RootPath", root);
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Db.Reader);
                services.AddSingleton<IRestrictionClassBinding, ConfidentialityBinding>();
            });
        });

    /// <summary>A problem response as an attacker sees it (trace ID removed).</summary>
    private sealed record Problem(HttpStatusCode Status, string Body);

    private sealed class Api(HttpClient client)
    {
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
            if (body is not null)
            {
                request.Content = Security.AttackWorld.Json(body);
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            }

            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            return await client.SendAsync(request, Ct);
        }

        public async Task<JsonElement> JsonAsync(HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null)
        {
            using var response = await SendAsync(method, url, user, body);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(expected, text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<Problem> ProblemAsync(HttpMethod method, string url, Guid user)
        {
            using var response = await SendAsync(method, url, user);
            var text = await response.Content.ReadAsStringAsync(Ct);
            if (JsonNode.Parse(text) is JsonObject json)
            {
                json.Remove("traceId");
                text = json.ToJsonString();
            }

            return new Problem(response.StatusCode, text);
        }
    }
}
