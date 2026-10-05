using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search;
using Opportunity.Core.Security;
using Opportunity.Data.Search;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Content;

/// <summary>
/// E16-T12 through the real API host, the PostgreSQL PDP and a filesystem object store: term hits computed over the
/// chunked text (phrases and W/n proximities as whole spans, also across chunk boundaries and pages), Highlight Sets
/// (CRUD, HighlightSet.Manage, audit in the transaction, per-user toggles), search handles bound to their user, and a
/// text of several million characters highlighted page by page.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class TextHitsApiTests(MigrationPostgresFixture postgres)
{
    private const string Line = "Item: the price increase was approved, then price adjustments followed; termination of the agreement is pending (memo).\n";

    private readonly InMemoryAuditEventWriter _audit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_multi_million_character_text_highlights_page_by_page_with_whole_phrase_and_proximity_spans()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var lines = (5 * 1024 * 1024 / Line.Length) + 1;
        var text = string.Concat(Enumerable.Repeat(Line, lines));
        var documentId = await db.ArtifactDocumentAsync(ws, Encoding.UTF8.GetBytes(text));
        var searchId = await SearchAsync(db, ws, admin, "(\"price increase\" OR termination W/3 agreement) AND NOT memo");
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var set = await JsonAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/highlight-sets", admin, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Key terms", ["color"] = "amber", ["terms"] = new JsonArray(new JsonObject { ["expression"] = "terminat*" }) });
        var setId = set["highlightSetId"]!.GetValue<Guid>();
        _audit.Clear();

        var basePath = $"/api/v1/workspaces/{ws}/documents/{documentId}";
        var clock = Stopwatch.StartNew();
        var hits = new List<(int Unit, int Chunk, int Start, int End)>();
        var counts = new Dictionary<int, long>();
        JsonArray? units = null;
        int? from = 0;
        var pages = 0;
        while (from is { } f)
        {
            var page = await JsonAsync(client, HttpMethod.Get, $"{basePath}/text/hits?searchId={searchId:N}&highlightSetId={setId}&fromChunk={f}", admin,
                HttpStatusCode.OK);
            pages++;
            page["fromChunk"]!.GetValue<int>().Should().Be(f);
            units = page["units"]!.AsArray();
            foreach (var unit in units)
            {
                var u = unit!["unit"]!.GetValue<int>();
                counts[u] = counts.GetValueOrDefault(u) + unit["count"]!.GetValue<long>();
            }

            hits.AddRange(page["hits"]!.AsArray().Select(h =>
                (h!["unit"]!.GetValue<int>(), h["chunk"]!.GetValue<int>(), h["start"]!.GetValue<int>(), h["end"]!.GetValue<int>())));
            from = page["nextChunk"]?.GetValue<int>();
        }

        clock.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"{text.Length:N0} characters, {pages} pages, {hits.Count:N0} hits: {clock.Elapsed.TotalMilliseconds:F0} ms");
        text.Length.Should().BeGreaterThan(1_000_000);
        pages.Should().BeGreaterThan(1, "a page covers at most 16 chunks or about 5,000 hits");

        // The units: the search's phrase and proximity (NOT memo is not highlighted), then the set's wildcard.
        units!.Select(u => (u!["label"]!.GetValue<string>(), u["kind"]!.GetValue<string>(), u["source"]!.GetValue<string>(), u["color"]?.GetValue<string>()))
            .Should().Equal(
                ("price increase", "phrase", "search", null),
                ("termination W/3 agreement", "proximity", "search", null),
                ("terminat*", "wildcard", "highlightSet", "amber"));
        counts.Should().BeEquivalentTo(new Dictionary<int, long> { [0] = lines, [1] = lines, [2] = lines });
        hits.Should().HaveCount(3 * lines);

        // Map chunk-local offsets back to the text through the chunks the viewer loads.
        var starts = new List<int>();
        var assembled = new StringBuilder();
        for (var n = 0; ; n++)
        {
            var chunk = await JsonAsync(client, HttpMethod.Get, $"{basePath}/text/chunks/{n}?purpose=prefetch", admin, HttpStatusCode.OK);
            starts.Add(assembled.Length);
            assembled.Append(chunk["text"]!.GetValue<string>());
            if (chunk["isLast"]!.GetValue<bool>())
            {
                break;
            }
        }

        assembled.ToString().Should().Be(text);
        var spans = hits.GroupBy(h => h.Unit).ToDictionary(g => g.Key, g => g.Select(h => text[(starts[h.Chunk] + h.Start)..(starts[h.Chunk] + h.End)]).Distinct().ToList());
        spans[0].Should().Equal(["price increase"], "a phrase is one span and never marks its words alone (price adjustments)");
        spans[1].Should().Equal(["termination of the agreement"], "a proximity hit spans from the first to the last word");
        spans[2].Should().Equal(["termination"]);
        hits.Select(h => (long)starts[h.Chunk] + h.Start).Should().BeInAscendingOrder();
        hits.Should().Contain(h => h.Chunk + 1 < starts.Count && h.End > starts[h.Chunk + 1] - starts[h.Chunk],
            "some spans run across a chunk boundary and are still whole");

        // One Document.Retrieved per page, as a text retrieval for term hits.
        _audit.Events.Where(e => e.Details.ContainsKey("use")).Should().HaveCount(pages).And.OnlyContain(e =>
            e.Category == "Document" && e.Action == "Retrieved" && e.Details["rendition"] == "Text" && e.Details["use"] == "termHits");
    }

    [Fact]
    public async Task Highlight_sets_need_HighlightSet_Manage_are_validated_versioned_audited_and_toggled_per_user()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var admin = await Member(db, ws, WorkspaceRole.WorkspaceAdmin);
        var reviewer = await Member(db, ws, WorkspaceRole.Reviewer);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var path = $"/api/v1/workspaces/{ws}/highlight-sets";
        var body = new JsonObject
        {
            ["name"] = "Key terms",
            ["color"] = "amber",
            ["terms"] = new JsonArray(
                new JsonObject { ["expression"] = "termination" },
                new JsonObject { ["expression"] = "price increase", ["color"] = "rose" },
                new JsonObject { ["expression"] = "agreement W/5 terminat*" }),
        };

        (await SendAsync(client, HttpMethod.Post, path, reviewer, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var invalid = await SendAsync(client, HttpMethod.Post, path, admin, new JsonObject
        {
            ["name"] = "Bad",
            ["color"] = "orange",
            ["terms"] = new JsonArray(new JsonObject { ["expression"] = "te*" }, new JsonObject { ["expression"] = "NOT x" }),
        });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = JsonNode.Parse(await invalid.Content.ReadAsStringAsync(Ct))!["errors"]!.AsObject();
        problem.Select(p => p.Key).Should().BeEquivalentTo(["color", "terms[0].expression", "terms[1].expression"]);

        var created = await JsonAsync(client, HttpMethod.Post, path, admin, HttpStatusCode.Created, body);
        var id = created["highlightSetId"]!.GetValue<Guid>();
        created["terms"]!.AsArray().Select(t => t!["color"]?.GetValue<string>()).Should().Equal(null, "rose", null);
        (await SendAsync(client, HttpMethod.Post, path, admin, body)).StatusCode.Should().Be(HttpStatusCode.Conflict, "names are unique");

        var list = await JsonAsync(client, HttpMethod.Get, path, reviewer, HttpStatusCode.OK);
        list["items"]!.AsArray().Should().ContainSingle();
        list["colors"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Contain("amber");

        var termId = created["terms"]![0]!["termId"]!.GetValue<Guid>();
        var update = new JsonObject
        {
            ["name"] = "Key terms v2",
            ["color"] = "blue",
            ["terms"] = new JsonArray(new JsonObject { ["expression"] = "termination", ["termId"] = termId.ToString() }),
        };
        (await SendAsync(client, HttpMethod.Put, $"{path}/{id}", admin, update)).StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        (await SendAsync(client, HttpMethod.Put, $"{path}/{id}", admin, update, "\"5\"")).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await SendAsync(client, HttpMethod.Put, $"{path}/{id}", reviewer, update, "\"1\"")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var updated = await SendAsync(client, HttpMethod.Put, $"{path}/{id}", admin, update, "\"1\"");
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedBody = JsonNode.Parse(await updated.Content.ReadAsStringAsync(Ct))!;
        updatedBody["version"]!.GetValue<long>().Should().Be(2);
        updatedBody["terms"]![0]!["termId"]!.GetValue<Guid>().Should().Be(termId, "a term keeps its identity across edits");

        // Toggles are per user: everything is on until the reviewer switches a set off; unknown IDs are dropped.
        var selection = $"/api/v1/workspaces/{ws}/highlight-set-selection";
        (await JsonAsync(client, HttpMethod.Get, selection, reviewer, HttpStatusCode.OK)).ToJsonString()
            .Should().Be("""{"disabledSetIds":[],"searchHits":true}""");
        var stored = await JsonAsync(client, HttpMethod.Put, selection, reviewer, HttpStatusCode.OK,
            new JsonObject { ["disabledSetIds"] = new JsonArray(id.ToString(), Guid.NewGuid().ToString()), ["searchHits"] = false });
        stored.ToJsonString().Should().Be($$"""{"disabledSetIds":["{{id}}"],"searchHits":false}""");
        (await JsonAsync(client, HttpMethod.Get, selection, admin, HttpStatusCode.OK))["disabledSetIds"]!.AsArray().Should().BeEmpty();

        (await SendAsync(client, HttpMethod.Delete, $"{path}/{id}", admin, null, "\"2\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(client, HttpMethod.Get, $"{path}/{id}", admin)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var actions = await db.Security.Core.ColumnAsync(
            $"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND resource_id = '{id}' AND category = 'Search' ORDER BY occurred_at");
        actions.Should().Equal("HighlightSet.Created", "HighlightSet.Modified", "HighlightSet.Deleted");
        (await db.Security.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND action = 'HighlightSet.Created' AND restricted_details->>'terms' = @terms AND details->>'termCount' = '3'",
            ("ws", ws), ("terms", "termination\nprice increase\nagreement W/5 terminat*"))).Should().Be(1);
    }

    [Fact]
    public async Task Search_handles_are_bound_to_their_user_and_unknown_sets_or_texts_are_answered_plainly()
    {
        await using var db = await ContentDatabase.CreateAsync(postgres);
        var ws = await db.Security.Core.CreateWorkspaceAsync();
        var owner = await Member(db, ws, WorkspaceRole.Reviewer);
        var colleague = await Member(db, ws, WorkspaceRole.Reviewer);
        var documentId = await db.ArtifactDocumentAsync(ws, Encoding.UTF8.GetBytes("A memo about the price increase."));
        var noText = await db.ArtifactDocumentAsync(ws, null);
        var searchId = await SearchAsync(db, ws, owner, "memo OR \"price increase\"");
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var basePath = $"/api/v1/workspaces/{ws}/documents/{documentId}/text/hits";

        var own = await JsonAsync(client, HttpMethod.Get, $"{basePath}?searchId={searchId:N}", owner, HttpStatusCode.OK);
        own["hits"]!.AsArray().Select(h => (h!["start"]!.GetValue<int>(), h["end"]!.GetValue<int>())).Should().Equal((2, 6), (17, 31));
        own["nextChunk"].Should().BeNull();

        _audit.Clear();
        var replayed = await SendAsync(client, HttpMethod.Get, $"{basePath}?searchId={searchId:N}", colleague);
        var unknown = await SendAsync(client, HttpMethod.Get, $"{basePath}?searchId={Guid.NewGuid():N}", colleague);
        replayed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _audit.Events.Should().ContainSingle(e => e.Category == "AuthZ" && e.ReasonCode == "SearchHandleMismatch", "the replayed handle is audited");
        (await SendAsync(client, HttpMethod.Get, $"{basePath}?highlightSetId={Guid.NewGuid()}", owner)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(client, HttpMethod.Get, $"{basePath}?fromChunk=-1", owner)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(client, HttpMethod.Get, $"{basePath}?fromChunk=7", owner)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var missing = await JsonAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/documents/{noText}/text/hits?searchId={searchId:N}", owner,
            HttpStatusCode.OK);
        missing["missing"]!.GetValue<bool>().Should().BeTrue();
        missing["hits"]!.AsArray().Should().BeEmpty();
    }

    private static async Task<Guid> SearchAsync(ContentDatabase db, Guid ws, Guid user, string query)
    {
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await new SearchSessionStore(db.Security.Core.AppDataSource).CreateAsync(
            new SearchSessionRecord(ws, id, user, null, query, "[]", 50, false, true, null, 0, true, now, now.AddMinutes(30)), [], Ct);
        return id;
    }

    private static async Task<Guid> Member(ContentDatabase db, Guid ws, WorkspaceRole role)
    {
        var user = await db.Security.CreateUserAsync();
        await db.Security.AssignAsync(ws, role, user);
        return user;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> JsonAsync(HttpClient client, HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null)
    {
        using var response = await SendAsync(client, method, url, user, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!;
    }

    private WebApplicationFactory<Program> Factory(ContentDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Security.Core.AppConnectionString);
            builder.UseSetting("ObjectStorage:Provider", "FileSystem");
            builder.UseSetting("ObjectStorage:FileSystem:RootPath", db.StoreRoot);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
                services.AddSingleton<IAuditEventWriter>(_audit);
            });
        });
}
