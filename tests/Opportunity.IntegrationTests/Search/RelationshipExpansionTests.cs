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
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Snapshots;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E09-T03 against real OpenSearch and PostgreSQL: "Include family / duplicates / email thread" in interactive search
/// (base and expanded counts reported separately, every added row flagged, families kept together, restricted and walled
/// members never added, Q-52), in snapshots (expansion in the freeze, before membership is fixed, and stable after later
/// family edits) and in saved searches (the stored choice applies when the search runs or is frozen).
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class RelationshipExpansionTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expanded_search_reports_base_and_expanded_counts_separately_and_flags_every_added_row()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var corpus = await Corpus.CreateAsync(h);
        var ws = corpus.Workspace;
        var reviewer = corpus.Reviewer;

        // No expansion: two hits, no expanded counts.
        var plain = Ok(await Search(h, ws, reviewer, "merger", null));
        plain.Total.Should().Be(new Contracts.Api.TotalCount(2, Contracts.Api.TotalRelation.Eq));
        plain.Expanded.Should().BeNull();
        plain.Expand.Should().BeNull();
        plain.Items.Should().OnlyContain(i => i.ExpandedBy == null);

        // Family: the hits' attachments are added; the walled attachment is neither returned nor counted (Q-52).
        var family = Ok(await Search(h, ws, reviewer, "merger", new SearchExpand(Family: true)));
        family.Total.Should().Be(new Contracts.Api.TotalCount(2, Contracts.Api.TotalRelation.Eq), "total stays the base hits");
        family.Expand.Should().Be(new SearchExpand(true, false, false));
        family.Expanded.Should().Be(new SearchExpandedCounts(Family: 2, Duplicates: 0, Thread: 0, Total: 4));
        Rows(family, corpus).Should().Equal("PA:-", "A1:family", "A2:family", "PB:-");
        family.Items.Select(i => i.DocumentId).Should().NotContain(corpus.Walled);

        // Everything: duplicates and the thread from the hits, then the families of what they added.
        var all = Ok(await Search(h, ws, reviewer, "merger", new SearchExpand(true, true, true)));
        all.Total.Value.Should().Be(2);
        all.Expanded.Should().Be(new SearchExpandedCounts(Family: 4, Duplicates: 1, Thread: 1, Total: 8));
        Rows(all, corpus).Should().Equal(
            "D1:duplicate", "D1a:family", "PA:-", "A1:family", "A2:family", "PB:-", "T2:thread", "T2a:family");
        all.Items.Should().OnlyContain(i => i.FamilyDate != null, "Date (Family) is on every row");

        // Duplicates only: the duplicate's own attachment does not come along.
        Rows(Ok(await Search(h, ws, reviewer, "merger", new SearchExpand(Duplicates: true))), corpus).Should().Equal("D1:duplicate", "PA:-", "PB:-");

        // Paging follows the expanded list; every page carries the same counts and flags.
        var page = Ok(await Search(h, ws, reviewer, "merger", new SearchExpand(true, true, true), pageSize: 3));
        page.Page.PageCount.Should().Be(3);
        var seen = new List<SearchHit>(page.Items);
        while (page.NextCursor is { } next)
        {
            page = Ok(await h.Search.PageAsync(ws, reviewer, page.SearchId, new SearchPageRequest(next)));
            page.Expanded.Should().Be(all.Expanded);
            page.Total.Value.Should().Be(2);
            seen.AddRange(page.Items);
        }

        seen.Select(i => i.DocumentId).Should().Equal(all.Items.Select(i => i.DocumentId));
        seen.Select(i => i.ExpandedBy).Should().Equal(all.Items.Select(i => i.ExpandedBy));
        var last = Ok(await h.Search.PageAsync(ws, reviewer, page.SearchId, new SearchPageRequest(Last: true)));
        last.Items.Select(i => i.DocumentId).Should().Equal(all.Items.Skip(6).Select(i => i.DocumentId));

        // The privilege reviewer (not walled) gets the walled attachment as family.
        var privileged = Ok(await Search(h, ws, corpus.PrivilegeReviewer, "merger", new SearchExpand(Family: true)));
        privileged.Expanded!.Family.Should().Be(3);
        privileged.Items.Single(i => i.DocumentId == corpus.Walled).ExpandedBy.Should().Be(SearchExpandedBy.Family);

        // Relevance sort keeps the hits first: expanded rows score nothing.
        var relevance = Ok(await h.Search.InScopeSearchAsync(ws, reviewer,
            new SearchRequest("merger", [new SearchSortKey(SearchSortFields.Relevance, SearchSortDirection.Desc)], Expand: new SearchExpand(Family: true))));
        relevance.Items.Take(2).Should().OnlyContain(i => i.ExpandedBy == null);

        // Audit: the expansion and the expanded size are recorded with the executed search.
        h.Search.Audit.Events.Where(e => e.Action == "Executed" && e.Details.GetValueOrDefault("expand") == "family+duplicates+thread")
            .Should().Contain(e => e.Details["expandedTotal"] == "8" && e.Details["total"] == "2");
    }

    [Fact]
    public async Task Stale_restricted_members_are_dropped_from_the_page_like_hits()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var corpus = await Corpus.CreateAsync(h);

        // Restricted in PostgreSQL after it was indexed: the page post-filter (Q-12) drops the expanded member too.
        await h.Search.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'AttorneysEyesOnly')",
            ("ws", corpus.Workspace), ("doc", corpus.Ids["A1"]));

        var page = Ok(await Search(h, corpus.Workspace, corpus.Reviewer, "merger", new SearchExpand(Family: true)));
        Rows(page, corpus).Should().Equal("PA:-", "A2:family", "PB:-");
        page.Expanded!.Family.Should().Be(2, "counts are approximate during projection lag, like totals (Q-10, Q-12)");
    }

    [Fact]
    public async Task Too_many_keys_to_expand_interactively_is_a_request_error()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var corpus = await Corpus.CreateAsync(h);
        h.Search.Options.Search.MaxExpansionKeys = 1;
        try
        {
            var outcome = await Search(h, corpus.Workspace, corpus.Reviewer, "merger", new SearchExpand(Family: true));
            outcome.Status.Should().Be(SearchStatus.InvalidRequest);
            outcome.RequestErrors.Should().ContainKey("expand");
        }
        finally
        {
            h.Search.Options.Search.MaxExpansionKeys = 250_000;
        }
    }

    [Fact]
    public async Task Snapshots_expand_before_freezing_and_keep_their_membership_after_later_family_edits()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var corpus = await Corpus.CreateAsync(h);
        var ws = corpus.Workspace;
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        await h.Search.Db.WallAsync(ws, [qc], [], [corpus.Walled]);

        var snapshot = await h.ReadyAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "merger",
            Expansion: new RelationshipExpansion(true, true, true)));

        snapshot.Expansion.Should().Be(new RelationshipExpansion(true, true, true));
        snapshot.DocumentCount.Should().Be(8);
        snapshot.InclusionCounts.Should().Equal(new Dictionary<SnapshotInclusionReason, long>
        {
            [SnapshotInclusionReason.Hit] = 2,
            [SnapshotInclusionReason.Family] = 4,
            [SnapshotInclusionReason.Duplicate] = 1,
            [SnapshotInclusionReason.Thread] = 1,
        });
        var members = await h.MembersAsync(ws, snapshot);
        members.Select(m => corpus.Name(m.DocumentId) + ":" + m.Reason).Should().BeEquivalentTo(
            "PA:Hit", "PB:Hit", "A1:Family", "A2:Family", "D1:Duplicate", "T2:Thread", "D1a:Family", "T2a:Family");
        members.Select(m => m.DocumentId).Should().NotContain(corpus.Walled, "a walled family member is never added (Q-52)");
        (await h.ServiceAsync(s => s.VerifyAsync(ws, snapshot.SnapshotId, Ct))).Valid.Should().BeTrue();

        // Explicit IDs expand too (Mass Edit of selected rows with "Include family").
        var selected = await h.ReadyAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, DocumentIds: [corpus.Ids["A2"]],
            Expansion: new RelationshipExpansion(true, false, false)));
        (await h.MembersAsync(ws, selected)).Select(m => corpus.Name(m.DocumentId) + ":" + m.Reason)
            .Should().Equal("PA:Family", "A1:Family", "A2:Explicit");

        // Later family edits: A2 leaves family A and a new attachment joins it. The frozen set does not change.
        await h.Search.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document SET family_id = document_id, parent_document_id = NULL, family_sequence = 0 WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", ws), ("doc", corpus.Ids["A2"]));
        var a5 = await h.Search.Db.Core.InsertDocumentAsync(ws, "EXP-A5", d =>
        {
            d.FamilyId = corpus.Ids["PA"];
            d.ParentDocumentId = corpus.Ids["PA"];
            d.FamilySequence = 5;
        });

        var again = await h.Store.GetAsync(ws, snapshot.SnapshotId, Ct);
        again!.DocumentCount.Should().Be(8);
        again.RootSha256.Should().Equal(snapshot.RootSha256!);
        (await h.MembersAsync(ws, again)).Select(m => m.DocumentId).Should().Equal(members.Select(m => m.DocumentId));
        (await h.ServiceAsync(s => s.VerifyAsync(ws, snapshot.SnapshotId, Ct))).Valid.Should().BeTrue();

        // A new snapshot reflects the families as they are now (PostgreSQL is authoritative at freeze time).
        var later = await h.ReadyAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "merger",
            Expansion: new RelationshipExpansion(true, false, false)));
        var laterMembers = (await h.MembersAsync(ws, later)).Select(m => m.DocumentId).ToList();
        laterMembers.Should().Contain(a5.DocumentId).And.NotContain(corpus.Ids["A2"]);

        // The expansion is part of the selection: the header refuses to change it.
        var change = () => h.Search.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document_set_snapshot SET expansion = 0 WHERE workspace_id = @ws AND snapshot_id = @id",
            ("ws", ws), ("id", snapshot.SnapshotId));
        await change.Should().ThrowAsync<Npgsql.PostgresException>().Where(e => e.MessageText.Contains("immutable"));
    }

    [Fact]
    public async Task Saved_searches_and_the_api_apply_the_expansion_and_return_the_documented_shape()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var corpus = await Corpus.CreateAsync(h);
        var ws = corpus.Workspace;
        var reviewer = corpus.Reviewer;
        await using var api = Api(h.Search);

        // POST /searches with expand: total stays base hits; expanded { family, duplicates, thread, total }; expandedBy per row.
        var page = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", reviewer, HttpStatusCode.OK,
            new JsonObject { ["query"] = "merger", ["expand"] = new JsonObject { ["family"] = true } });
        page.GetProperty("total").GetProperty("value").GetInt64().Should().Be(2);
        page.GetProperty("expand").GetProperty("family").GetBoolean().Should().BeTrue();
        var expanded = page.GetProperty("expanded");
        (expanded.GetProperty("family").GetInt64(), expanded.GetProperty("duplicates").GetInt64(), expanded.GetProperty("thread").GetInt64(),
            expanded.GetProperty("total").GetInt64()).Should().Be((2L, 0L, 0L, 4L));
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("expandedBy").ValueKind == JsonValueKind.Null
            ? "-" : i.GetProperty("expandedBy").GetString()).Should().Equal("-", "family", "family", "-");

        // A saved search stores family and duplicates; running it applies them (Q-72: includeFamily now takes effect).
        var saved = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", reviewer, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Merger with family", ["query"] = "merger", ["includeFamily"] = true, ["includeDuplicates"] = true });
        saved.GetProperty("includeDuplicates").GetBoolean().Should().BeTrue();
        saved.GetProperty("includeThread").GetBoolean().Should().BeFalse();
        var savedId = saved.GetProperty("savedSearchId").GetString();
        var run = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", reviewer, HttpStatusCode.OK,
            new JsonObject { ["savedSearchId"] = savedId });
        run.GetProperty("expanded").GetProperty("total").GetInt64().Should().Be(6, "PA, PB, A1, A2, D1 and D1a");
        run.GetProperty("total").GetProperty("value").GetInt64().Should().Be(2);

        // The run's own expand wins over the stored one.
        var overridden = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", reviewer, HttpStatusCode.OK,
            new JsonObject { ["savedSearchId"] = savedId, ["expand"] = new JsonObject { ["family"] = false } });
        overridden.GetProperty("expanded").ValueKind.Should().Be(JsonValueKind.Null);

        // Freezing the saved search (snapshot input) expands the same way, before materialization; the walled A3 stays out.
        var snapshot = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", reviewer, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "report", ["savedSearchId"] = savedId });
        snapshot.GetProperty("documentCount").GetInt64().Should().Be(6);
        snapshot.GetProperty("expand").GetProperty("duplicates").GetBoolean().Should().BeTrue();
        snapshot.GetProperty("inclusionCounts").GetProperty("Family").GetInt64().Should().Be(3);
        snapshot.GetProperty("inclusionCounts").GetProperty("Duplicate").GetInt64().Should().Be(1);

        // A snapshot request may expand on its own.
        var fromQuery = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", reviewer, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "report", ["query"] = "merger", ["expand"] = new JsonObject { ["thread"] = true } });
        fromQuery.GetProperty("inclusionCounts").GetProperty("Thread").GetInt64().Should().Be(1);
        fromQuery.GetProperty("documentCount").GetInt64().Should().Be(3);
    }

    private static Task<SearchOutcome> Search(
        SnapshotHarness h, Guid ws, Guid user, string query, SearchExpand? expand, int pageSize = 50) =>
        h.Search.InScopeSearchAsync(ws, user, new SearchRequest(query, PageSize: pageSize, Expand: expand));

    private static SearchResultPage Ok(SearchOutcome outcome)
    {
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Message).Concat(outcome.RequestErrors.SelectMany(e => e.Value))));
        return outcome.Page!;
    }

    private static IEnumerable<string> Rows(SearchResultPage page, Corpus corpus) =>
        page.Items.Select(i => corpus.Name(i.DocumentId) + ":" + (i.ExpandedBy?.ToString().ToLowerInvariant() ?? "-"));

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

    /// <summary>
    /// Four families and a stray document, related in PostgreSQL and in the projection alike:
    /// family A (PA "merger memo" + A1, A2 and the walled A3), standalone PB "merger plan", family D (D1, a duplicate of PA,
    /// + D1a), family T (T2, in PA's email thread, + T2a) and U. Family dates order the families D, A, B, T.
    /// </summary>
    private sealed class Corpus
    {
        public required Guid Workspace { get; init; }

        public required Guid Reviewer { get; init; }

        public required Guid PrivilegeReviewer { get; init; }

        public Dictionary<string, Guid> Ids { get; } = [];

        public Guid Walled => Ids["A3"];

        public string Name(Guid id) => Ids.Single(p => p.Value == id).Key;

        public static async Task<Corpus> CreateAsync(SnapshotHarness h)
        {
            var ws = await h.Search.WorkspaceAsync();
            var corpus = new Corpus
            {
                Workspace = ws,
                Reviewer = await h.MemberAsync(ws, WorkspaceRole.Reviewer),
                PrivilegeReviewer = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer),
            };
            var group = Guid.CreateVersion7();
            var thread = Guid.CreateVersion7();
            await h.Search.Db.Core.ExecuteAsync(
                "INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @id, 1, 1, 'dup-1')",
                ("ws", ws), ("id", group));
            await h.Search.Db.Core.ExecuteAsync(
                "INSERT INTO opportunity.email_thread (workspace_id, email_thread_id, source, thread_key) VALUES (@ws, @id, 1, 'thread-1')",
                ("ws", ws), ("id", thread));

            var a = Date(2024, 1);
            var d = Date(2023, 12);
            var t = Date(2024, 3);
            await corpus.AddAsync(h, "PA", "EXP-001", "merger memo", a, null, 0, group, thread);
            await corpus.AddAsync(h, "A1", "EXP-002", "spreadsheet", a, "PA", 1);
            await corpus.AddAsync(h, "A2", "EXP-003", "photo", a, "PA", 2);
            await corpus.AddAsync(h, "A3", "EXP-004", "walled attachment", a, "PA", 3);
            await corpus.AddAsync(h, "PB", "EXP-005", "merger plan", Date(2024, 2), null, 0);
            await corpus.AddAsync(h, "D1", "EXP-006", "copy of the memo", d, null, 0, group);
            await corpus.AddAsync(h, "D1a", "EXP-007", "attachment of the copy", d, "D1", 1);
            await corpus.AddAsync(h, "T2", "EXP-008", "reply about the deal", t, null, 0, null, thread);
            await corpus.AddAsync(h, "T2a", "EXP-009", "attachment of the reply", t, "T2", 1);
            await corpus.AddAsync(h, "U", "EXP-010", "unrelated", Date(2022, 1), null, 0);

            // A3 is walled from the reviewer, in PostgreSQL and in the projection.
            var wall = await h.Search.Db.WallAsync(ws, [corpus.Reviewer], [], [corpus.Walled]);
            await corpus.ProjectAsync(h, "A3", wall);
            return corpus;
        }

        private readonly Dictionary<string, JsonObject> _rows = [];

        private async Task AddAsync(
            SnapshotHarness h, string name, string controlNumber, string text, DateTimeOffset familyDate, string? parent, int sequence,
            Guid? duplicateGroup = null, Guid? thread = null)
        {
            var document = await h.Search.Db.Core.InsertDocumentAsync(Workspace, controlNumber, doc =>
            {
                if (parent is not null)
                {
                    doc.FamilyId = Ids[parent];
                    doc.ParentDocumentId = Ids[parent];
                }

                doc.FamilySequence = sequence;
                doc.FamilyDate = familyDate;
                doc.DuplicateGroupId = duplicateGroup;
                doc.EmailThreadId = thread;
                doc.EmailThreadSource = thread is null ? null : EmailThreadSource.Upstream;
            });
            Ids[name] = document.DocumentId;
            _rows[name] = new JsonObject
            {
                ["workspaceId"] = Workspace.ToString("D"),
                ["documentId"] = document.DocumentId.ToString("D"),
                ["controlNumber"] = controlNumber,
                ["controlNumberSort"] = controlNumber,
                ["fileName"] = controlNumber + ".msg",
                ["familyId"] = document.FamilyId.ToString("D"),
                ["parentDocumentId"] = document.ParentDocumentId?.ToString("D"),
                ["familySequence"] = sequence,
                ["familyDate"] = familyDate.ToString("O"),
                ["duplicateGroupId"] = duplicateGroup?.ToString("D"),
                ["emailThreadId"] = thread?.ToString("D"),
                ["text"] = text,
            };
            await ProjectAsync(h, name, null);
        }

        private Task ProjectAsync(SnapshotHarness h, string name, Guid? wall)
        {
            var row = (JsonObject)_rows[name].DeepClone();
            row["securityTags"] = wall is { } w ? new JsonArray(SecurityTagsOf(w)) : new JsonArray();
            return h.Search.ProjectAsync(Workspace, row, Ids[name]);
        }

        private static string SecurityTagsOf(Guid wall) => Opportunity.Search.Querying.SecurityTags.Wall(wall);

        private static DateTimeOffset Date(int year, int month) => new(year, month, 1, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class ApiClient(WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        private readonly HttpClient _client = factory.CreateClient();

        public async Task<JsonElement> JsonAsync(HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
            if (method == HttpMethod.Post && url.EndsWith("/snapshots", StringComparison.Ordinal))
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            }

            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await _client.SendAsync(request, Ct);
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
