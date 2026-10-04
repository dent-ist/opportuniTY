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
using Opportunity.Application.Fields;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T09 saved searches through the real API host, PostgreSQL PDP and audit store: the Searches section's management
/// API, runs that follow the runner's current permissions, nested criteria with cycle detection, deleted fields, snapshot
/// input and audit.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class SavedSearchApiTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const string Group = "litigation-support";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saved_searches_are_managed_shared_and_listed_only_where_visible()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var owner = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer);
        var reviewer = await h.MemberAsync(ws);
        var admin = await h.MemberAsync(ws, WorkspaceRole.WorkspaceAdmin);
        var outsider = await h.MemberAsync(ws);
        await h.DocumentAsync(ws, "SS-001", "apple orchard memo");
        await using var api = Api(h);

        // Folders: create, list, rename with If-Match.
        var folder = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-search-folders", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Hot documents" });
        var folderId = folder.GetProperty("folderId").GetGuid();
        folder.GetProperty("parentFolderId").ValueKind.Should().Be(JsonValueKind.Null);
        var sub = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-search-folders", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Second level", ["parentFolderId"] = folderId.ToString() });
        (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-search-folders", owner,
            new JsonObject { ["name"] = "hot DOCUMENTS" })).StatusCode.Should().Be(HttpStatusCode.Conflict, "sibling names are unique");
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-search-folders/{folderId}", owner,
            new JsonObject { ["name"] = "Hot", ["parentFolderId"] = sub.GetProperty("folderId").GetString() }, ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-search-folders/{folderId}", owner,
            new JsonObject { ["name"] = "Hot" })).ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        (await api.JsonAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-search-folders/{folderId}", owner, HttpStatusCode.OK,
            new JsonObject { ["name"] = "Hot" }, ifMatch: "\"1\"")).GetProperty("version").GetInt64().Should().Be(2);
        var folders = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-search-folders", reviewer, HttpStatusCode.OK);
        folders.GetProperty("items").EnumerateArray().Select(f => f.GetProperty("name").GetString()).Should().Equal("Hot", "Second level");

        // Create: private, versioned, with the criteria and the AST version they were validated against.
        using (var created = await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", owner, new JsonObject
        {
            ["name"] = "Apple memos",
            ["folderId"] = folderId.ToString(),
            ["query"] = "apple",
            ["columns"] = new JsonArray("controlNumber", "fileName"),
            ["sort"] = new JsonArray(new JsonObject { ["field"] = "ControlNumber", ["direction"] = "desc" }),
            ["includeFamily"] = true,
        }))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            created.Headers.ETag!.Tag.Should().Be("\"1\"");
            created.Headers.Location!.ToString().Should().StartWith($"/api/v1/workspaces/{ws}/saved-searches/");
        }

        var list = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches", owner, HttpStatusCode.OK);
        var summary = list.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        var id = summary.GetProperty("savedSearchId").GetGuid();
        summary.GetProperty("folderId").GetGuid().Should().Be(folderId);
        summary.GetProperty("owner").GetProperty("userId").GetGuid().Should().Be(owner);
        summary.GetProperty("scope").GetString().Should().Be("private");
        summary.GetProperty("lastRunAt").ValueKind.Should().Be(JsonValueKind.Null);
        summary.GetProperty("lastHitCount").ValueKind.Should().Be(JsonValueKind.Null);
        var resource = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches/{id}", owner, HttpStatusCode.OK);
        resource.GetProperty("query").GetString().Should().Be("apple");
        resource.GetProperty("astVersion").GetInt32().Should().Be(1);
        resource.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).Should().Equal("controlNumber", "fileName");
        resource.GetProperty("sort")[0].GetProperty("field").GetString().Should().Be("controlNumber");
        resource.GetProperty("sort")[0].GetProperty("direction").GetString().Should().Be("desc");
        resource.GetProperty("includeFamily").GetBoolean().Should().BeTrue();

        // Not shared: other users see nothing (404, never 403), not even through runs or snapshots.
        foreach (var (method, url, body) in new (HttpMethod, string, JsonNode?)[]
        {
            (HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches/{id}", null),
            (HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches/{id}/clone", new JsonObject()),
            (HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", new JsonObject { ["savedSearchId"] = id.ToString() }),
            (HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", new JsonObject { ["purpose"] = "Report", ["savedSearchId"] = id.ToString() }),
        })
        {
            await (await api.SendAsync(method, url, reviewer, body)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        }

        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}", reviewer,
            new JsonObject { ["name"] = "Mine now", ["query"] = "apple" }, ifMatch: "*")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches", reviewer, HttpStatusCode.OK))
            .GetProperty("items").GetArrayLength().Should().Be(0);

        // Sharing needs SavedSearch.Share (a Reviewer lacks it) and is only for the owner or a Workspace Admin.
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}/sharing", reviewer,
            new JsonObject { ["sharedWith"] = new JsonArray() })).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}/sharing", owner, new JsonObject
        {
            ["sharedWith"] = new JsonArray(new JsonObject { ["kind"] = "user", ["id"] = Guid.CreateVersion7().ToString() }),
        })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        var shared = await api.JsonAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}/sharing", owner, HttpStatusCode.OK, new JsonObject
        {
            ["sharedWith"] = new JsonArray(new JsonObject { ["kind"] = "group", ["id"] = Group }, new JsonObject { ["kind"] = "user", ["id"] = admin.ToString() }),
        });
        shared.GetProperty("scope").GetString().Should().Be("shared");
        shared.GetProperty("version").GetInt64().Should().Be(2);
        shared.GetProperty("sharedWith").EnumerateArray().Select(s => (s.GetProperty("kind").GetString(), s.GetProperty("id").GetString()))
            .Should().BeEquivalentTo([("group", Group), ("user", admin.ToString())]);

        // Shared with one of the reviewer's IdP groups: listed with folder, owner, scope; visible but not theirs to change.
        var viaGroup = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches?folderId={folderId}&q=APPLE", reviewer,
            HttpStatusCode.OK, groups: Group);
        var entry = viaGroup.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        entry.GetProperty("scope").GetString().Should().Be("shared");
        entry.GetProperty("owner").GetProperty("userId").GetGuid().Should().Be(owner);
        entry.GetProperty("folderId").GetGuid().Should().Be(folderId);
        (await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches?folderId=root", reviewer, HttpStatusCode.OK, groups: Group))
            .GetProperty("items").GetArrayLength().Should().Be(0);
        (await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches", outsider, HttpStatusCode.OK))
            .GetProperty("items").GetArrayLength().Should().Be(0, "neither the user nor a group of theirs is named");
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}", reviewer,
            new JsonObject { ["name"] = "Mine now", ["query"] = "apple" }, ifMatch: "*", groups: Group))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-searches/{id}", reviewer, ifMatch: "*", groups: Group))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var clone = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches/{id}/clone", reviewer, HttpStatusCode.Created,
            new JsonObject(), groups: Group);
        clone.GetProperty("name").GetString().Should().Be("Copy of Apple memos");
        clone.GetProperty("owner").GetProperty("userId").GetGuid().Should().Be(reviewer);
        clone.GetProperty("scope").GetString().Should().Be("private");
        clone.GetProperty("query").GetString().Should().Be("apple");

        // Workspace Admins see every search, including the reviewer's private clone.
        var all = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches?limit=1", admin, HttpStatusCode.OK);
        all.GetProperty("items").GetArrayLength().Should().Be(1);
        var second = await api.JsonAsync(HttpMethod.Get,
            $"/api/v1/workspaces/{ws}/saved-searches?limit=1&cursor={all.GetProperty("nextCursor").GetString()}", admin, HttpStatusCode.OK);
        new[] { all, second }.Select(p => p.GetProperty("items")[0].GetProperty("name").GetString()).Should().Equal("Apple memos", "Copy of Apple memos");
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        // Modify with optimistic concurrency.
        var update = new JsonObject { ["name"] = "Apple memos (all)", ["folderId"] = folderId.ToString(), ["query"] = "apple OR orchard" };
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}", owner, update))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}", owner, update, ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        var updated = await api.JsonAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}", owner, HttpStatusCode.OK, update, ifMatch: "\"2\"");
        updated.GetProperty("version").GetInt64().Should().Be(3);
        updated.GetProperty("columns").GetArrayLength().Should().Be(0);
        updated.GetProperty("sharedWith").GetArrayLength().Should().Be(2, "replacing the criteria keeps the sharing");

        // A folder that holds a search cannot be deleted; an empty one can.
        await (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-search-folders/{folderId}", owner))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "FOLDER_NOT_EMPTY");
        (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-search-folders/{sub.GetProperty("folderId").GetString()}", reviewer))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "only the folder's creator or a Workspace Admin");
        (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-search-folders/{sub.GetProperty("folderId").GetString()}", owner))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Delete: If-Match, owner; then it is gone for everyone.
        (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-searches/{id}", owner, ifMatch: "\"3\"")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        await (await api.SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches/{id}", admin)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-search-folders/{folderId}", owner))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "FOLDER_NOT_EMPTY");

        // The clone stayed in the folder; a Workspace Admin may delete another user's search.
        (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-searches/{clone.GetProperty("savedSearchId").GetString()}", admin,
            ifMatch: "*")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-search-folders/{folderId}", owner)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        // Create, modify, share and delete are audited in the transaction of the change (criteria as restricted search text).
        var actions = await h.Db.Core.ColumnAsync(
            $"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND resource_id = '{id}' AND category = 'Search' ORDER BY occurred_at");
        actions.Should().Equal("SavedSearch.Created", "SavedSearch.Shared", "SavedSearch.Modified", "SavedSearch.Deleted");
        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND action = 'SavedSearch.Modified' AND restricted_details->>'query' = 'apple OR orchard' AND details->>'changed' = 'name,query,columns,sort,includeFamily'",
            ("ws", ws))).Should().Be(1);
        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND action = 'SavedSearch.Created' AND details->>'clonedFrom' = @id",
            ("ws", ws), ("id", id.ToString()))).Should().Be(1);
    }

    [Fact]
    public async Task Share_candidates_are_the_workspace_members_and_groups_and_need_the_share_permission()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var other = await h.WorkspaceAsync();
        var sharer = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(ws);
        var viaGroup = await h.Db.CreateUserAsync(groups: [Group]);
        await h.Db.AssignGroupAsync(ws, WorkspaceRole.Reviewer, Group);
        var stranger = await h.MemberAsync(other);
        await using var api = Api(h);

        var url = $"/api/v1/workspaces/{ws}/saved-searches/share-candidates";
        var all = (await api.JsonAsync(HttpMethod.Get, url, sharer, HttpStatusCode.OK)).GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("kind").GetString(), i.GetProperty("id").GetString(), i.GetProperty("displayName").GetString())).ToList();
        all.Should().BeEquivalentTo(new[]
        {
            ("group", Group, Group),
            ("user", reviewer.ToString(), reviewer.ToString()),
            ("user", viaGroup.ToString(), viaGroup.ToString()),
        }, "members (directly or through a group) and role-holding groups; never the caller or another workspace's users");
        all.Select(a => a.Item2).Should().NotContain([sharer.ToString(), stranger.ToString()]);

        var filtered = await api.JsonAsync(HttpMethod.Get, $"{url}?q=LITIGATION&limit=5", sharer, HttpStatusCode.OK);
        filtered.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal(Group);
        await (await api.SendAsync(HttpMethod.Get, $"{url}?limit=0", sharer)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await api.SendAsync(HttpMethod.Get, url, reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await api.SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{other}/saved-searches/share-candidates", sharer))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task A_shared_search_runs_with_the_current_permissions_of_whoever_runs_it_and_records_the_run()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var owner = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer);
        var reviewer = await h.MemberAsync(ws);
        await h.DocumentAsync(ws, "RUN-001", "merger agreement draft");
        await h.DocumentAsync(ws, "RUN-002", "merger board minutes");
        var aeo = await h.DocumentAsync(ws, "RUN-003", "merger valuation for counsel", [RestrictionClasses.AttorneysEyesOnly]);
        var walled = await h.Db.DocumentAsync(ws);
        var wall = await h.Db.WallAsync(ws, [owner], [], [walled]);
        await h.DocumentAsync(ws, "RUN-004", "merger side letter", walls: [wall], documentId: walled);
        await using var api = Api(h);

        var saved = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Merger", ["query"] = "merger", ["sort"] = new JsonArray(new JsonObject { ["field"] = "controlNumber" }) });
        var id = saved.GetProperty("savedSearchId").GetGuid();
        await api.JsonAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}/sharing", owner, HttpStatusCode.OK,
            new JsonObject { ["sharedWith"] = new JsonArray(new JsonObject { ["kind"] = "user", ["id"] = reviewer.ToString() }) });

        // The owner (Privilege Reviewer, walled from RUN-004) sees the AEO document; the reviewer sees neither restricted one,
        // but does see RUN-004: the saved search carries no rights of its creator, only criteria.
        var asOwner = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", owner, HttpStatusCode.OK,
            new JsonObject { ["savedSearchId"] = id.ToString() });
        ControlNumbers(asOwner).Should().Equal("RUN-001", "RUN-002", "RUN-003");
        asOwner.GetProperty("savedSearchId").GetGuid().Should().Be(id);
        asOwner.GetProperty("normalized").GetString().Should().Be("merger");

        var asReviewer = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", reviewer, HttpStatusCode.OK,
            new JsonObject { ["savedSearchId"] = id.ToString(), ["pageSize"] = 2 });
        ControlNumbers(asReviewer).Should().Equal("RUN-001", "RUN-002");
        asReviewer.GetProperty("total").GetProperty("value").GetInt64().Should().Be(3, "counts are computed only over what the runner may see");
        var next = await api.JsonAsync(HttpMethod.Get,
            $"/api/v1/workspaces/{ws}/searches/{asReviewer.GetProperty("searchId").GetString()}/pages?cursor={asReviewer.GetProperty("nextCursor").GetString()}",
            reviewer, HttpStatusCode.OK);
        ControlNumbers(next).Should().Equal("RUN-004");
        ControlNumbers(asReviewer).Concat(ControlNumbers(next)).Should().NotContain("RUN-003");
        _ = aeo;

        // The last run (the reviewer's) is recorded on the search with its hit count and freshness.
        var listed = await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches", owner, HttpStatusCode.OK);
        var entry = listed.GetProperty("items")[0];
        entry.GetProperty("lastRunAt").ValueKind.Should().Be(JsonValueKind.String);
        entry.GetProperty("lastHitCount").GetInt64().Should().Be(3);
        entry.GetProperty("lastHitRelation").GetString().Should().Be("eq");
        entry.GetProperty("lastRunFreshness").GetProperty("state").GetString().Should().Be("current");
        entry.GetProperty("version").GetInt64().Should().Be(2, "a run is not a modification");

        // Access is current, not frozen: revoking the share hides the search from the reviewer at once.
        await api.JsonAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}/sharing", owner, HttpStatusCode.OK,
            new JsonObject { ["sharedWith"] = new JsonArray() });
        await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", reviewer, new JsonObject { ["savedSearchId"] = id.ToString() }))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // Runs are audited as Search.Executed with the saved search's ID and its criteria as search text.
        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND action = 'Executed' AND details->>'savedSearchId' = @id AND restricted_details->>'query' = 'merger'",
            ("ws", ws), ("id", id.ToString()))).Should().Be(2);

        // Either query or savedSearchId.
        await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", owner,
            new JsonObject { ["savedSearchId"] = id.ToString(), ["query"] = "merger" })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // Snapshot (and so bulk) input: the frozen set holds what the creator may act on.
        var snapshot = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", owner, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "Report", ["savedSearchId"] = id.ToString() });
        snapshot.GetProperty("documentCount").GetInt64().Should().Be(3);
        snapshot.GetProperty("source").GetProperty("query").GetString().Should().Be($"savedsearch:{id:D}");
        await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", owner,
            new JsonObject { ["purpose"] = "Report", ["savedSearchId"] = Guid.CreateVersion7().ToString() })).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Nested_saved_searches_expand_at_run_time_and_cycles_are_rejected()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var other = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        await h.DocumentAsync(ws, "NEST-001", "apple iphone launch");
        await h.DocumentAsync(ws, "NEST-002", "apple orchard harvest");
        await h.DocumentAsync(ws, "NEST-003", "banana iphone case");
        await using var api = Api(h);

        async Task<JsonElement> SaveAsync(Guid by, string name, string query) =>
            await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", by, HttpStatusCode.Created,
                new JsonObject { ["name"] = name, ["query"] = query });

        var apple = (await SaveAsync(user, "Apple", "apple")).GetProperty("savedSearchId").GetGuid();
        var appleIphone = (await SaveAsync(user, "Apple iPhone", $"savedsearch:{apple} AND iphone")).GetProperty("savedSearchId").GetGuid();
        var top = await SaveAsync(user, "Top", $"savedsearch:\"{appleIphone:N}\" OR banana");
        var topId = top.GetProperty("savedSearchId").GetGuid();

        var run = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", user, HttpStatusCode.OK,
            new JsonObject { ["savedSearchId"] = topId.ToString(), ["sort"] = new JsonArray(new JsonObject { ["field"] = "controlNumber" }) });
        ControlNumbers(run).Should().Equal("NEST-001", "NEST-003");

        // Re-parsed at run time: changing an inner search changes every search that uses it.
        await api.JsonAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{apple}", user, HttpStatusCode.OK,
            new JsonObject { ["name"] = "Apple", ["query"] = "apple OR banana" }, ifMatch: "*");
        ControlNumbers(await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", user, HttpStatusCode.OK,
            new JsonObject { ["query"] = $"savedsearch:{appleIphone}", ["sort"] = new JsonArray(new JsonObject { ["field"] = "controlNumber" }) }))
            .Should().Equal("NEST-001", "NEST-003");

        // Cycles: directly and through a chain, on save; the validate endpoint reports the same codes with spans.
        var selfRef = await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{apple}", user,
            new JsonObject { ["name"] = "Apple", ["query"] = $"apple OR savedsearch:{apple}" }, ifMatch: "*");
        Codes(await selfRef.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query")).Should().Equal("SAVED_SEARCH_CYCLE");
        var loop = await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{apple}", user,
            new JsonObject { ["name"] = "Apple", ["query"] = $"savedsearch:{topId}" }, ifMatch: "*");
        var loopProblem = await loop.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
        Codes(loopProblem).Should().Equal("SAVED_SEARCH_CYCLE");
        loopProblem.GetProperty("queryErrors")[0].GetProperty("span").GetProperty("start").GetInt32().Should().Be(0);
        (await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/saved-searches/{apple}", user, HttpStatusCode.OK))
            .GetProperty("query").GetString().Should().Be("apple OR banana", "a rejected change is not stored");

        // References must name a search the caller can see, at Boolean level.
        var hidden = (await SaveAsync(other, "Other's", "orchard")).GetProperty("savedSearchId").GetGuid();
        foreach (var (query, code) in new[]
        {
            ($"savedsearch:{hidden}", "SAVED_SEARCH_NOT_FOUND"),
            ($"savedsearch:{Guid.CreateVersion7()}", "SAVED_SEARCH_NOT_FOUND"),
            ("savedsearch:*", "SAVED_SEARCH_INVALID_REFERENCE"),
            ("savedsearch:not-an-id", "SAVED_SEARCH_INVALID_REFERENCE"),
        })
        {
            var validation = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/query-validations", user, HttpStatusCode.OK,
                new JsonObject { ["query"] = query });
            validation.GetProperty("valid").GetBoolean().Should().BeFalse(query);
            validation.GetProperty("errors")[0].GetProperty("code").GetString().Should().Be(code, query);
            Codes(await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", user, new JsonObject { ["query"] = query }))
                .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query")).Should().Contain(code, query);
        }

        var valid = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/query-validations", user, HttpStatusCode.OK,
            new JsonObject { ["query"] = $"savedsearch:{topId} AND NOT orchard" });
        valid.GetProperty("valid").GetBoolean().Should().BeTrue(valid.GetRawText());
        valid.GetProperty("normalized").GetString().Should().Be($"savedsearch:{topId} AND NOT orchard");

        // Shared with the other user: they may run it, and the nested searches inside it are part of its criteria.
        await api.JsonAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{topId}/sharing", user, HttpStatusCode.OK,
            new JsonObject { ["sharedWith"] = new JsonArray(new JsonObject { ["kind"] = "user", ["id"] = other.ToString() }) });
        ControlNumbers(await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", other, HttpStatusCode.OK,
            new JsonObject { ["savedSearchId"] = topId.ToString(), ["sort"] = new JsonArray(new JsonObject { ["field"] = "controlNumber" }) }))
            .Should().Equal("NEST-001", "NEST-003");

        // A deleted inner search: the outer search fails with a clear error instead of silently matching less.
        await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/saved-searches/{apple}", user, ifMatch: "*");
        var broken = await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", user,
            new JsonObject { ["savedSearchId"] = topId.ToString() })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
        var error = broken.GetProperty("queryErrors")[0];
        error.GetProperty("code").GetString().Should().Be("SAVED_SEARCH_NOT_FOUND");
        error.GetProperty("message").GetString().Should().Contain("'Apple iPhone'");
    }

    [Fact]
    public async Task A_saved_search_that_uses_a_deleted_field_returns_a_clear_validation_error()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws, WorkspaceRole.WorkspaceAdmin);
        await h.Fields.InitializeWorkspaceAsync(ws, Ct);
        var custodian = (await h.Fields.CreateFieldAsync(new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata), Ct)).Value!;
        await h.DocumentAsync(ws, "FLD-001", "quarterly report");
        await using var api = Api(h);

        var saved = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", user, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Smith", ["query"] = "report AND custodian:smith" });
        var id = saved.GetProperty("savedSearchId").GetGuid();
        var outer = (await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", user, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Outer", ["query"] = $"quarterly AND savedsearch:{id}" })).GetProperty("savedSearchId").GetGuid();
        (await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", user, HttpStatusCode.OK,
            new JsonObject { ["savedSearchId"] = id.ToString() })).GetProperty("items").GetArrayLength().Should().Be(0);

        (await h.Fields.DeleteFieldAsync(ws, custodian.FieldId, Ct)).Succeeded.Should().BeTrue();

        // Running it: 400 with the unknown field positioned in the saved search's own text.
        var direct = await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", user,
            new JsonObject { ["savedSearchId"] = id.ToString() })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
        var error = direct.GetProperty("queryErrors")[0];
        error.GetProperty("code").GetString().Should().Be("UNKNOWN_FIELD");
        error.GetProperty("span").GetProperty("start").GetInt32().Should().Be("report AND ".Length);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        // Through a nesting search: the error points at the reference and names the saved search.
        var nested = await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", user,
            new JsonObject { ["savedSearchId"] = outer.ToString() })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
        var nestedError = nested.GetProperty("queryErrors")[0];
        nestedError.GetProperty("code").GetString().Should().Be("UNKNOWN_FIELD");
        nestedError.GetProperty("message").GetString().Should().StartWith("In saved search 'Smith':");
        nestedError.GetProperty("span").GetProperty("start").GetInt32().Should().Be("quarterly AND ".Length);

        // Saving criteria with the deleted field is refused the same way.
        await (await api.SendAsync(HttpMethod.Put, $"/api/v1/workspaces/{ws}/saved-searches/{id}", user,
            new JsonObject { ["name"] = "Smith", ["query"] = "custodian:jones" }, ifMatch: "*")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
        await (await api.SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", user,
            new JsonObject { ["purpose"] = "Report", ["savedSearchId"] = id.ToString() })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid-query");
    }

    private static List<string?> ControlNumbers(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("controlNumber").GetString()).ToList();

    private static List<string?> Codes(JsonElement problem) =>
        problem.GetProperty("queryErrors").EnumerateArray().Select(e => e.GetProperty("code").GetString()).Distinct().ToList();

    private static ApiClient Api(SearchHarness h)
    {
        var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", h.Db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", h.Options.Endpoint!.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", h.Scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("OpenSearch:Search:FieldCatalogCacheTtl", "00:00:00");
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader>(h.Db.Reader);
            });
        });
        return new ApiClient(factory);
    }

    private sealed class ApiClient(WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        private readonly HttpClient _client = factory.CreateClient();

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null, string? groups = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
            if (groups is not null)
            {
                request.Headers.Add(TestAuthentication.GroupsHeader, groups);
            }

            if (method == HttpMethod.Post && url.EndsWith("/snapshots", StringComparison.Ordinal))
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            }

            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            return await _client.SendAsync(request, Ct);
        }

        public async Task<JsonElement> JsonAsync(
            HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null, string? ifMatch = null, string? groups = null)
        {
            using var response = await SendAsync(method, url, user, body, ifMatch, groups);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(expected, "{0} {1}: {2}", method, url, text);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await factory.DisposeAsync();
        }
    }
}
