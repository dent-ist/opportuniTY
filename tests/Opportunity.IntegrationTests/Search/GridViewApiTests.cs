using System.Globalization;
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
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E16-T09 through the real API host, PostgreSQL PDP and audit store: personal and shared document-list views (shared
/// ones need View.ManageShared and are audited), the per-user, per-workspace layout, and searches that sort by up to three
/// workspace fields with a deterministic Control Number tie-break and return the values of the grid's columns.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class GridViewApiTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Views_are_personal_or_shared_and_shared_views_need_the_manage_views_permission()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var reviewer = await h.MemberAsync(ws);
        var other = await h.MemberAsync(ws);
        var admin = await h.MemberAsync(ws, WorkspaceRole.WorkspaceAdmin);
        var catalog = await CatalogAsync(h, ws);
        await using var api = Api(h);
        var views = $"/api/v1/workspaces/{ws}/grid-views";

        // A personal view: columns with width and pin, sort by a workspace field (canonical query name) and a fixed one.
        JsonObject Body(string name, string? visibility = null, params (string Field, string Direction)[] sort)
        {
            var body = new JsonObject
            {
                ["name"] = name,
                ["columns"] = new JsonArray(
                    new JsonObject { ["field"] = catalog.Custodian, ["width"] = 180, ["pinned"] = true },
                    new JsonObject { ["field"] = catalog.Responsiveness },
                    new JsonObject { ["field"] = "datesent" }),
                ["sort"] = new JsonArray([.. sort.Select(s => (JsonNode)new JsonObject { ["field"] = s.Field, ["direction"] = s.Direction })]),
            };
            if (visibility is not null)
            {
                body["visibility"] = visibility;
            }

            return body;
        }

        using (var created = await api.SendAsync(HttpMethod.Post, views, reviewer,
            Body("My review columns", null, (catalog.Custodian.ToUpperInvariant(), "desc"), ("documentDate", "asc"))))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            created.Headers.ETag!.Tag.Should().Be("\"1\"");
        }

        var mine = (await api.JsonAsync(HttpMethod.Get, views, reviewer, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        var personalId = mine.GetProperty("viewId").GetGuid();
        mine.GetProperty("visibility").GetString().Should().Be("personal");
        mine.GetProperty("canEdit").GetBoolean().Should().BeTrue();
        mine.GetProperty("columns")[0].GetProperty("width").GetInt32().Should().Be(180);
        mine.GetProperty("columns")[0].GetProperty("pinned").GetBoolean().Should().BeTrue();
        mine.GetProperty("sort").EnumerateArray().Select(s => s.GetProperty("field").GetString()).Should().Equal(catalog.Custodian, "documentDate");

        // Sort rules: at most 3 fields, and only sortable ones (a choice field is not, ADR-007 R8).
        await (await api.SendAsync(HttpMethod.Post, views, reviewer, Body("Too many", null,
            ("controlNumber", "asc"), ("fileName", "asc"), ("fileType", "asc"), ("fileSize", "asc")))).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await api.SendAsync(HttpMethod.Post, views, reviewer, Body("Choice sort", null, (catalog.Responsiveness, "asc"))))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await api.SendAsync(HttpMethod.Post, views, reviewer, Body("my REVIEW columns")))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");

        // Shared views need View.ManageShared (a Reviewer lacks it), also to make an own view shared.
        await (await api.SendAsync(HttpMethod.Post, views, reviewer, Body("Team view", "shared"))).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await api.SendAsync(HttpMethod.Put, $"{views}/{personalId}", reviewer, Body("My review columns", "shared"), ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Personal views are invisible to others (404, never 403).
        (await api.JsonAsync(HttpMethod.Get, views, other, HttpStatusCode.OK)).GetProperty("items").GetArrayLength().Should().Be(0);
        await (await api.SendAsync(HttpMethod.Get, $"{views}/{personalId}", other)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await api.SendAsync(HttpMethod.Delete, $"{views}/{personalId}", other, ifMatch: "*")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // A Workspace Admin (who holds View.ManageShared) shares a view with the workspace.
        var shared = await api.JsonAsync(HttpMethod.Post, views, admin, HttpStatusCode.Created, Body("First pass", "shared", ("fileName", "asc")));
        var sharedId = shared.GetProperty("viewId").GetGuid();
        await (await api.SendAsync(HttpMethod.Post, views, admin, Body("FIRST PASS", "shared"))).ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        (await api.JsonAsync(HttpMethod.Post, views, admin, HttpStatusCode.Created, Body("First pass")))
            .GetProperty("visibility").GetString().Should().Be("personal", "personal names are separate from shared ones");

        var seen = (await api.JsonAsync(HttpMethod.Get, views, other, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        seen.GetProperty("viewId").GetGuid().Should().Be(sharedId);
        seen.GetProperty("canEdit").GetBoolean().Should().BeFalse();
        await (await api.SendAsync(HttpMethod.Put, $"{views}/{sharedId}", other, Body("Renamed", "shared"), ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await api.SendAsync(HttpMethod.Delete, $"{views}/{sharedId}", other, ifMatch: "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Admins see every view, other users' personal ones included, and may change them.
        var all = (await api.JsonAsync(HttpMethod.Get, views, admin, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().ToList();
        all.Select(v => v.GetProperty("visibility").GetString()).Should().Equal("shared", "personal", "personal");
        all.Should().Contain(v => v.GetProperty("viewId").GetGuid() == personalId && v.GetProperty("canEdit").GetBoolean());

        // Changes need If-Match.
        await (await api.SendAsync(HttpMethod.Put, $"{views}/{sharedId}", admin, Body("First pass review", "shared")))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await api.SendAsync(HttpMethod.Put, $"{views}/{sharedId}", admin, Body("First pass review", "shared"), ifMatch: "\"7\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        (await api.JsonAsync(HttpMethod.Put, $"{views}/{sharedId}", admin, HttpStatusCode.OK, Body("First pass review", "shared", ("fileSize", "desc")),
            ifMatch: "\"1\"")).GetProperty("version").GetInt64().Should().Be(2);
        (await api.SendAsync(HttpMethod.Delete, $"{views}/{sharedId}", admin, ifMatch: "\"2\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.SendAsync(HttpMethod.Delete, $"{views}/{personalId}", reviewer, ifMatch: "\"1\"")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Shared-view changes are audited in the transaction of the change (never the name); personal views are not.
        (await h.Db.Core.ColumnAsync(
            $"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND resource_id = '{sharedId}' ORDER BY occurred_at"))
            .Should().Equal("GridView.Created", "GridView.Modified", "GridView.Deleted");
        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND action = 'GridView.Modified' AND details->>'changed' = 'name,sort' AND details->>'sort' = 'fileSize:desc'",
            ("ws", ws))).Should().Be(1);
        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND (resource_id = @id OR details::text LIKE '%First pass%')",
            ("ws", ws), ("id", personalId.ToString()))).Should().Be(0);
    }

    [Fact]
    public async Task The_layout_persists_per_user_per_workspace_and_falls_back_to_the_default_view()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var otherWs = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        await h.Db.AssignAsync(otherWs, WorkspaceRole.Reviewer, user);
        var colleague = await h.MemberAsync(ws);
        await using var api = Api(h);
        var layout = $"/api/v1/workspaces/{ws}/grid-views/layout";

        var empty = await api.JsonAsync(HttpMethod.Get, layout, user, HttpStatusCode.OK);
        empty.GetProperty("viewId").ValueKind.Should().Be(JsonValueKind.Null);
        empty.GetProperty("modifiedAt").ValueKind.Should().Be(JsonValueKind.Null);

        var view = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/grid-views", user, HttpStatusCode.Created,
            new JsonObject { ["name"] = "Mine", ["columns"] = new JsonArray(new JsonObject { ["field"] = "filename" }) });
        var viewId = view.GetProperty("viewId").GetString();
        var saved = await api.JsonAsync(HttpMethod.Put, layout, user, HttpStatusCode.OK, new JsonObject
        {
            ["viewId"] = viewId,
            ["columns"] = new JsonArray(new JsonObject { ["field"] = "filename", ["width"] = 300 }, new JsonObject { ["field"] = "filesize" }),
            ["sort"] = new JsonArray(new JsonObject { ["field"] = "fileSize", ["direction"] = "desc" }),
        });
        saved.GetProperty("viewId").GetString().Should().Be(viewId);

        var read = await api.JsonAsync(HttpMethod.Get, layout, user, HttpStatusCode.OK);
        read.GetProperty("viewId").GetString().Should().Be(viewId);
        read.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("field").GetString()).Should().Equal("filename", "filesize");
        read.GetProperty("columns")[0].GetProperty("width").GetInt32().Should().Be(300);
        read.GetProperty("sort")[0].GetProperty("direction").GetString().Should().Be("desc");

        // Per user and per workspace.
        (await api.JsonAsync(HttpMethod.Get, layout, colleague, HttpStatusCode.OK)).GetProperty("columns").ValueKind.Should().Be(JsonValueKind.Null);
        (await api.JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{otherWs}/grid-views/layout", user, HttpStatusCode.OK))
            .GetProperty("columns").ValueKind.Should().Be(JsonValueKind.Null);

        // A view the caller cannot see is not a layout; invalid sort is rejected.
        await (await api.SendAsync(HttpMethod.Put, layout, colleague, new JsonObject { ["viewId"] = viewId }))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await api.SendAsync(HttpMethod.Put, layout, user, new JsonObject
        {
            ["sort"] = new JsonArray(new JsonObject { ["field"] = "begattach", ["direction"] = "asc" }),
        })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // The view is deleted: the layout falls back to the Default view and keeps the adjustments.
        (await api.SendAsync(HttpMethod.Delete, $"/api/v1/workspaces/{ws}/grid-views/{viewId}", user, ifMatch: "*")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        var fallback = await api.JsonAsync(HttpMethod.Get, layout, user, HttpStatusCode.OK);
        fallback.GetProperty("viewId").ValueKind.Should().Be(JsonValueKind.Null);
        fallback.GetProperty("columns").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Searches_sort_by_up_to_three_fields_break_ties_by_control_number_and_return_column_values()
    {
        await using var h = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await h.WorkspaceAsync();
        var user = await h.MemberAsync(ws);
        var catalog = await CatalogAsync(h, ws);
        await using var api = Api(h);

        // Inserted out of order; custodian and file type tie for every "same" document.
        foreach (var (control, custodian, responsive) in new[]
        {
            ("GV-0010", "Smith", true), ("GV-0002", "Smith", false), ("GV-0100", "Smith", true), ("GV-0001", "Adams", true),
            ("GV-0020", "Smith", false),
        })
        {
            var id = await h.Db.DocumentAsync(ws);
            var row = new JsonObject
            {
                ["workspaceId"] = ws.ToString("D"),
                ["documentId"] = id.ToString("D"),
                ["controlNumber"] = control,
                ["controlNumberSort"] = ControlNumber.SortKey(control),
                ["fileName"] = control + ".msg",
                ["fileType"] = "Email",
                ["dateSent"] = "2024-03-01T10:00:00Z",
                ["securityTags"] = new JsonArray(),
                ["text"] = "grid",
                ["metadata"] = new JsonObject { [catalog.CustodianSlot[..2]] = new JsonObject { [catalog.CustodianSlot[3..]] = custodian } },
                ["coding"] = new JsonObject
                {
                    ["ch"] = new JsonObject
                    {
                        [catalog.ResponsivenessSlot[3..]] = (responsive ? catalog.Responsive : catalog.NotResponsive).ToString(CultureInfo.InvariantCulture),
                    },
                },
            };
            await h.ProjectAsync(ws, row, id);
        }

        var searches = $"/api/v1/workspaces/{ws}/searches";
        var page = await api.JsonAsync(HttpMethod.Post, searches, user, HttpStatusCode.OK, new JsonObject
        {
            ["query"] = "",
            ["pageSize"] = 3,
            ["sort"] = new JsonArray(
                new JsonObject { ["field"] = catalog.Custodian, ["direction"] = "desc" },
                new JsonObject { ["field"] = "fileType", ["direction"] = "asc" },
                new JsonObject { ["field"] = "datesent", ["direction"] = "asc" }),
            ["fields"] = new JsonArray(catalog.Custodian, catalog.Responsiveness, "datesent", "nosuchfield"),
        });
        var first = page.GetProperty("items").EnumerateArray().ToList();
        first.Select(i => i.GetProperty("controlNumber").GetString()).Should().Equal(["GV-0002", "GV-0010", "GV-0020"],
            "equal sort values come back in Control Number order");
        var fields = first[0].GetProperty("fields");
        fields.GetProperty(catalog.Custodian)[0].GetString().Should().Be("Smith");
        fields.GetProperty(catalog.Responsiveness)[0].GetString().Should().Be(catalog.NotResponsive.ToString(CultureInfo.InvariantCulture));
        fields.GetProperty("datesent")[0].GetString().Should().Be("2024-03-01T10:00:00Z");
        fields.TryGetProperty("nosuchfield", out _).Should().BeFalse("unknown fields are ignored");

        // The next page keeps the sort, the tie-break and the fields.
        var next = await api.JsonAsync(HttpMethod.Get,
            $"{searches}/{page.GetProperty("searchId").GetString()}/pages?cursor={page.GetProperty("nextCursor").GetString()}", user, HttpStatusCode.OK);
        var second = next.GetProperty("items").EnumerateArray().ToList();
        second.Select(i => i.GetProperty("controlNumber").GetString()).Should().Equal("GV-0100", "GV-0001");
        second[1].GetProperty("fields").GetProperty(catalog.Custodian)[0].GetString().Should().Be("Adams");

        // Choice fields are not sortable; without fields asked for, hits carry none.
        await (await api.SendAsync(HttpMethod.Post, searches, user, new JsonObject
        {
            ["query"] = "",
            ["sort"] = new JsonArray(new JsonObject { ["field"] = catalog.Responsiveness, ["direction"] = "asc" }),
        })).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        var plain = await api.JsonAsync(HttpMethod.Post, searches, user, HttpStatusCode.OK, new JsonObject { ["query"] = "" });
        plain.GetProperty("items")[0].GetProperty("fields").ValueKind.Should().Be(JsonValueKind.Null);

        // A saved search keeps the view's columns and a workspace-field sort, and runs with that sort.
        var saved = await api.JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", user, HttpStatusCode.Created, new JsonObject
        {
            ["name"] = "Custodians",
            ["query"] = "",
            ["columns"] = new JsonArray(catalog.Custodian, catalog.Responsiveness),
            ["sort"] = new JsonArray(new JsonObject { ["field"] = catalog.Custodian, ["direction"] = "asc" }),
        });
        saved.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).Should().Equal(catalog.Custodian, catalog.Responsiveness);
        var run = await api.JsonAsync(HttpMethod.Post, searches, user, HttpStatusCode.OK, new JsonObject
        {
            ["savedSearchId"] = saved.GetProperty("savedSearchId").GetString(),
            ["fields"] = new JsonArray(catalog.Custodian),
        });
        run.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("controlNumber").GetString())
            .Should().Equal("GV-0001", "GV-0002", "GV-0010", "GV-0020", "GV-0100");
    }

    private sealed record Catalog(string Custodian, string CustodianSlot, string Responsiveness, string ResponsivenessSlot, int Responsive, int NotResponsive);

    private static async Task<Catalog> CatalogAsync(SearchHarness h, Guid ws)
    {
        await h.Fields.InitializeWorkspaceAsync(ws, Ct);
        var custodian = await FieldAsync(h, new NewField(ws, "Grid Custodian", FieldType.Keyword, FieldStorage.Metadata));
        var responsiveness = await FieldAsync(h, new NewField(ws, "Grid Responsiveness", FieldType.SingleChoice, FieldStorage.Coding));
        var responsive = (await h.Fields.AddChoiceAsync(ws, responsiveness.FieldId, "Responsive", Ct)).Value!;
        var notResponsive = (await h.Fields.AddChoiceAsync(ws, responsiveness.FieldId, "Not Responsive", Ct)).Value!;
        var names = FieldQueryNames.Assign(await h.Fields.GetCatalogAsync(ws, includeDeleted: false, Ct) is { } c ? c.Fields : []);
        return new Catalog(names[custodian.FieldId], custodian.SearchSlot!, names[responsiveness.FieldId], responsiveness.SearchSlot!,
            responsive.ChoiceId, notResponsive.ChoiceId);
    }

    private static async Task<FieldDefinition> FieldAsync(SearchHarness h, NewField field)
    {
        var result = await h.Fields.CreateFieldAsync(field, Ct);
        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Value!;
    }

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

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid user, JsonNode? body = null, string? ifMatch = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
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

        public async Task<JsonElement> JsonAsync(HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null, string? ifMatch = null)
        {
            using var response = await SendAsync(method, url, user, body, ifMatch);
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
