using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Snapshots;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.ReviewBatches;

/// <summary>
/// #178 minimal batch review through the real API host, PostgreSQL PDP and OpenSearch: a Batch Set cut in Family Date
/// order, "check out next batch" (skipping batches whose documents are all hidden from the reviewer, returning the batch
/// already held, 409 when none is left), Review mode's search bound to the batch (walled and restricted documents never
/// listed, Q-11/Q-13/Q-52), check-in, release and reassignment by a manager, assignee candidates, and the audit trail.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class BatchReviewTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reviewers_check_out_the_next_batch_review_only_its_visible_documents_and_managers_release_and_reassign()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Search.WorkspaceAsync();
        var manager = await h.MemberAsync(ws, WorkspaceRole.WorkspaceAdmin);
        var reviewer = await h.MemberAsync(ws, WorkspaceRole.Reviewer);
        var reviewer2 = await h.MemberAsync(ws, WorkspaceRole.Reviewer);
        var latecomer = await h.MemberAsync(ws, WorkspaceRole.Reviewer);
        var auditor = await h.MemberAsync(ws, WorkspaceRole.Auditor);

        // Family Date order: R (2018, Attorneys' Eyes Only), W (2019, walled from the first reviewer), P1 + A1 (2020),
        // S2 (2021), S1 (2022). Batches of 2 with families together: [R, W], [P1, A1], [S2, S1].
        var restricted = await h.DocumentAsync(ws, "BR-0001", "alpha restricted", classes: [RestrictionClasses.AttorneysEyesOnly]);
        var walled = await h.DocumentAsync(ws, "BR-0002", "alpha walled", projectSecurity: false);
        await h.Search.Db.WallAsync(ws, [reviewer], [], [walled]);
        var parent = await h.DocumentAsync(ws, "BR-0003", "alpha contract");
        var attachment = await h.DocumentAsync(ws, "BR-0004", "beta schedule", parent: parent, sequence: 1);
        var late = await h.DocumentAsync(ws, "BR-0005", "alpha note");
        var middle = await h.DocumentAsync(ws, "BR-0006", "gamma memo");
        await DateAsync(h, ws, (restricted, 2018), (walled, 2019), (parent, 2020), (attachment, 2020), (middle, 2021), (late, 2022));

        await using var factory = Factory(h);
        using var client = factory.CreateClient();
        var api = new Api(client, ws);

        var snapshot = await api.SendAsync(HttpMethod.Post, "/snapshots", manager, new JsonObject
        {
            ["purpose"] = "ReviewBatch",
            ["documentIds"] = new JsonArray([.. new[] { late, middle, attachment, parent, walled, restricted }.Select(d => (JsonNode)d.ToString())]),
        }, key: "snap");
        snapshot.Status.Should().Be(HttpStatusCode.Created, snapshot.Text);
        var created = await api.SendAsync(HttpMethod.Post, "/review-batch-sets", manager, new JsonObject
        {
            ["name"] = "First pass by date",
            ["snapshotId"] = snapshot.Json.GetProperty("snapshotId").GetString(),
            ["batchPrefix"] = "FD",
            ["maxBatchSize"] = 2,
            ["sort"] = "familyDate",
        }, key: "set");
        created.Status.Should().Be(HttpStatusCode.Created, created.Text);
        created.Json.GetProperty("sort").GetString().Should().Be("familyDate");
        created.Json.GetProperty("sortDescending").GetBoolean().Should().BeFalse();
        var setId = created.Json.GetProperty("batchSetId").GetGuid();
        var batches = (await api.SendAsync(HttpMethod.Get, $"/review-batches?batchSetId={setId}", manager)).Json.GetProperty("items")
            .EnumerateArray().ToList();
        batches.Select(b => b.GetProperty("name").GetString()).Should().Equal("FD_0001", "FD_0002", "FD_0003");
        var ids = batches.Select(b => b.GetProperty("batchId").GetGuid()).ToList();
        (await MembersAsync(api, manager, ids[0])).Should().Equal(restricted, walled);
        (await MembersAsync(api, manager, ids[1])).Should().Equal(parent, attachment);
        (await MembersAsync(api, manager, ids[2])).Should().Equal(middle, late);

        // The first batch holds nothing the first reviewer may see: they get the second one; asking again returns it.
        var next = await api.SendAsync(HttpMethod.Post, $"/review-batch-sets/{setId}/check-out-next", reviewer);
        next.Status.Should().Be(HttpStatusCode.OK, next.Text);
        next.Json.GetProperty("batchId").GetGuid().Should().Be(ids[1]);
        next.Json.GetProperty("status").GetString().Should().Be("checkedOut");
        next.ETag.Should().Be("\"2\"");
        (await api.SendAsync(HttpMethod.Post, $"/review-batch-sets/{setId}/check-out-next", reviewer)).Json.GetProperty("batchId").GetGuid()
            .Should().Be(ids[1], "a reviewer who holds a batch of the set gets it back instead of another one");
        var second = await api.SendAsync(HttpMethod.Post, $"/review-batch-sets/{setId}/check-out-next", reviewer2);
        second.Json.GetProperty("batchId").GetGuid().Should().Be(ids[0], "the walled document is visible to the second reviewer");
        second.Json.GetProperty("documentCount").GetInt32().Should().Be(1, "the restricted document is not counted (Q-52)");

        // Review mode lists the batch through a search bound to it: only its documents the caller may see, in any query.
        (await SearchAsync(api, reviewer, ids[1], string.Empty)).Should().BeEquivalentTo([parent, attachment]);
        (await SearchAsync(api, reviewer, ids[1], "alpha")).Should().Equal(parent);
        (await SearchAsync(api, reviewer2, ids[0], string.Empty)).Should().Equal(walled);
        (await SearchAsync(api, reviewer, ids[0], "alpha")).Should().BeEmpty("walled and restricted documents never appear (Q-11, Q-13)");
        var refused = await api.SendAsync(HttpMethod.Post, "/searches", reviewer,
            new JsonObject { ["query"] = string.Empty, ["reviewBatchId"] = ids[1].ToString(), ["expand"] = new JsonObject { ["family"] = true } });
        refused.Status.Should().Be(HttpStatusCode.BadRequest, refused.Text);
        (await api.SendAsync(HttpMethod.Post, "/searches", reviewer, new JsonObject { ["query"] = string.Empty, ["reviewBatchId"] = Guid.CreateVersion7().ToString() }))
            .Status.Should().Be(HttpStatusCode.NotFound);

        // Check in when done; the next request hands out the last batch, and then there is none left.
        (await api.SendAsync(HttpMethod.Post, $"/review-batches/{ids[1]}/check-in", reviewer, new JsonObject { ["completed"] = true }, ifMatch: next.ETag))
            .Status.Should().Be(HttpStatusCode.OK);
        var third = await api.SendAsync(HttpMethod.Post, $"/review-batch-sets/{setId}/check-out-next", reviewer);
        third.Json.GetProperty("batchId").GetGuid().Should().Be(ids[2]);
        var none = await api.SendAsync(HttpMethod.Post, $"/review-batch-sets/{setId}/check-out-next", latecomer);
        none.Status.Should().Be(HttpStatusCode.Conflict, none.Text);
        (await api.SendAsync(HttpMethod.Post, $"/review-batch-sets/{setId}/check-out-next", auditor)).Status.Should().Be(HttpStatusCode.Forbidden,
            "checking a batch out needs Coding.Write");

        // A manager lists who may take a batch, releases one and reassigns it.
        var candidates = await api.SendAsync(HttpMethod.Get, $"/review-batch-sets/{setId}/assignee-candidates", manager);
        candidates.Status.Should().Be(HttpStatusCode.OK, candidates.Text);
        candidates.Json.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("userId").GetGuid())
            .Should().BeEquivalentTo([manager, reviewer, reviewer2, latecomer], "the auditor may not code");
        (await api.SendAsync(HttpMethod.Get, $"/review-batch-sets/{setId}/assignee-candidates", reviewer)).Status.Should().Be(HttpStatusCode.Forbidden);
        var released = await api.SendAsync(HttpMethod.Put, $"/review-batches/{ids[2]}/assignment", manager, new JsonObject { ["assigneeId"] = null },
            ifMatch: third.ETag);
        released.Status.Should().Be(HttpStatusCode.OK, released.Text);
        released.Json.GetProperty("status").GetString().Should().Be("available");
        var reassigned = await api.SendAsync(HttpMethod.Put, $"/review-batches/{ids[2]}/assignment", manager,
            new JsonObject { ["assigneeId"] = latecomer.ToString() }, ifMatch: released.ETag);
        reassigned.Json.GetProperty("assignee").GetProperty("userId").GetGuid().Should().Be(latecomer);

        var set = await api.SendAsync(HttpMethod.Get, $"/review-batch-sets/{setId}", manager);
        set.Json.GetProperty("statusCounts").GetProperty("checkedOut").GetInt32().Should().Be(2);
        set.Json.GetProperty("statusCounts").GetProperty("completed").GetInt32().Should().Be(1);

        var audit = await h.Search.Db.Core.ColumnAsync(
            $"""
            SELECT e.action || '|' || (e.details->>'batchName') || '|' || e.actor_id
              FROM audit.audit_event e
             WHERE e.workspace_id = '{ws}' AND e.resource_type = 'ReviewBatch'
             ORDER BY e.occurred_at, e.event_id
            """);
        audit.Should().Equal(
            $"ReviewBatch.CheckedOut|FD_0002|{reviewer}",
            $"ReviewBatch.CheckedOut|FD_0001|{reviewer2}",
            $"ReviewBatch.CheckedIn|FD_0002|{reviewer}",
            $"ReviewBatch.CheckedOut|FD_0003|{reviewer}",
            $"ReviewBatch.Assigned|FD_0003|{manager}",
            $"ReviewBatch.Assigned|FD_0003|{manager}");
    }

    private static async Task DateAsync(SnapshotHarness h, Guid ws, params (Guid Document, int Year)[] dates)
    {
        foreach (var (document, year) in dates)
        {
            await h.Search.Db.Core.ExecuteAsync(
                "UPDATE opportunity.document SET family_date = @date WHERE workspace_id = @ws AND document_id = @doc",
                ("ws", ws), ("doc", document), ("date", new DateTimeOffset(year, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        }
    }

    private static async Task<List<Guid>> MembersAsync(Api api, Guid user, Guid batchId)
    {
        var reply = await api.SendAsync(HttpMethod.Get, $"/review-batches/{batchId}/documents", user);
        reply.Status.Should().Be(HttpStatusCode.OK, reply.Text);
        return [.. reply.Json.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("documentId").GetGuid())];
    }

    private static async Task<List<Guid>> SearchAsync(Api api, Guid user, Guid batchId, string query)
    {
        var reply = await api.SendAsync(HttpMethod.Post, "/searches", user,
            new JsonObject { ["query"] = query, ["reviewBatchId"] = batchId.ToString(), ["pageSize"] = 50 });
        reply.Status.Should().Be(HttpStatusCode.OK, reply.Text);
        return [.. reply.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("documentId").GetGuid())];
    }

    private static WebApplicationFactory<Program> Factory(SnapshotHarness h) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Search.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", h.Search.Options.Endpoint!.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Search.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("OpenSearch:Search:FieldCatalogCacheTtl", "00:00:00");
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Search.Db.Reader);
            });
        });

    private sealed record Reply(HttpStatusCode Status, string Text, string? ETag)
    {
        public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();
    }

    private sealed class Api(HttpClient client, Guid workspaceId)
    {
        private readonly string _prefix = $"/api/v1/workspaces/{workspaceId}";

        public async Task<Reply> SendAsync(HttpMethod method, string path, Guid user, JsonNode? body = null, string? ifMatch = null, string? key = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(_prefix + path, UriKind.Relative));
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }

            using var response = await client.SendAsync(request, Ct);
            return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Headers.ETag?.ToString());
        }
    }
}
