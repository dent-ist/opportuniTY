using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T05 against real OpenSearch and PostgreSQL: the workspace filter cannot be escaped, restricted and walled
/// documents never come back (search-side filter and the Q-12 page post-filter), search handles and cursors are
/// bound to (user, session, workspace), paging follows Q-32/Q-33/Q-49, and executed search text is audited (Q-16).
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class SearchServiceTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workspace_never_sees_another_workspaces_documents(bool dedicated)
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws1 = await h.WorkspaceAsync(dedicated);
        var ws2 = await h.WorkspaceAsync(dedicated);
        var user = await h.MemberAsync(ws1);
        await h.Db.AssignAsync(ws2, WorkspaceRole.Reviewer, user);
        var mine = await h.DocumentAsync(ws1, "WS1-001", "privileged merger memo");
        var theirs = await h.DocumentAsync(ws2, "WS2-001", "privileged merger memo");

        // A projection row in WS-1's index that claims WS-2, and one that claims WS-1 for a WS-2 document.
        await h.ProjectAsync(ws1, Row(ws2, Guid.CreateVersion7(), "FORGED-1", "privileged merger memo"), Guid.CreateVersion7());
        await h.ProjectAsync(ws1, Row(ws1, theirs, "FORGED-2", "privileged merger memo"), Guid.CreateVersion7());

        foreach (var query in new[]
        {
            "privileged", "privileged OR NOT privileged", "", "NOT nothing", "(merger OR memo) OR NOT (merger OR memo)",
            "controlNumber:WS2*", "fileName:WS2*", "\"privileged merger\" OR merger W/2 memo",
        })
        {
            var page = Ok(await h.SearchAsync(ws1, user, query));
            page.Items.Select(i => i.DocumentId).Should().OnlyContain(id => id == mine, "query {0}", query);
        }

        foreach (var injection in new[] { $"workspaceId:{ws2}", $"privileged OR workspaceId:{ws2}", $"NOT workspaceId:{ws1}", "securityTags:*", $"documentId:{theirs}" })
        {
            var outcome = await h.SearchAsync(ws1, user, injection);
            outcome.Status.Should().Be(SearchStatus.InvalidQuery, injection);
            outcome.QueryErrors.Should().Contain(e => e.Code == "UNKNOWN_FIELD", injection);
        }

        Ok(await h.SearchAsync(ws2, user, "privileged")).Items.Select(i => i.DocumentId).Should().Equal(theirs);
        h.Recorder.SearchBodies.Should().OnlyContain(b => FilterWorkspace(b) == ws1.ToString("D") || FilterWorkspace(b) == ws2.ToString("D"));
    }

    [Fact]
    public async Task Restricted_and_walled_documents_are_filtered_in_search_and_on_every_page()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var reviewer = await h.MemberAsync(ws);
        var privilegeReviewer = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer);
        var plain = await h.DocumentAsync(ws, "DOC-001", "apple launches the iphone");
        var aeo = await h.DocumentAsync(ws, "DOC-002", "apple iphone pricing secret", [RestrictionClasses.AttorneysEyesOnly]);
        var walledDoc = await h.Db.DocumentAsync(ws);
        var wall = await h.Db.WallAsync(ws, [reviewer], [], [walledDoc]);
        await h.DocumentAsync(ws, "DOC-003", "apple iphone supplier dispute", walls: [wall], documentId: walledDoc);

        // Projected security attributes: excluded by the outer filter, so not even counted.
        var asReviewer = Ok(await h.SearchAsync(ws, reviewer, "apple"));
        asReviewer.Items.Select(i => i.DocumentId).Should().Equal(plain);
        asReviewer.Total.Should().Be(new TotalCount(1, TotalRelation.Eq));
        Ok(await h.SearchAsync(ws, privilegeReviewer, "apple")).Items.Select(i => i.DocumentId).Should().Equal(plain, aeo, walledDoc);

        // Stale projection (classes and walls applied in PostgreSQL after indexing): the page post-filter drops them.
        var lateAeo = await h.DocumentAsync(ws, "DOC-004", "apple iphone memo to counsel", projectSecurity: false);
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'AttorneysEyesOnly')",
            ("ws", ws), ("doc", lateAeo));
        var lateWalled = await h.DocumentAsync(ws, "DOC-005", "apple iphone board minutes", projectSecurity: false);
        await h.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) VALUES (@ws, @doc, @wall)",
            ("ws", ws), ("doc", lateWalled), ("wall", wall));
        h.Audit.Clear();

        var stale = Ok(await h.SearchAsync(ws, reviewer, "iphone"));
        stale.Items.Select(i => i.DocumentId).Should().Equal(plain);
        stale.Items.Single().Snippets.Should().ContainSingle().Which.Text.Should().Contain("iphone");
        stale.Total.Value.Should().Be(3, "counts are approximate during projection lag (Q-10, Q-12); the hits themselves never leak");
        var denied = h.Audit.Events.Where(e => e.Action == AuditTaxonomy.AuthZ.Denied).Should().ContainSingle("one summary per page (Q-59)").Subject;
        denied.Details["denied"].Should().Be("2");
        denied.Details["denied.RestrictionClass"].Should().Be("1");
        denied.Details["denied.EthicalWall"].Should().Be("1");

        // The privilege reviewer is walled from neither late document and holds the AEO grant.
        Ok(await h.SearchAsync(ws, privilegeReviewer, "iphone")).Items.Select(i => i.DocumentId).Should().Contain([lateAeo, lateWalled]);

        // Highlights never carry text of a dropped document, on any page.
        var paged = Ok(await h.SearchAsync(ws, reviewer, "iphone", pageSize: 1));
        var seen = new List<SearchHit>(paged.Items);
        while (paged.NextCursor is { } next)
        {
            paged = Ok(await h.PageAsync(ws, reviewer, paged.SearchId, new SearchPageRequest(next)));
            seen.AddRange(paged.Items);
        }

        seen.Select(i => i.DocumentId).Should().Equal(plain);
        seen.SelectMany(i => i.Snippets).Select(s => s.Text).Should().NotContain(t => t.Contains("counsel") || t.Contains("board"));
    }

    [Fact]
    public async Task Search_handles_and_cursors_are_bound_to_user_session_and_workspace()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws1 = await h.WorkspaceAsync();
        var ws2 = await h.WorkspaceAsync();
        var alice = await h.MemberAsync(ws1);
        var bob = await h.MemberAsync(ws1);
        await h.Db.AssignAsync(ws2, WorkspaceRole.Reviewer, alice);
        var session = Guid.CreateVersion7();
        for (var i = 1; i <= 4; i++)
        {
            await h.DocumentAsync(ws1, $"A-{i:000}", "quarterly report");
            await h.DocumentAsync(ws2, $"B-{i:000}", "quarterly report");
        }

        var first = Ok(await h.SearchAsync(ws1, alice, "report", pageSize: 2, session: session));
        var other = Ok(await h.SearchAsync(ws2, alice, "report", pageSize: 2, session: session));
        first.NextCursor.Should().NotBeNull();
        var next = new SearchPageRequest(first.NextCursor);
        h.Audit.Clear();

        (await h.PageAsync(ws1, bob, first.SearchId, next, session)).Status.Should().Be(SearchStatus.NotFound, "another user");
        (await h.PageAsync(ws1, alice, first.SearchId, next, Guid.CreateVersion7())).Status.Should().Be(SearchStatus.NotFound, "another session");
        (await h.PageAsync(ws1, alice, first.SearchId, next)).Status.Should().Be(SearchStatus.NotFound, "no session");
        (await h.PageAsync(ws2, alice, first.SearchId, next, session)).Status.Should().Be(SearchStatus.NotFound, "another workspace");
        h.Audit.Events.Where(e => e.Action == AuditTaxonomy.AuthZ.Denied).Select(e => e.ReasonCode)
            .Should().Equal(Enumerable.Repeat("SearchHandleMismatch", 3), "replays by another user or session are audited");

        // Tampered or foreign cursors and handles.
        foreach (var cursor in new[] { Guid.NewGuid().ToString("N"), "not-a-cursor", first.NextCursor!.ToUpperInvariant() + "0", other.NextCursor!, "" })
        {
            (await h.PageAsync(ws1, alice, first.SearchId, new SearchPageRequest(cursor), session)).Status.Should().Be(SearchStatus.NotFound, cursor);
        }

        foreach (var handle in new[] { Guid.NewGuid().ToString("N"), other.SearchId!, first.SearchId![..^1] + (first.SearchId[^1] == '0' ? "1" : "0"), "../" + first.SearchId })
        {
            (await h.PageAsync(ws1, alice, handle, next, session)).Status.Should().Be(SearchStatus.NotFound, handle);
        }

        (await h.PageAsync(ws1, alice, first.SearchId, new SearchPageRequest(first.NextCursor, Last: true), session))
            .Status.Should().Be(SearchStatus.InvalidRequest);

        var second = Ok(await h.PageAsync(ws1, alice, first.SearchId, next, session));
        second.Items.Select(i => i.ControlNumber).Should().Equal("A-003", "A-004");

        // Removing the user's role takes effect on the next page, like any other request.
        await h.Db.Core.ExecuteAsync("DELETE FROM opportunity.workspace_role_assignment WHERE user_id = @u AND workspace_id = @ws", ("u", alice), ("ws", ws1));
        (await h.PageAsync(ws1, alice, first.SearchId, new SearchPageRequest(second.PreviousCursor), session)).Status.Should().Be(SearchStatus.NotFound);
    }

    [Fact]
    public async Task Paging_walks_forward_backward_jumps_near_the_top_and_reverses_for_last()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres, o => o.MaxResultWindow = 20);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        for (var i = 1; i <= 23; i++)
        {
            await h.DocumentAsync(ws, $"CN-{i:000}", "invoice");
        }

        var p1 = Ok(await h.SearchAsync(ws, user, "invoice", pageSize: 5));
        Numbers(p1).Should().Equal(1, 2, 3, 4, 5);
        p1.Page.Should().Be(new SearchPageInfo(1, 5, 5, IsFirst: true, IsLast: false));
        p1.PreviousCursor.Should().BeNull();
        p1.Total.Should().Be(new TotalCount(23, TotalRelation.Eq));
        p1.SearchId.Should().MatchRegex("^[0-9a-f]{32}$");

        var p2 = Ok(await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(p1.NextCursor)));
        Numbers(p2).Should().Equal(6, 7, 8, 9, 10);
        p2.Page.Number.Should().Be(2);
        var back = Ok(await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(p2.PreviousCursor)));
        Numbers(back).Should().Equal(1, 2, 3, 4, 5);
        back.Page.Should().Be(new SearchPageInfo(1, 5, 5, IsFirst: true, IsLast: false));

        var last = Ok(await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(Last: true)));
        Numbers(last).Should().Equal(21, 22, 23);
        last.Page.Should().Be(new SearchPageInfo(5, 5, 5, IsFirst: false, IsLast: true));
        last.NextCursor.Should().BeNull();
        var beforeLast = Ok(await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(last.PreviousCursor)));
        Numbers(beforeLast).Should().Equal(16, 17, 18, 19, 20);
        beforeLast.Page.Number.Should().Be(4);
        Numbers(Ok(await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(beforeLast.NextCursor)))).Should().Equal(21, 22, 23);

        var p3 = Ok(await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(Number: 3)));
        Numbers(p3).Should().Equal(11, 12, 13, 14, 15);
        p3.Page.Number.Should().Be(3);
        var deep = await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(Number: 4));
        deep.Status.Should().Be(SearchStatus.InvalidRequest, "Q-49: no deep page jumps past the result window");
        (await h.PageAsync(ws, user, p1.SearchId, new SearchPageRequest(Number: 0))).Status.Should().Be(SearchStatus.InvalidRequest);

        // Q-32: a capped total is a lower bound and gives no page count; "count exactly" lifts the cap.
        await using var capped = await SearchHarness.CreateAsync(openSearch, postgres, o => o.Search.TrackTotalHitsUpTo = 10);
        var cws = await capped.WorkspaceAsync();
        var cuser = await capped.MemberAsync(cws);
        for (var i = 1; i <= 12; i++)
        {
            await capped.DocumentAsync(cws, $"X-{i:000}", "invoice");
        }

        var approx = Ok(await capped.SearchAsync(cws, cuser, "invoice", pageSize: 5));
        approx.Total.Should().Be(new TotalCount(10, TotalRelation.Gte));
        approx.Page.PageCount.Should().BeNull();
        Ok(await capped.PageAsync(cws, cuser, approx.SearchId, new SearchPageRequest(Last: true))).Page.Number.Should().BeNull();
        var exact = Ok(await capped.SearchAsync(cws, cuser, "invoice", pageSize: 5, countExact: true));
        exact.Total.Should().Be(new TotalCount(12, TotalRelation.Eq));
        capped.Audit.Events.Should().Contain(e => e.Category == "Search" && e.Action == "CountExact");
    }

    [Fact]
    public async Task Every_request_carries_the_workspace_filter_and_an_expired_reader_is_reopened()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        for (var i = 1; i <= 6; i++)
        {
            await h.DocumentAsync(ws, $"P-{i:000}", "pricing apple iphone");
        }

        h.Recorder.Clear();
        var first = Ok(await h.SearchAsync(ws, user, "apple iphone", pageSize: 2, facets: ["fileType"]));
        first.Facets.Should().ContainSingle().Which.Buckets.Should().Equal(new SearchFacetBucket("Email", 6));
        first.Items.Should().OnlyContain(i => i.Snippets.Count > 0 && i.Snippets[0].Highlights.Count > 0);
        first.ResultsRefreshed.Should().BeFalse();
        var second = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(first.NextCursor)));
        Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(Number: 2)));
        Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(Last: true)));

        // Q-33: the live reader expired; the next page reopens it and says so.
        await h.ExpireReadersAsync();
        var refreshed = Ok(await h.PageAsync(ws, user, first.SearchId, new SearchPageRequest(second.NextCursor)));
        refreshed.ResultsRefreshed.Should().BeTrue();
        refreshed.Items.Select(i => i.ControlNumber).Should().Equal("P-005", "P-006");

        var bodies = h.Recorder.SearchBodies;
        bodies.Should().HaveCountGreaterThanOrEqualTo(6);
        bodies.Should().OnlyContain(b => FilterWorkspace(b) == ws.ToString("D") && b["pit"] != null, "hits, aggregations, highlights and continuations");
        bodies.Should().Contain(b => b["aggs"] != null).And.Contain(b => b["search_after"] != null).And.Contain(b => b["highlight"] != null);
        var pits = h.Recorder.Requests.Where(r => r.PathAndQuery.Contains("/_search/point_in_time", StringComparison.Ordinal)).ToList();
        pits.Should().HaveCount(2, "one reader per search, one reopened").And.OnlyContain(r => r.PathAndQuery.Contains("routing=" + ws.ToString("D")));
        h.Recorder.Requests.Should().NotContain(r => r.PathAndQuery.Contains("/_search", StringComparison.Ordinal)
            && !r.PathAndQuery.StartsWith("/_search", StringComparison.Ordinal) && !r.PathAndQuery.Contains("point_in_time", StringComparison.Ordinal),
            "searches only ever run against the point-in-time reader");
    }

    [Fact]
    public async Task Filter_row_clauses_run_with_catalogue_names_and_contains_on_file_name()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        var early = await h.DocumentAsync(ws, "FLT-001", "alpha memo"); // FLT-001.msg, 2024-05-08
        var late = await h.DocumentAsync(ws, "FLT-0002", "beta memo"); // FLT-0002.msg, 2024-05-09

        Ok(await h.SearchAsync(ws, user, "filename:*0002*")).Items.Select(i => i.DocumentId).Should().Equal(late);
        Ok(await h.SearchAsync(ws, user, "date:[2024-05-09 TO *]")).Items.Select(i => i.DocumentId).Should().Equal(late);
        Ok(await h.SearchAsync(ws, user, @"(memo) AND filename:*t\-00* AND NOT date:[2024-05-09 TO *]"))
            .Items.Select(i => i.DocumentId).Should().Equal(early);
    }

    [Fact]
    public async Task Executed_search_text_is_audited_and_unindexed_workspaces_answer_empty()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        await h.DocumentAsync(ws, "Q-001", "settlement draft");

        var page = Ok(await h.SearchAsync(ws, user, "settlement   draft"));
        var executed = h.Audit.Events.Should().ContainSingle(e => e.Category == "Search" && e.Action == "Executed").Subject;
        executed.RestrictedDetails!["query"].Should().Be("settlement   draft");
        executed.RestrictedDetails["normalized"].Should().Be("settlement AND draft");
        executed.RestrictedDetails["ast"].Should().Contain("\"kind\"");
        executed.Details.Values.Should().NotContain(v => v != null && v.Contains("settlement"), "search text is restricted detail only (Q-16)");
        executed.ResourceId.Should().Be(page.SearchId);
        executed.Details["returned"].Should().Be("1");

        Ok(await h.PageAsync(ws, user, page.SearchId, new SearchPageRequest(Last: true)));
        h.Audit.Events.Should().ContainSingle(e => e.Action == "ResultsPageServed").Which.RestrictedDetails.Should().BeNull();

        var unplaced = await h.Db.Core.CreateWorkspaceAsync();
        await h.Db.AssignAsync(unplaced, WorkspaceRole.Reviewer, user);
        var empty = Ok(await h.SearchAsync(unplaced, user, "anything"));
        empty.Items.Should().BeEmpty();
        empty.SearchId.Should().BeNull();
        empty.Total.Should().Be(new TotalCount(0, TotalRelation.Eq));

        var outsider = await h.Db.CreateUserAsync();
        (await h.SearchAsync(ws, outsider, "settlement")).Status.Should().Be(SearchStatus.NotFound);
        var auditor = await h.MemberAsync(ws, WorkspaceRole.Auditor);
        (await h.SearchAsync(ws, auditor, "settlement")).Status.Should().Be(
            RoleCatalog.Get(WorkspaceRole.Auditor).Grants.Contains(Permission.SearchExecute) ? SearchStatus.Ok : SearchStatus.Forbidden);
        (await h.SearchAsync(ws, user, "settlement (")).Status.Should().Be(SearchStatus.InvalidQuery);
    }

    private static SearchResultPage Ok(SearchOutcome outcome)
    {
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)
            .Concat(outcome.RequestErrors.SelectMany(e => e.Value))));
        return outcome.Page!;
    }

    private static int[] Numbers(SearchResultPage page) => [.. page.Items.Select(i => int.Parse(i.ControlNumber[3..], System.Globalization.CultureInfo.InvariantCulture))];

    private static string? FilterWorkspace(JsonObject body) =>
        body["query"]?["bool"]?["filter"]?[0]?["term"]?["workspaceId"]?["value"]?.GetValue<string>();

    private static JsonObject Row(Guid workspaceId, Guid documentId, string controlNumber, string text) => new()
    {
        ["workspaceId"] = workspaceId.ToString("D"),
        ["documentId"] = documentId.ToString("D"),
        ["controlNumber"] = controlNumber,
        ["text"] = text,
    };
}
