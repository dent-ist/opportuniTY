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
/// E20-T01 legal holds through the real API host, the PostgreSQL PDP and audit store: placing and releasing need
/// <c>Workspace.ManageHolds</c> and a reason, a release needs a second person by default, every step is audited, every
/// member sees the hold on the workspace, and a delete API on a held workspace answers 423 with an audit event.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PreservationLockApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Placing_and_releasing_need_the_hold_permission_a_reason_and_a_second_person_and_are_audited()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var counsel = await db.CreateUserAsync();
        var partner = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, counsel);
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, partner);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.Core.ExecuteAsync("UPDATE opportunity.app_user SET display_name = 'Casey Counsel' WHERE user_id = @id", ("id", counsel));
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var locks = $"/api/v1/workspaces/{ws}/preservation-locks";
        const string place = """{"reason":"Litigation hold: complaint served","matterReference":"2026-CV-0142"}""";

        // The designated role: Workspace.ManageHolds (Workspace Admin); members without it get 403, others 404.
        await (await SendAsync(client, HttpMethod.Get, locks, reviewer)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, locks, reviewer, place)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, locks, await db.CreateUserAsync(), place)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // A reason is required.
        (await (await SendAsync(client, HttpMethod.Post, locks, counsel, """{"reason":"  "}"""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("reason", out _).Should().BeTrue();
        await (await SendAsync(client, HttpMethod.Post, locks, counsel, """{"reason":"ok","matterReference":"two\nlines"}"""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        Guid lockId;
        using (var created = await SendAsync(client, HttpMethod.Post, locks, counsel, place))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            created.Headers.ETag!.Tag.Should().Be("\"1\"");
            using var json = await JsonAsync(created);
            var root = json.RootElement;
            lockId = root.GetProperty("lockId").GetGuid();
            root.GetProperty("status").GetString().Should().Be("active");
            root.GetProperty("scope").GetString().Should().Be("workspace");
            root.GetProperty("releaseRequiresApproval").GetBoolean().Should().BeTrue("a second person approves releases by default (Q-23)");
            root.GetProperty("placedBy").GetProperty("displayName").GetString().Should().Be("Casey Counsel");
            root.GetProperty("matterReference").GetString().Should().Be("2026-CV-0142");
        }

        // Every member sees the hold on the workspace (header, settings); only hold managers read the locks.
        using (var workspace = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", reviewer)))
        {
            workspace.RootElement.GetProperty("activePreservationLocks").GetInt32().Should().Be(1);
            workspace.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().NotContain("Workspace.ManageHolds");
        }

        var release = $"{locks}/{lockId}/release";
        await (await SendAsync(client, HttpMethod.Post, release, counsel, """{"reason":"Case dismissed"}"""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await SendAsync(client, HttpMethod.Post, release, counsel, """{"reason":""}""", "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await SendAsync(client, HttpMethod.Post, release, reviewer, """{"reason":"Case dismissed"}""", "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // The request leaves the hold active; the requester cannot approve it; the other admin can cancel it.
        using (var requested = await JsonAsync(await SendAsync(client, HttpMethod.Post, release, counsel, """{"reason":"Case dismissed"}""", "\"1\"")))
        {
            requested.RootElement.GetProperty("status").GetString().Should().Be("releasePending");
            requested.RootElement.GetProperty("version").GetInt64().Should().Be(2);
        }

        await (await SendAsync(client, HttpMethod.Post, release, partner, """{"reason":"Again"}""", "\"2\""))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        await (await SendAsync(client, HttpMethod.Post, release + "/approve", counsel, null, "\"2\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "second-person-required");
        await (await SendAsync(client, HttpMethod.Post, release + "/approve", partner, null, "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        using (var cancelled = await JsonAsync(await SendAsync(client, HttpMethod.Post, release + "/cancel", partner, null, "\"2\"")))
        {
            cancelled.RootElement.GetProperty("status").GetString().Should().Be("active");
        }

        (await SendAsync(client, HttpMethod.Post, release, counsel, """{"reason":"Appeal window closed"}""", "\"3\"")).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var approved = await JsonAsync(await SendAsync(client, HttpMethod.Post, release + "/approve", partner, null, "\"4\"")))
        {
            var root = approved.RootElement;
            root.GetProperty("status").GetString().Should().Be("released");
            root.GetProperty("releaseRequestedBy").GetProperty("userId").GetGuid().Should().Be(counsel);
            root.GetProperty("releaseApprovedBy").GetProperty("userId").GetGuid().Should().Be(partner);
            root.GetProperty("releaseReason").GetString().Should().Be("Appeal window closed");
        }

        await (await SendAsync(client, HttpMethod.Post, release + "/approve", partner, null, "\"5\""))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        await (await SendAsync(client, HttpMethod.Post, $"{locks}/{Guid.CreateVersion7()}/release", counsel, """{"reason":"x"}""", "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // A hold placed without the approval requirement is released by one person.
        string single;
        using (var created = await JsonAsync(await SendAsync(client, HttpMethod.Post, locks, partner,
            """{"reason":"Short-term hold for a subpoena","releaseRequiresApproval":false}""")))
        {
            single = created.RootElement.GetProperty("lockId").GetString()!;
        }

        using (var list = await JsonAsync(await SendAsync(client, HttpMethod.Get, locks, counsel)))
        {
            list.RootElement.GetProperty("activeCount").GetInt32().Should().Be(1);
            list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).Should().Equal("active", "released");
        }

        using (var released = await JsonAsync(await SendAsync(client, HttpMethod.Post, $"{locks}/{single}/release", partner, """{"reason":"Subpoena withdrawn"}""", "\"1\"")))
        {
            released.RootElement.GetProperty("status").GetString().Should().Be("released");
            released.RootElement.TryGetProperty("releaseApprovedBy", out var approver).Should().BeTrue();
            approver.ValueKind.Should().Be(JsonValueKind.Null);
        }

        using (var workspace = await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", reviewer)))
        {
            workspace.RootElement.GetProperty("activePreservationLocks").GetInt32().Should().Be(0);
        }

        // Every step is audited with the lock id; reasons and matter references stay on the lock record (ADR-013 §7).
        var audit = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', action, actor_id, resource_type, resource_id, details->>'releaseRequiresApproval')
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Workspace' ORDER BY recorded_at
            """);
        audit.Should().Equal(
            $"HoldPlaced|{counsel}|PreservationLock|{lockId}|true",
            $"HoldReleaseRequested|{counsel}|PreservationLock|{lockId}|true",
            $"HoldReleaseCancelled|{partner}|PreservationLock|{lockId}",
            $"HoldReleaseRequested|{counsel}|PreservationLock|{lockId}|true",
            $"HoldReleased|{partner}|PreservationLock|{lockId}|true",
            $"HoldPlaced|{partner}|PreservationLock|{single}|false",
            $"HoldReleased|{partner}|PreservationLock|{single}|false");
        (await db.Core.ColumnAsync($"SELECT details::text FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Workspace'"))
            .Should().NotContain(d => d.Contains("complaint", StringComparison.Ordinal) || d.Contains("2026-CV", StringComparison.Ordinal)
                || d.Contains("dismissed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delete_api_on_a_held_workspace_answers_423_with_an_audit_event_and_deletes_nothing()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        var reportId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        await db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.job (workspace_id, job_id, job_type, initiated_by) VALUES (@ws, @job, 'SearchTermReport', @by);
            INSERT INTO opportunity.search_term_report (workspace_id, report_id, name, scope_kind, status, status_reason, job_id, created_by,
                                                        created_by_display, executed_by, executed_by_display, completed_at)
            VALUES (@ws, @id, 'Hot terms', 'Workspace', 'Failed', 'Probe', @job, @by, 'Admin', @by, 'Admin', now())
            """,
            ("ws", ws), ("id", reportId), ("job", jobId), ("by", admin));
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var report = $"/api/v1/workspaces/{ws}/search-term-reports/{reportId}";

        string lockId;
        using (var created = await JsonAsync(await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/preservation-locks", admin,
            """{"reason":"Hold","releaseRequiresApproval":false}""")))
        {
            lockId = created.RootElement.GetProperty("lockId").GetString()!;
        }

        var problem = await (await SendAsync(client, HttpMethod.Delete, report, admin)).ShouldBeProblemAsync((HttpStatusCode)423, "preservation-locked");
        problem.GetProperty("detail").GetString().Should().Contain("legal hold");
        (await db.Core.ScalarAsync<long>($"SELECT count(*) FROM opportunity.search_term_report WHERE workspace_id = '{ws}'")).Should().Be(1);

        var blocked = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', action, outcome, reason_code, actor_id, resource_id, details->>'method', details->>'route', details->>'target')
            FROM audit.audit_event WHERE workspace_id = '{ws}' AND action IN ('DeletionBlocked', 'TermReportDeleted')
            """);
        blocked.Should().Equal(
            $"DeletionBlocked|Denied|LegalHold|{admin}|{ws}|DELETE|/api/v1/workspaces/{{workspaceId}}/search-term-reports/{{reportId}}|search_term_report");

        (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/preservation-locks/{lockId}/release", admin, """{"reason":"Done"}""", "\"1\""))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, HttpMethod.Delete, report, admin)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await db.Core.ScalarAsync<long>($"SELECT count(*) FROM opportunity.search_term_report WHERE workspace_id = '{ws}'")).Should().Be(0);
    }

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, string? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (method != HttpMethod.Get && method != HttpMethod.Delete)
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
