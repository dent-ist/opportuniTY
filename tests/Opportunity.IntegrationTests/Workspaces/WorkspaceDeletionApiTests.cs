using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Workspaces;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// E20-T02 through the real API host and PostgreSQL: a Workspace Admin requests (reason, profile, name typed), a
/// Retention Approver (installation group, MFA) approves, the requester cannot approve, only the two of them see the
/// deletion, a legal hold answers 423 with an audit event, the fenced workspace answers 404 while the deletion's status
/// and its certificate stay readable at installation level.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class WorkspaceDeletionApiTests(MigrationPostgresFixture postgres)
{
    private const string Approvers = "retention-approvers";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_deletion_is_requested_approved_by_a_second_person_fenced_run_and_certified_through_the_api()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        await using var h = DeletionHarness.Over(db);
        var ws = await db.Core.CreateWorkspaceAsync();
        await h.SeedAsync(ws, "API");
        var admin = await db.CreateUserAsync();
        var reviewer = await db.CreateUserAsync();
        var approver = await db.CreateUserAsync();
        var outsider = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.Core.ExecuteAsync("UPDATE opportunity.app_user SET display_name = 'Avery Admin' WHERE user_id = @id", ("id", admin));
        var name = await db.Core.ScalarAsync<string>("SELECT name FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws));
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        var requests = $"/api/v1/workspaces/{ws}/deletions";
        var body = JsonSerializer.Serialize(new { reason = "Matter closed; protective order ¶ 14 requires destruction", confirmName = name });

        // Workspace.RequestDeletion (Workspace Admin): others get 403, non-members 404; the name must be typed exactly.
        await (await SendAsync(client, HttpMethod.Post, requests, reviewer, body: body)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, requests, outsider, body: body)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        (await (await SendAsync(client, HttpMethod.Post, requests, admin, body: JsonSerializer.Serialize(new { reason = "x", confirmName = "wrong" })))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("confirmName", out _).Should().BeTrue();

        string deletion;
        using (var created = await SendAsync(client, HttpMethod.Post, requests, admin, body: body))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            created.Headers.ETag!.Tag.Should().Be("\"1\"");
            using var json = await JsonAsync(created);
            var root = json.RootElement;
            deletion = root.GetProperty("deletionId").GetString()!;
            root.GetProperty("status").GetString().Should().Be("requested");
            root.GetProperty("retentionProfile").GetString().Should().Be("retainRecords", "Q-23: records are retained by default");
            root.GetProperty("requestedBy").GetProperty("displayName").GetString().Should().Be("Avery Admin");
            root.GetProperty("canApprove").GetBoolean().Should().BeFalse();
            root.GetProperty("canCancel").GetBoolean().Should().BeTrue();
        }

        await (await SendAsync(client, HttpMethod.Post, requests, admin, body: body)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        using (var list = await JsonAsync(await SendAsync(client, HttpMethod.Get, requests, admin)))
        {
            list.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        }

        // Visibility: the requester and approvers; anyone else gets 404 and an empty list.
        var item = $"/api/v1/workspace-deletions/{deletion}";
        await (await SendAsync(client, HttpMethod.Get, item, outsider)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        using (var list = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/workspace-deletions", outsider)))
        {
            list.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        }

        using (var seen = await JsonAsync(await SendAsync(client, HttpMethod.Get, item, approver, groups: Approvers)))
        {
            seen.RootElement.GetProperty("canApprove").GetBoolean().Should().BeTrue();
        }

        // Approval: the installation permission, MFA, If-Match, and a second person.
        var approve = item + "/approve";
        await (await SendAsync(client, HttpMethod.Post, approve, approver, mfa: true, ifMatch: "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await SendAsync(client, HttpMethod.Post, approve, approver, groups: Approvers, ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "step-up-required");
        await (await SendAsync(client, HttpMethod.Post, approve, admin, groups: Approvers, mfa: true, ifMatch: "\"1\""))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "second-person-required");
        await (await SendAsync(client, HttpMethod.Post, approve, approver, groups: Approvers, mfa: true))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionRequired, "precondition-required");
        await (await SendAsync(client, HttpMethod.Post, approve, approver, groups: Approvers, mfa: true, ifMatch: "\"7\""))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "version-conflict");
        using (var approved = await JsonAsync(await SendAsync(client, HttpMethod.Post, approve, approver, groups: Approvers, mfa: true, ifMatch: "\"1\"",
            body: """{"note":"Order of 2026-10-01 confirmed"}""")))
        {
            var root = approved.RootElement;
            root.GetProperty("status").GetString().Should().Be("approved");
            root.GetProperty("approvedBy").GetProperty("userId").GetGuid().Should().Be(approver);
            root.GetProperty("runNotBefore").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        }

        await (await SendAsync(client, HttpMethod.Post, item + "/cancel", outsider, ifMatch: "\"2\"")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        // The fence: once the run starts, every workspace route answers 404; the deletion stays readable.
        h.Options.DrainSettleDelay = TimeSpan.FromHours(1);
        await h.Coordinator().StepAsync(Guid.Parse(deletion), Ct);
        await (await SendAsync(client, HttpMethod.Get, $"/api/v1/workspaces/{ws}", admin)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        await (await SendAsync(client, HttpMethod.Get, requests, admin)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        using (var running = await JsonAsync(await SendAsync(client, HttpMethod.Get, item, admin)))
        {
            running.RootElement.GetProperty("status").GetString().Should().Be("running");
            running.RootElement.GetProperty("currentStep").GetString().Should().Be("drain");
            running.RootElement.GetProperty("canCancel").GetBoolean().Should().BeFalse();
        }

        await (await SendAsync(client, HttpMethod.Post, item + "/cancel", admin, ifMatch: "\"3\"")).ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        await (await SendAsync(client, HttpMethod.Get, item + "/certificate", admin)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");

        h.Options.DrainSettleDelay = TimeSpan.Zero;
        (await h.RunAsync(Guid.Parse(deletion))).Status.Should().Be(Application.Workspaces.Deletion.WorkspaceDeletionStatus.Completed);
        using (var done = await JsonAsync(await SendAsync(client, HttpMethod.Get, item, admin)))
        {
            var root = done.RootElement;
            root.GetProperty("status").GetString().Should().Be("completed");
            root.GetProperty("certificateAvailable").GetBoolean().Should().BeTrue();
            var steps = root.GetProperty("steps").EnumerateArray().ToList();
            steps.Select(s => s.GetProperty("step").GetString()).Should().Equal(
                "fence", "drain", "inventory", "searchPurge", "databasePurge", "storagePurge", "keyDestruction", "verification", "certification");
            steps.Should().AllSatisfy(s => s.GetProperty("outcome").GetString().Should().Be("success"));
            steps[2].GetProperty("totals").GetProperty("rows").GetInt64().Should().BeGreaterThan(0);
        }

        await (await SendAsync(client, HttpMethod.Get, item + "/certificate", outsider)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
        using (var download = await SendAsync(client, HttpMethod.Get, item + "/certificate", approver, groups: Approvers))
        {
            download.StatusCode.Should().Be(HttpStatusCode.OK);
            download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            using var json = await JsonAsync(download);
            var root = json.RootElement;
            var canonical = root.GetProperty("canonical").GetString()!;
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).Should().Be(root.GetProperty("sha256").GetString());
            root.GetProperty("certificate").GetProperty("workspace").GetProperty("id").GetGuid().Should().Be(ws);
            root.GetProperty("certificate").GetProperty("retentionProfile").GetString().Should().Be("RetainRecords");
            root.GetProperty("signature").GetProperty("algorithm").GetString().Should().Be("ES256");
        }
    }

    [Fact]
    public async Task A_legal_hold_refuses_the_request_with_423_and_an_audit_event()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        var name = await db.Core.ScalarAsync<string>("SELECT name FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws));
        await using var factory = Factory(db);
        using var client = factory.CreateClient();
        (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/preservation-locks", admin, body: """{"reason":"Litigation hold"}"""))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await (await SendAsync(client, HttpMethod.Post, $"/api/v1/workspaces/{ws}/deletions", admin,
            body: JsonSerializer.Serialize(new { reason = "End of matter", retentionProfile = "purgeAll", confirmName = name })))
            .ShouldBeProblemAsync((HttpStatusCode)423, "preservation-locked");
        (await db.Core.ScalarAsync<long>(
            $"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'DeletionBlocked' AND outcome = 'Denied'")).Should().Be(1);
        (await db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.workspace_deletion WHERE deletion_workspace_id = @ws", ("ws", ws))).Should().Be(0);
    }

    private static WebApplicationFactory<Program> Factory(AuthorizationDatabase db) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.UseSetting("Authentication:Mfa:AmrValues:0", "mfa");
            builder.UseSetting("Authorization:RetentionApproverGroups:0", Approvers);
            builder.UseSetting("WorkspaceDeletion:WaitingPeriod", "00:00:00");
            builder.UseSetting("WorkspaceDeletion:AllowShortWaitingPeriod", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid user, string? groups = null, string? body = null, bool mfa = false, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        if (groups is not null)
        {
            request.Headers.Add(TestAuthentication.GroupsHeader, groups);
        }

        if (mfa)
        {
            request.Headers.Add(TestAuthentication.AmrHeader, "mfa");
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
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text);
    }
}
