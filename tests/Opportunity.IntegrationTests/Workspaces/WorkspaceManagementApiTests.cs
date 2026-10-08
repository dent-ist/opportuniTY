using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// E04-T05 through the real API host, the PostgreSQL PDP (RLS-bound app login) and the PostgreSQL audit store: list,
/// create, get, update and members, each with the non-member, walled, wrong-role and anonymous cases.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class WorkspaceManagementApiTests(MigrationPostgresFixture postgres)
{
    private const string AdminGroup = "cn=opp-installation-admins";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_list_shows_only_the_callers_workspaces_with_a_bound_cursor()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var user = await db.CreateUserAsync();
        var outsider = await db.CreateUserAsync();
        var mine = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var ws = await db.Core.CreateWorkspaceAsync();
            await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);
            mine.Add(ws);
        }

        var walled = await db.Core.CreateWorkspaceAsync();
        await db.AssignGroupAsync(walled, WorkspaceRole.Reviewer, "cn=review");
        await db.WallAsync(walled, [user], [], [await db.DocumentAsync(walled)]);
        mine.Add(walled);
        var notMine = await db.Core.CreateWorkspaceAsync();
        await db.AssignAsync(notMine, WorkspaceRole.WorkspaceAdmin, outsider);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        using var first = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/workspaces?limit=3", user, "cn=review"));
        first.RootElement.GetProperty("total").GetProperty("value").GetInt64().Should().Be(4);
        first.RootElement.GetProperty("total").GetProperty("relation").GetString().Should().Be("eq");
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();
        using var second = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces?limit=3&cursor={cursor}", user, "cn=review"));
        second.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        var listed = Ids(first).Concat(Ids(second)).ToList();
        listed.Should().BeEquivalentTo(mine).And.NotContain(notMine);
        var item = first.RootElement.GetProperty("items")[0];
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["workspaceId", "name", "matterNumber", "displayTimeZone", "status", "createdAt"]);

        await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces?cursor={cursor}", outsider))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Get, "/api/v1/workspaces?cursor=not-a-cursor", user))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        using var outsiderList = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/workspaces", outsider));
        Ids(outsiderList).Should().Equal(notMine);
        (await SendAsync(client, HttpMethod.Get, "/api/v1/workspaces", user: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Every listed workspace opens; the walled one too (walls hide documents, Q-59).
        foreach (var ws in listed)
        {
            (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", user, "cn=review")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Installation_admins_create_workspaces_with_mfa_and_become_their_admin()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var admin = await db.CreateUserAsync();
        var plain = await db.CreateUserAsync();
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        const string body = """{"name":"  Acme v. Widget ","matterNumber":"2026-001","displayTimeZone":"Europe/Berlin"}""";

        await (await SendAsync(client, HttpMethod.Post, "/api/v1/workspaces", plain, "cn=review", body: body, mfa: true))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, "/api/v1/workspaces", admin, AdminGroup, body: body))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "step-up-required");
        await (await SendAsync(client, HttpMethod.Post, "/api/v1/workspaces", admin, AdminGroup, mfa: true,
                body: """{"name":"X","displayTimeZone":"Mars/Olympus"}"""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, "/api/v1/workspaces", admin, AdminGroup, mfa: true,
                body: """{"name":"X","displayTimeZone":"UTC","storageProfile":"unknown"}"""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        using var response = await SendAsync(client, HttpMethod.Post, "/api/v1/workspaces", admin, AdminGroup, body: body, mfa: true);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        using var created = await JsonAsync(response);
        var ws = created.RootElement.GetProperty("workspaceId").GetGuid();
        response.Headers.Location!.ToString().Should().Be($"/api/v1/workspaces/{ws}");
        created.RootElement.GetProperty("name").GetString().Should().Be("Acme v. Widget");
        created.RootElement.GetProperty("storageProfile").GetString().Should().Be("default");
        Permissions(created).Should().BeEquivalentTo(RoleCatalog.Get(WorkspaceRole.WorkspaceAdmin).Grants.Select(p => p.Name()));

        using var list = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/workspaces", admin));
        Ids(list).Should().Equal(ws);

        // Provisioned for loading: the system fields exist, so an import can map Control Number at once.
        using var fields = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/fields?limit=200", admin));
        fields.RootElement.GetProperty("items").EnumerateArray().Select(f => f.GetProperty("queryName").GetString())
            .Should().Contain(["controlnumber", "filename", "date"]);
        (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", plain)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // E13-T01: the privilege system fields and the default workspace template (guide §3.4).
        var byName = fields.RootElement.GetProperty("items").EnumerateArray().ToDictionary(f => f.GetProperty("displayName").GetString()!);
        byName.Keys.Should().Contain(["Privilege Status", "Privilege Basis", "Privilege Description", "Attorneys Involved", "Log Category",
            "Responsiveness", "Confidentiality Designation", "Issues", "Key Document", "Reviewer Comments"]);
        var status = byName["Privilege Status"];
        status.GetProperty("fieldId").GetInt32().Should().Be(Core.Fields.PrivilegeFields.Status);
        status.GetProperty("isSystem").GetBoolean().Should().BeTrue();
        status.GetProperty("isSecurityAffecting").GetBoolean().Should().BeTrue();
        var statusChoices = status.GetProperty("choices").EnumerateArray()
            .ToDictionary(c => c.GetProperty("systemKey").GetString()!, c => c.GetProperty("choiceId").GetInt32());
        statusChoices.Keys.Should().Equal(Core.Fields.PrivilegeFields.Keys.NotPrivileged, Core.Fields.PrivilegeFields.Keys.Withhold,
            Core.Fields.PrivilegeFields.Keys.Redact, Core.Fields.PrivilegeFields.Keys.NeedsSecondLevelReview);
        byName["Responsiveness"].GetProperty("isSystem").GetBoolean().Should().BeFalse("template fields are ordinary custom fields");
        byName["Confidentiality Designation"].GetProperty("isSecurityAffecting").GetBoolean().Should().BeTrue();

        using var layouts = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/coding-layouts", admin));
        var layoutItems = layouts.RootElement.GetProperty("items").EnumerateArray().ToList();
        layoutItems.Select(l => (l.GetProperty("name").GetString(), l.GetProperty("isDefault").GetBoolean()))
            .Should().Equal(("First Pass Review", true), ("Privilege Review", false));
        var basis = layoutItems[0].GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("fieldId").GetInt32() == Core.Fields.PrivilegeFields.Basis);
        basis.GetProperty("isRequired").GetBoolean().Should().BeTrue();
        basis.GetProperty("visibleWhen").GetProperty("fieldId").GetInt32().Should().Be(Core.Fields.PrivilegeFields.Status);
        basis.GetProperty("visibleWhen").GetProperty("choiceIds").EnumerateArray().Select(c => c.GetInt32())
            .Should().BeEquivalentTo([statusChoices[Core.Fields.PrivilegeFields.Keys.Withhold], statusChoices[Core.Fields.PrivilegeFields.Keys.Redact]]);
        layoutItems[1].GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("fields").EnumerateArray())
            .Select(f => f.GetProperty("fieldId").GetInt32()).Should().HaveCount(6, "all five privilege fields plus Confidentiality Designation");

        var audit = await db.Core.ColumnAsync(
            """
            SELECT concat_ws('|', coalesce(workspace_id::text, 'system'), category, action, actor_id, outcome, coalesce(reason_code, ''), coalesce(details->>'permission', ''))
            FROM audit.audit_event WHERE category IN ('Workspace', 'Security') OR (category = 'AuthZ' AND resource_type = 'Installation')
            """);
        audit.Should().BeEquivalentTo(
            [
                $"system|AuthZ|Denied|{plain}|Denied|PermissionNotGranted|Installation.ManageWorkspaces",
                $"{ws}|Workspace|Created|{admin}|Success||",
                $"{ws}|Security|RoleAssigned|{admin}|Success||",
            ]);
    }

    [Fact]
    public async Task The_current_user_lists_the_installation_permissions_the_app_offers_actions_for()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var admin = await db.CreateUserAsync();
        var plain = await db.CreateUserAsync();
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        using var adminMe = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/me", admin, AdminGroup));
        adminMe.RootElement.GetProperty("installationPermissions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal("Installation.ManageWorkspaces", "Installation.AssignBreakGlass");
        using var plainMe = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/me", plain, "cn=review"));
        plainMe.RootElement.GetProperty("installationPermissions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Settings_updates_need_the_permission_and_the_current_etag_and_are_audited()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        var walledAdmin = await db.CreateUserAsync();
        var outsider = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, walledAdmin);
        await db.WallAsync(ws, [walledAdmin], [], [await db.DocumentAsync(ws)]);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{ws}";
        const string body = """{"name":"Renamed matter","matterNumber":"","displayTimeZone":"Asia/Tokyo","storageProfile":"archive"}""";

        using var get = await SendAsync(client, HttpMethod.Get, url, admin);
        get.Headers.ETag!.Tag.Should().Be("\"1\"");

        await (await SendAsync(client, HttpMethod.Put, url, outsider, body: body, ifMatch: "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await SendAsync(client, HttpMethod.Put, url, reviewer, body: body, ifMatch: "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Put, url, admin, body: body)).ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await SendAsync(client, HttpMethod.Put, url, admin, body: body, ifMatch: "\"9\"")).ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");

        using var updated = await SendAsync(client, HttpMethod.Put, url, admin, body: body, ifMatch: "\"1\"");
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        updated.Headers.ETag!.Tag.Should().Be("\"2\"");
        using (var json = await JsonAsync(updated))
        {
            json.RootElement.GetProperty("name").GetString().Should().Be("Renamed matter");
            json.RootElement.GetProperty("matterNumber").ValueKind.Should().Be(JsonValueKind.Null);
            json.RootElement.GetProperty("displayTimeZone").GetString().Should().Be("Asia/Tokyo");
            json.RootElement.GetProperty("storageProfile").GetString().Should().Be("archive");
            json.RootElement.GetProperty("version").GetInt64().Should().Be(2);
        }

        // A wall hides documents only: a walled Workspace Admin keeps workspace administration (Q-59).
        (await SendAsync(client, HttpMethod.Put, url, walledAdmin, ifMatch: "\"2\"",
            body: """{"name":"Renamed again","displayTimeZone":"Asia/Tokyo"}""")).StatusCode.Should().Be(HttpStatusCode.OK);

        await db.Core.InsertStoredObjectAsync(ws, documentId: null);
        await (await SendAsync(client, HttpMethod.Put, url, admin, ifMatch: "\"3\"",
                body: """{"name":"Renamed again","displayTimeZone":"Asia/Tokyo","storageProfile":"default"}"""))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");

        var audit = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', category, action, actor_id, outcome, coalesce(reason_code, ''), coalesce(details->>'changed', details->>'permission'))
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND category IN ('Workspace', 'AuthZ')
            """);
        audit.Should().BeEquivalentTo(
            [
                $"AuthZ|Denied|{outsider}|Denied|NotAMember|Workspace.ManageSecurity",
                $"AuthZ|Denied|{reviewer}|Denied|PermissionNotGranted|Workspace.ManageSecurity",
                $"Workspace|SettingsChanged|{admin}|Success||name,matterNumber,displayTimeZone,storageProfile",
                $"Workspace|SettingsChanged|{walledAdmin}|Success||name",
            ]);
    }

    [Fact]
    public async Task Members_are_listed_for_user_managers_only()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.AssignGroupAsync(ws, WorkspaceRole.Auditor, "cn=audit");
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var url = $"/api/v1/workspaces/{ws}/members";

        await (await SendAsync(client, HttpMethod.Get, url, reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Get, url, await db.CreateUserAsync())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        using var first = await JsonAsync(await SendAsync(client, HttpMethod.Get, url + "?limit=2", admin));
        first.RootElement.GetProperty("total").GetProperty("value").GetInt64().Should().Be(3);
        var cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        using var second = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"{url}?limit=2&cursor={cursor}", admin));
        var members = first.RootElement.GetProperty("items").EnumerateArray().Concat(second.RootElement.GetProperty("items").EnumerateArray())
            .Select(m => $"{m.GetProperty("kind").GetString()}|{m.GetProperty("role").GetString()}|{(m.GetProperty("userId").ValueKind == JsonValueKind.Null ? m.GetProperty("groupName").GetString() : m.GetProperty("userId").GetString())}")
            .ToList();
        // Pages follow the assignment id, not the order of assignment (ids minted in the same millisecond may sort
        // either way), so compare the membership as a set: every assignment exactly once across the two pages.
        members.Should().BeEquivalentTo([$"user|WorkspaceAdmin|{admin}", $"user|Reviewer|{reviewer}", "group|Auditor|cn=audit"]);

        // A cursor is bound to the workspace it was served for.
        var other = await db.Core.CreateWorkspaceAsync();
        await db.AssignAsync(other, WorkspaceRole.WorkspaceAdmin, admin);
        await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{other}/members?cursor={cursor}", admin))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Search_placement_is_read_only_and_never_names_an_index()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var placed = await db.Core.CreateWorkspaceAsync();
        var user = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);
        await db.AssignAsync(placed, WorkspaceRole.Reviewer, user);
        await db.Core.ExecuteAsync(
            "INSERT INTO opportunity.workspace_index_placement (workspace_id, kind, generation, primary_shards, state) VALUES (@ws, 2, 3, 1, 1)",
            ("ws", placed));
        await using var factory = Factory(db, openSearch: true);
        using var client = factory.CreateClient();

        using var unplaced = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", user));
        unplaced.RootElement.GetProperty("searchPlacement").ValueKind.Should().Be(JsonValueKind.Null);

        using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{placed}", user);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(raw);
        var placement = json.RootElement.GetProperty("searchPlacement");
        placement.GetProperty("kind").GetString().Should().Be("dedicated");
        placement.GetProperty("projectionGeneration").GetInt32().Should().Be(3);
        placement.GetProperty("state").GetString().Should().Be("active");
        raw.Should().NotContain("opp-").And.NotContain("-g3", "physical index and alias names stay inside Opportunity.Search");
    }

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db, bool openSearch = false) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.UseSetting("Authentication:Mfa:AmrValues:0", "mfa");
            builder.UseSetting("Authorization:InstallationAdminGroups:0", AdminGroup);
            builder.UseSetting("Workspaces:StorageProfiles:0", "default");
            builder.UseSetting("Workspaces:StorageProfiles:1", "archive");
            if (openSearch)
            {
                // Index management is registered, but reading a placement never contacts OpenSearch.
                builder.UseSetting("ConnectionStrings:OpenSearch", "http://opensearch.invalid:9200");
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid? user, string? groups = null, string? body = null, bool mfa = false, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (user is { } id)
        {
            request.Headers.Add(TestAuthentication.UserHeader, id.ToString());
            if (groups is not null)
            {
                request.Headers.Add(TestAuthentication.GroupsHeader, groups);
            }

            if (mfa)
            {
                request.Headers.Add(TestAuthentication.AmrHeader, "mfa");
            }
        }
        else
        {
            request.Headers.Add(TestAuthentication.AnonymousHeader, "1");
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.IsSuccessStatusCode.Should().BeTrue(text);
            return JsonDocument.Parse(text);
        }
    }

    private static List<Guid> Ids(JsonDocument page) =>
        [.. page.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("workspaceId").GetGuid())];

    private static List<string> Permissions(JsonDocument document) =>
        [.. document.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
}
