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
/// E05-T08 role administration through the real API host, the PostgreSQL PDP (RLS-bound app login) and the audit store:
/// the roles × permissions catalogue, the assignment matrix with its set version, replacing a user's or group's roles
/// (If-Match, same-transaction audit, effect on the very next request), self-protection, the last Workspace Admin, and
/// Break-glass (users only, Installation Admin only, removal ends a live activation).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class RoleAssignmentApiTests(MigrationPostgresFixture postgres)
{
    private const string AdminGroup = "cn=opp-installation-admins";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_catalogue_and_the_matrix_need_user_management_and_list_every_principal_once()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.Core.ExecuteAsync("UPDATE opportunity.app_user SET display_name = 'Avery Admin', email = 'avery@example.test' WHERE user_id = @id", ("id", admin));
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.AssignAsync(ws, WorkspaceRole.QcReviewer, reviewer);
        await db.AssignGroupAsync(ws, WorkspaceRole.Auditor, "cn=audit");
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        foreach (var path in new[] { "roles", "role-assignments", "role-assignments/candidates" })
        {
            await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/{path}", reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/{path}", await db.CreateUserAsync()))
                .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        }

        using (var roles = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/roles", admin)))
        {
            var catalogue = roles.RootElement.GetProperty("roles").EnumerateArray().ToDictionary(
                r => r.GetProperty("key").GetString()!, r => r.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList());
            catalogue.Keys.Should().Equal(RoleCatalog.All.Select(r => r.Key));
            foreach (var role in RoleCatalog.All)
            {
                catalogue[role.Key].Should().BeEquivalentTo(role.Grants.Select(p => p.Name()));
            }

            roles.RootElement.GetProperty("permissions").GetArrayLength().Should().Be(PermissionCatalog.All.Count);
            var breakGlass = roles.RootElement.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "BreakGlass");
            breakGlass.GetProperty("usersOnly").GetBoolean().Should().BeTrue();
            breakGlass.GetProperty("needsInstallationAdmin").GetBoolean().Should().BeTrue();
        }

        using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/role-assignments", admin, "cn=audit");
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        using var list = await JsonAsync(response);
        list.RootElement.GetProperty("version").GetInt64().Should().Be(1);
        list.RootElement.GetProperty("administratorPaths").GetInt32().Should().Be(1);
        list.RootElement.GetProperty("canAssignBreakGlass").GetBoolean().Should().BeFalse();
        Rows(list).Should().BeEquivalentTo(
            [
                $"user|{admin}|Avery Admin|WorkspaceAdmin|you",
                $"user|{reviewer}|{reviewer}|Reviewer,QcReviewer|",
                "group|cn=audit|cn=audit|Auditor|you",
            ]);
    }

    [Fact]
    public async Task Replacing_roles_needs_the_set_version_is_audited_and_applies_to_the_next_request()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var newcomer = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var users = $"/api/v1/workspaces/{ws}/role-assignments/users";
        const string qc = """{"roles":["QcReviewer","Reviewer"]}""";

        (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", newcomer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", reviewer, body: qc, ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", admin, body: qc)).ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", admin, body: qc, ifMatch: "\"7\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        (await (await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", admin, body: """{"roles":["Owner"]}""", ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").GetProperty("roles").GetArrayLength().Should().Be(1);
        (await (await SendAsync(client, HttpMethod.Put, $"{users}/{Guid.CreateVersion7()}", admin, body: qc, ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("userId", out _).Should().BeTrue();
        (await SendAsync(client, HttpMethod.Put, $"{users}/not-a-guid", admin, body: qc, ifMatch: "\"1\"")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var assigned = await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", admin, body: qc, ifMatch: "\"1\"");
        assigned.Headers.ETag!.Tag.Should().Be("\"2\"");
        using (var json = await JsonAsync(assigned))
        {
            json.RootElement.GetProperty("version").GetInt64().Should().Be(2);
            var principal = json.RootElement.GetProperty("principal");
            principal.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("Reviewer", "QcReviewer");
            principal.GetProperty("appliesToYou").GetBoolean().Should().BeFalse();
        }

        // The PDP reads PostgreSQL per request: the newcomer is a member with QC permissions at once.
        using (var workspace = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", newcomer)))
        {
            workspace.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
                .Should().Contain("Coding.Bulk").And.NotContain("Workspace.ManageUsers");
        }

        // The same roles again change nothing (no version bump, no audit); a stale version is refused.
        (await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", admin, body: qc, ifMatch: "\"2\"")).Headers.ETag!.Tag.Should().Be("\"2\"");
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", admin, body: """{"roles":[]}""", ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");

        // Group assignments, then removing everything: the user is no longer a member.
        using (var group = await JsonAsync(await SendAsync(client, HttpMethod.Put, $"/api/v1/workspaces/{ws}/role-assignments/groups?name=cn%3Dlit-support%2Cou%3Dlegal",
            admin, body: """{"roles":["ProductionManager"]}""", ifMatch: "\"2\"")))
        {
            group.RootElement.GetProperty("principal").GetProperty("groupName").GetString().Should().Be("cn=lit-support,ou=legal");
        }

        await (await SendAsync(client, HttpMethod.Put, $"/api/v1/workspaces/{ws}/role-assignments/groups?name=%20", admin, body: qc, ifMatch: "\"3\""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        (await SendAsync(client, HttpMethod.Put, $"{users}/{newcomer}", admin, body: """{"roles":[]}""", ifMatch: "\"3\"")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", newcomer)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var audit = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', action, actor_id, resource_type, details->>'role', coalesce(details->>'userId', details->>'groupName'))
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Security' ORDER BY recorded_at
            """);
        audit.Should().Equal(
            $"RoleAssigned|{admin}|RoleAssignment|Reviewer|{newcomer}",
            $"RoleAssigned|{admin}|RoleAssignment|QcReviewer|{newcomer}",
            $"RoleAssigned|{admin}|RoleAssignment|ProductionManager|cn=lit-support,ou=legal",
            $"RoleRevoked|{admin}|RoleAssignment|Reviewer|{newcomer}",
            $"RoleRevoked|{admin}|RoleAssignment|QcReviewer|{newcomer}");
    }

    [Fact]
    public async Task Nobody_adds_a_role_to_themselves_and_giving_one_up_needs_confirmation_but_never_removes_the_last_admin()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var first = await db.CreateUserAsync();
        var second = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, first);
        await db.AssignGroupAsync(ws, WorkspaceRole.Reviewer, "cn=review");
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var users = $"/api/v1/workspaces/{ws}/role-assignments/users";
        var groups = $"/api/v1/workspaces/{ws}/role-assignments/groups?name=cn%3Dreview";

        // Adding to oneself, directly or through a group one belongs to, is self-protection (ADR-015 D6.5).
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{first}", first, body: """{"roles":["WorkspaceAdmin","Auditor"]}""", ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "self-protection");
        await (await SendAsync(client, HttpMethod.Put, groups, first, "cn=review", body: """{"roles":["Reviewer","QcReviewer"]}""", ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "self-protection");

        // The only Workspace Admin cannot give the role up, confirmed or not.
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{first}", first, body: """{"roles":[],"confirmSelfRemoval":true}""", ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "last-administrator");

        (await SendAsync(client, HttpMethod.Put, $"{users}/{second}", first, body: """{"roles":["WorkspaceAdmin"]}""", ifMatch: "\"1\""))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // With a second admin the first may leave, but only after confirming; a group of theirs counts too.
        await (await SendAsync(client, HttpMethod.Put, groups, first, "cn=review", body: """{"roles":[]}""", ifMatch: "\"2\""))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "confirmation-required");
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{first}", first, body: """{"roles":[]}""", ifMatch: "\"2\""))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "confirmation-required");
        using (var left = await JsonAsync(await SendAsync(client, HttpMethod.Put, $"{users}/{first}", first,
            body: """{"roles":[],"confirmSelfRemoval":true}""", ifMatch: "\"2\"")))
        {
            left.RootElement.GetProperty("administratorPaths").GetInt32().Should().Be(1);
        }

        (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/role-assignments", first)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Now the second admin is the last one: removing them is refused for anyone, the workspace stays administrable.
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{second}", second, body: """{"roles":["Reviewer"],"confirmSelfRemoval":true}""", ifMatch: "\"3\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "self-protection");
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{second}", second, body: """{"roles":[],"confirmSelfRemoval":true}""", ifMatch: "\"3\""))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "last-administrator");
        (await db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND role = 'WorkspaceAdmin'", ("ws", ws))).Should().Be(1);

        var audit = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', action, actor_id, details->>'role', coalesce(details->>'selfRemoval', ''))
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Security' ORDER BY recorded_at
            """);
        audit.Should().Equal($"RoleAssigned|{first}|WorkspaceAdmin|", $"RoleRevoked|{first}|WorkspaceAdmin|true");
    }

    [Fact]
    public async Task Concurrent_removals_cannot_both_remove_an_admin()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var a = await db.CreateUserAsync();
        var b = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, a);
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, b);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var users = $"/api/v1/workspaces/{ws}/role-assignments/users";

        // Each removes the other with the version both read: the workspace row lock lets exactly one through. The other
        // answers 412 (stale version) or, when its request starts after the first committed, 403 (no longer an admin).
        var results = await Task.WhenAll(
            SendAsync(client, HttpMethod.Put, $"{users}/{b}", a, body: """{"roles":[]}""", ifMatch: "\"1\""),
            SendAsync(client, HttpMethod.Put, $"{users}/{a}", b, body: """{"roles":[]}""", ifMatch: "\"1\""));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.PreconditionFailed || r.StatusCode == HttpStatusCode.Forbidden);
        foreach (var r in results)
        {
            r.Dispose();
        }

        (await db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND role = 'WorkspaceAdmin'", ("ws", ws))).Should().Be(1);
    }

    [Fact]
    public async Task Break_glass_is_assigned_to_users_by_installation_admins_only_and_its_removal_ends_a_live_activation()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var installationAdmin = await db.CreateUserAsync();
        var responder = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, installationAdmin);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var users = $"/api/v1/workspaces/{ws}/role-assignments/users";
        const string breakGlass = """{"roles":["Reviewer","BreakGlass"]}""";

        await (await SendAsync(client, HttpMethod.Put, $"{users}/{responder}", admin, body: breakGlass, ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Put, $"/api/v1/workspaces/{ws}/role-assignments/groups?name=cn%3Dresponders", installationAdmin, AdminGroup,
            body: """{"roles":["BreakGlass"]}""", ifMatch: "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Put, $"{users}/{installationAdmin}", installationAdmin, AdminGroup,
            body: """{"roles":["WorkspaceAdmin","BreakGlass"]}""", ifMatch: "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "self-protection");

        using (var list = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/role-assignments", installationAdmin, AdminGroup)))
        {
            list.RootElement.GetProperty("canAssignBreakGlass").GetBoolean().Should().BeTrue();
        }

        (await SendAsync(client, HttpMethod.Put, $"{users}/{responder}", installationAdmin, AdminGroup, body: breakGlass, ifMatch: "\"1\""))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await db.ActivateBreakGlassAsync(ws, responder, TimeSpan.FromMinutes(60));
        using (var workspace = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", responder)))
        {
            workspace.RootElement.GetProperty("breakGlassActive").GetBoolean().Should().BeTrue();
        }

        // Revoking emergency access needs no installation role, and it ends the live activation in the same transaction.
        (await SendAsync(client, HttpMethod.Put, $"{users}/{responder}", admin, body: """{"roles":["Reviewer"]}""", ifMatch: "\"2\""))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.Core.ScalarAsync<string>(
            "SELECT ended_reason FROM opportunity.break_glass_activation WHERE workspace_id = @ws AND user_id = @u", ("ws", ws), ("u", responder)))
            .Should().Be("Revoked");

        var audit = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', coalesce(workspace_id::text, 'system'), category, action, actor_id, coalesce(details->>'role', details->>'Cause', details->>'permission'))
            FROM audit.audit_event WHERE category = 'Security' OR (category = 'AuthZ' AND resource_type = 'Installation') ORDER BY recorded_at
            """);
        audit.Should().Equal(
            $"system|AuthZ|Denied|{admin}|Installation.AssignBreakGlass",
            $"{ws}|Security|RoleAssigned|{installationAdmin}|Reviewer",
            $"{ws}|Security|RoleAssigned|{installationAdmin}|BreakGlass",
            $"{ws}|Security|RoleRevoked|{admin}|BreakGlass",
            $"{ws}|Security|BreakGlassEnded|{admin}|RoleRevoked");
    }

    [Fact]
    public async Task Candidates_are_signed_in_users_and_known_groups_matching_the_text()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var morgan = await db.CreateUserAsync(groups: ["cn=paralegals"]);
        await db.Core.ExecuteAsync("UPDATE opportunity.app_user SET display_name = 'Morgan Reyes', email = 'morgan@example.test' WHERE user_id = @id", ("id", morgan));
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await using var factory = Factory(db);
        using var client = factory.CreateClient();

        using var byName = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/role-assignments/candidates?q=morgan", admin));
        byName.RootElement.GetProperty("items").EnumerateArray()
            .Select(c => $"{c.GetProperty("kind").GetString()}|{c.GetProperty("displayName").GetString()}|{c.GetProperty("email").GetString()}")
            .Should().Equal("user|Morgan Reyes|morgan@example.test");
        using var byGroup = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/role-assignments/candidates?q=PARA", admin));
        byGroup.RootElement.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("groupName").GetString()).Should().Equal("cn=paralegals");
        using var wildcard = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}/role-assignments/candidates?q=%25", admin));
        wildcard.RootElement.GetProperty("items").GetArrayLength().Should().Be(0, "LIKE wildcards are matched literally");
    }

    private static List<string> Rows(JsonDocument list) =>
        [.. list.RootElement.GetProperty("items").EnumerateArray().Select(p => string.Join('|',
            p.GetProperty("kind").GetString(),
            p.GetProperty("kind").GetString() == "user" ? p.GetProperty("userId").GetString() : p.GetProperty("groupName").GetString(),
            p.GetProperty("displayName").GetString(),
            string.Join(',', p.GetProperty("roles").EnumerateArray().Select(r => r.GetString())),
            p.GetProperty("appliesToYou").GetBoolean() ? "you" : string.Empty))];

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.UseSetting("Authorization:InstallationAdminGroups:0", AdminGroup);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, string? groups = null, string? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (groups is not null)
        {
            request.Headers.Add(TestAuthentication.GroupsHeader, groups);
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
}
