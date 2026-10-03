using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Workspaces;
using Opportunity.Core.Security;
using Opportunity.Data.Workspaces;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// E04-T05 workspace store as the RLS-bound app role: the membership lookup behind <c>GET /api/v1/workspaces</c>
/// (V0014) agrees with the PDP for every kind of principal, leaves no RLS context behind, and writes are audited in
/// their own transaction.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class WorkspaceStoreTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_membership_list_matches_the_pdp_for_every_principal()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var active = await db.Core.CreateWorkspaceAsync();
        var closed = await db.Core.CreateWorkspaceAsync();
        var deleting = await db.Core.CreateWorkspaceAsync();
        var walled = await db.Core.CreateWorkspaceAsync();
        var breakGlass = await db.Core.CreateWorkspaceAsync();
        var foreign = await db.Core.CreateWorkspaceAsync();
        await db.Core.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Closed', closed_at = now() WHERE workspace_id = @ws", ("ws", closed));
        await db.Core.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Deleting', closed_at = now() WHERE workspace_id = @ws", ("ws", deleting));

        var user = await db.CreateUserAsync();
        var responder = await db.CreateUserAsync();
        var outsider = await db.CreateUserAsync();
        await db.AssignAsync(active, WorkspaceRole.Reviewer, user);
        await db.AssignGroupAsync(closed, WorkspaceRole.Auditor, "cn=litigation");
        await db.AssignAsync(deleting, WorkspaceRole.WorkspaceAdmin, user);
        await db.AssignAsync(walled, WorkspaceRole.Reviewer, user);
        await db.WallAsync(walled, [user], [], [await db.DocumentAsync(walled)]);
        await db.AssignAsync(breakGlass, WorkspaceRole.BreakGlass, user);
        await db.AssignGroupAsync(breakGlass, WorkspaceRole.BreakGlass, "cn=litigation");
        await db.AssignAsync(breakGlass, WorkspaceRole.BreakGlass, responder);
        await db.ActivateBreakGlassAsync(breakGlass, responder, TimeSpan.FromMinutes(30));
        await db.AssignAsync(foreign, WorkspaceRole.WorkspaceAdmin, outsider);

        var store = new WorkspaceStore(db.Core.AppDataSource);
        var principals = new[]
        {
            User(user),
            User(user, "cn=litigation"),
            User(responder),
            User(outsider),
            User(Guid.CreateVersion7(), "cn=unknown"),
        };
        var all = new[] { active, closed, deleting, walled, breakGlass, foreign };
        foreach (var principal in principals)
        {
            var listed = (await store.ListForPrincipalAsync(principal, null, 50, Ct)).Items.Select(w => w.WorkspaceId).ToList();
            var admitted = new List<Guid>();
            foreach (var ws in all)
            {
                if ((await Pdp(db).AuthorizeMembershipAsync(principal, ws, Ct)).IsAllowed)
                {
                    admitted.Add(ws);
                }
            }

            listed.Should().BeEquivalentTo(admitted, "the list shows exactly what GET admits for {0} [{1}]", principal.UserId, string.Join(',', principal.Groups));
        }

        (await store.ListForPrincipalAsync(User(user, "cn=litigation"), null, 50, Ct)).Items.Select(w => w.WorkspaceId)
            .Should().BeEquivalentTo([active, closed, walled], "walls hide documents, not workspaces (Q-59); break-glass needs an activation");
        (await store.ListForPrincipalAsync(User(responder), null, 50, Ct)).Items.Select(w => w.WorkspaceId).Should().Equal(breakGlass);
    }

    [Fact]
    public async Task The_list_pages_by_name_and_counts_every_membership()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var user = await db.CreateUserAsync();
        var names = new[] { "beta", "Alpha", "gamma", "alpha", "Delta" };
        var ids = new Dictionary<Guid, string>();
        foreach (var name in names)
        {
            var ws = await db.Core.CreateWorkspaceAsync();
            await db.Core.ExecuteAsync("UPDATE opportunity.workspace SET name = @name WHERE workspace_id = @ws", ("name", name), ("ws", ws));
            await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);
            ids[ws] = name;
        }

        var store = new WorkspaceStore(db.Core.AppDataSource);
        var seen = new List<string>();
        WorkspaceListPosition? after = null;
        var pages = 0;
        do
        {
            var page = await store.ListForPrincipalAsync(User(user), after, 2, Ct);
            page.Total.Should().Be(5);
            page.Items.Count.Should().BeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(w => w.Name));
            after = page.Next;
            pages++;
        }
        while (after is not null);

        pages.Should().Be(3);
        seen.Select(n => n.ToLowerInvariant()).Should().Equal("alpha", "alpha", "beta", "delta", "gamma");
    }

    [Fact]
    public async Task The_lookup_restores_the_callers_rls_context()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var user = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);

        await using var connection = await db.Core.AppDataSource.OpenConnectionAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM opportunity.member_workspace_ids(@user, '{}')),
                   coalesce(current_setting('app.workspace_id', true), ''),
                   (SELECT count(*) FROM opportunity.workspace_role_assignment)
            """, connection, tx);
        command.Parameters.AddWithValue("user", user);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetString(1).Should().BeEmpty("the function puts the caller's (empty) context back");
        reader.GetInt64(2).Should().Be(0, "without a context the caller still sees no tenant row");
        await reader.DisposeAsync();

        // Called inside another workspace's context, that context survives the call.
        var other = await db.Core.CreateWorkspaceAsync();
        await using var bound = new NpgsqlCommand(
            """
            SELECT set_config('app.workspace_id', @other, true),
                   (SELECT count(*) FROM opportunity.member_workspace_ids(@user, '{}')),
                   current_setting('app.workspace_id', true)
            """, connection, tx);
        bound.Parameters.AddWithValue("other", other.ToString());
        bound.Parameters.AddWithValue("user", user);
        await using var boundReader = await bound.ExecuteReaderAsync(Ct);
        (await boundReader.ReadAsync(Ct)).Should().BeTrue();
        boundReader.GetInt64(1).Should().Be(1);
        boundReader.GetString(2).Should().Be(other.ToString());
    }

    [Fact]
    public async Task Only_the_app_role_may_run_the_lookup()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var readonlyLogin = await Documents.CoreSchemaDatabase.CreateLoginAsync(db.Core.ConnectionString, "opportunity_readonly");
        await using var source = NpgsqlDataSource.Create(readonlyLogin);
        await using var command = source.CreateCommand("SELECT count(*) FROM opportunity.member_workspace_ids(gen_random_uuid(), '{}')");
        var act = async () => await command.ExecuteScalarAsync(Ct);
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Create_and_update_write_the_workspace_role_and_audit_atomically()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var creator = await db.CreateUserAsync();
        var store = new WorkspaceStore(db.Core.AppDataSource);
        var ws = Guid.CreateVersion7();

        (await store.CreateAsync(ws, new WorkspaceSettings("Acme v. Widget", null, "Not/AZone", "default"), User(creator), Ct))
            .Outcome.Should().Be(WorkspaceWriteOutcome.InvalidTimeZone);
        (await db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws))).Should().Be(0);

        var created = await store.CreateAsync(ws, new WorkspaceSettings("Acme v. Widget", "2026-001", "Europe/Berlin", "default"), User(creator), Ct);
        created.Outcome.Should().Be(WorkspaceWriteOutcome.Ok);
        created.Workspace!.RowVersion.Should().Be(1);
        (await Pdp(db).AuthorizeAsync(User(creator), ws, Permission.WorkspaceManageUsers, Ct)).IsAllowed.Should().BeTrue("the creator is the first Workspace Admin");
        (await db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.restriction_class WHERE workspace_id = @ws", ("ws", ws)))
            .Should().Be(3, "the built-in classes are seeded under the new workspace's context");

        (await store.UpdateAsync(ws, 7, new WorkspaceSettings("Renamed", null, "UTC", "default"), User(creator), Ct))
            .Outcome.Should().Be(WorkspaceWriteOutcome.VersionConflict);
        var unchanged = await store.UpdateAsync(ws, 1, new WorkspaceSettings("Acme v. Widget", "2026-001", "Europe/Berlin", "default"), User(creator), Ct);
        unchanged.Workspace!.RowVersion.Should().Be(1, "a no-op update changes nothing and is not audited");
        var updated = await store.UpdateAsync(ws, 1, new WorkspaceSettings("Renamed", null, "Asia/Tokyo", "archive"), User(creator), Ct);
        updated.Outcome.Should().Be(WorkspaceWriteOutcome.Ok);
        updated.Workspace!.Should().BeEquivalentTo(new { Name = "Renamed", MatterNumber = (string?)null, DisplayTimeZone = "Asia/Tokyo", StorageProfile = "archive", RowVersion = 2L });

        await db.Core.InsertStoredObjectAsync(ws, documentId: null);
        (await store.UpdateAsync(ws, 2, new WorkspaceSettings("Renamed", null, "Asia/Tokyo", "default"), User(creator), Ct))
            .Outcome.Should().Be(WorkspaceWriteOutcome.StorageProfileLocked);

        var audit = await db.Core.ColumnAsync(
            $"""
            SELECT concat_ws('|', category, action, actor_id, resource_type, outcome, coalesce(details->>'changed', details->>'role', details->>'storageProfile'))
            FROM audit.audit_event WHERE workspace_id = '{ws}' ORDER BY recorded_at, category
            """);
        audit.Should().BeEquivalentTo(
            [
                $"Workspace|Created|{creator}|Workspace|Success|default",
                $"Security|RoleAssigned|{creator}|RoleAssignment|Success|WorkspaceAdmin",
                $"Workspace|SettingsChanged|{creator}|Workspace|Success|name,matterNumber,displayTimeZone,storageProfile",
            ]);
        (await db.Core.ScalarAsync<long>(
            $"SELECT count(*) FROM audit.audit_event WHERE workspace_id = '{ws}' AND details::text LIKE '%Renamed%'"))
            .Should().Be(0, "names and matter numbers are not copied into audit details");
    }

    [Fact]
    public async Task Members_list_user_and_group_assignments_of_this_workspace_only()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var other = await db.Core.CreateWorkspaceAsync();
        var alice = await db.CreateUserAsync();
        await db.Core.ExecuteAsync("UPDATE opportunity.app_user SET display_name = 'Alice Example' WHERE user_id = @id", ("id", alice));
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, alice);
        await db.AssignGroupAsync(ws, WorkspaceRole.Reviewer, "cn=review");
        await db.AssignAsync(ws, WorkspaceRole.Auditor, alice);
        await db.AssignAsync(other, WorkspaceRole.Reviewer, await db.CreateUserAsync());

        var store = new WorkspaceStore(db.Core.AppDataSource);
        var first = await store.ListMembersAsync(ws, null, 2, Ct);
        first.Total.Should().Be(3);
        first.Items.Select(m => (m.Role, m.UserId, m.UserDisplayName, m.GroupName)).Should().Equal(
            (WorkspaceRole.WorkspaceAdmin, alice, "Alice Example", null),
            (WorkspaceRole.Reviewer, null, null, "cn=review"));
        var second = await store.ListMembersAsync(ws, first.NextAfter, 2, Ct);
        second.Items.Should().ContainSingle().Which.Role.Should().Be(WorkspaceRole.Auditor);
        second.NextAfter.Should().BeNull();
    }

    private static AuthorizationService Pdp(AuthorizationDatabase db) => new(db.Reader, new InMemoryAuditEventWriter(), TimeProvider.System);

    private static SecurityPrincipal User(Guid userId, params string[] groups) =>
        new() { UserId = userId, DisplayName = "user " + userId, Groups = groups };
}
