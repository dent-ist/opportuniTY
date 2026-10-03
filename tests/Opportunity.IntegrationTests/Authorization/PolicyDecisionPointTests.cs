using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Authorization;

/// <summary>
/// E05-T02: the PDP over authoritative PostgreSQL state (V0008) read as the RLS-bound app role: roles through users
/// and groups, default deny, class grants (Q-11), walls over every grant (Q-13), read-only break-glass (Q-45), and
/// that no state from another workspace ever counts.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PolicyDecisionPointTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryAuditEventWriter _audit = new();

    [Fact]
    public async Task Membership_comes_from_user_or_group_assignments_in_this_workspace_only()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var other = await db.Core.CreateWorkspaceAsync();
        var direct = await db.CreateUserAsync();
        var viaGroup = await db.CreateUserAsync();
        var outsider = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, direct);
        await db.AssignGroupAsync(ws, WorkspaceRole.Auditor, "cn=auditors");
        await db.AssignAsync(other, WorkspaceRole.WorkspaceAdmin, outsider);

        (await Pdp(db).AuthorizeAsync(User(direct), ws, Permission.CodingWrite, Ct)).IsAllowed.Should().BeTrue();
        (await Pdp(db).AuthorizeAsync(User(viaGroup, "cn=auditors"), ws, Permission.AuditRead, Ct)).IsAllowed.Should().BeTrue();
        (await Pdp(db).AuthorizeAsync(User(viaGroup), ws, Permission.AuditRead, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.NotAMember), "without the group claim there is no membership");
        (await Pdp(db).AuthorizeAsync(User(outsider), ws, Permission.DocumentView, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.NotAMember), "admin of another workspace is nobody here");
        (await Pdp(db).AuthorizeAsync(User(direct), Guid.CreateVersion7(), Permission.DocumentView, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.WorkspaceNotFound));
        (await Pdp(db).AuthorizeAsync(User(direct), ws, Permission.ExportCreate, Ct))
            .Should().Be(AuthorizationDecision.Deny(AuthorizationReasons.PermissionNotGranted));
    }

    [Fact]
    public async Task A_group_named_like_a_user_id_or_a_forged_role_grants_nothing()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var attacker = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);

        // Groups are matched only against group assignments, never against user assignments.
        (await Pdp(db).AuthorizeAsync(User(attacker, admin.ToString(), "WorkspaceAdmin", "%", "*"), ws, Permission.DocumentView, Ct))
            .Outcome.Should().Be(AuthorizationOutcome.NotFound);

        var insert = () => db.Core.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @id, 'SuperAdmin', @user)",
            ("ws", ws), ("id", Guid.CreateVersion7()), ("user", attacker));
        (await insert.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("workspace_role_assignment_role_ck");
    }

    [Fact]
    public async Task Built_in_classes_and_default_grants_are_seeded_for_every_new_workspace()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var user = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);

        var read = await db.Reader.ReadAsync(ws, User(user), includePrincipal: true, documentIds: null, Ct);

        read.Principal!.ClassGrants.Keys.Should().BeEquivalentTo(RestrictionClasses.BuiltIn.Select(c => c.ClassKey));
        foreach (var (classKey, _, roles) in RestrictionClasses.BuiltIn)
        {
            read.Principal.ClassGrants[classKey].Should().BeEquivalentTo(roles, "V0008 seeds the defaults in RestrictionClasses ({0})", classKey);
        }
    }

    [Fact]
    public async Task Restricted_documents_are_hidden_from_roles_without_the_class_grant()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var reviewer = await db.CreateUserAsync();
        var privilege = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, reviewer);
        await db.AssignAsync(ws, WorkspaceRole.PrivilegeReviewer, privilege);
        var plain = await db.DocumentAsync(ws);
        var privileged = await db.DocumentAsync(ws, RestrictionClasses.Privileged);
        var aeo = await db.DocumentAsync(ws, RestrictionClasses.AttorneysEyesOnly, RestrictionClasses.Privileged);

        var asReviewer = await Pdp(db).AuthorizeManyAsync(User(reviewer), ws, Permission.DocumentView, [plain, privileged, aeo], cancellationToken: Ct);
        asReviewer[plain].IsAllowed.Should().BeTrue();
        asReviewer[privileged].IsAllowed.Should().BeTrue();
        asReviewer[aeo].Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.RestrictionClass));

        (await Pdp(db).AuthorizeAsync(User(privilege), ws, Permission.DocumentView, aeo, Ct)).IsAllowed.Should().BeTrue();

        // Tightening a grant applies on the next decision; no cache, no index refresh (§24).
        await db.Core.ExecuteAsync(
            "DELETE FROM opportunity.restriction_class_grant WHERE workspace_id = @ws AND class_key = 'Privileged' AND role = 'Reviewer'", ("ws", ws));
        (await Pdp(db).AuthorizeAsync(User(reviewer), ws, Permission.DocumentView, privileged, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.RestrictionClass));
    }

    [Fact]
    public async Task Walls_hide_covered_documents_from_members_and_their_groups_even_workspace_admins()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        var grouped = await db.CreateUserAsync();
        var colleague = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, grouped);
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, colleague);
        var covered = await db.DocumentAsync(ws);
        var open = await db.DocumentAsync(ws);
        await db.WallAsync(ws, users: [admin], groups: ["cn=acme-deal"], documents: [covered]);

        foreach (var permission in new[] { Permission.DocumentView, Permission.CodingWrite, Permission.ExportCreate, Permission.DocumentDownloadNative })
        {
            (await Pdp(db).AuthorizeAsync(User(admin), ws, permission, covered, Ct))
                .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.EthicalWall), permission.Name());
        }

        (await Pdp(db).AuthorizeAsync(User(grouped, "cn=acme-deal"), ws, Permission.DocumentView, covered, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.EthicalWall));
        (await Pdp(db).AuthorizeAsync(User(grouped), ws, Permission.DocumentView, covered, Ct)).IsAllowed.Should().BeTrue();
        (await Pdp(db).AuthorizeAsync(User(colleague), ws, Permission.DocumentView, covered, Ct)).IsAllowed.Should().BeTrue();
        (await Pdp(db).AuthorizeAsync(User(admin), ws, Permission.DocumentView, open, Ct)).IsAllowed.Should().BeTrue();

        var visibility = await Pdp(db).GetVisibilityAsync(User(grouped, "cn=acme-deal"), ws, Ct);
        visibility.Filter!.WallIds.Should().ContainSingle();
    }

    [Fact]
    public async Task Walls_and_documents_of_another_workspace_are_invisible()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var other = await db.Core.CreateWorkspaceAsync();
        var user = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);
        await db.AssignAsync(other, WorkspaceRole.Reviewer, user);
        var foreignDocument = await db.DocumentAsync(other);
        await db.WallAsync(other, users: [user], groups: [], documents: [foreignDocument]);

        (await Pdp(db).AuthorizeAsync(User(user), ws, Permission.DocumentView, foreignDocument, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.DocumentNotFound), "a document ID from another workspace does not exist here");
        (await Pdp(db).GetVisibilityAsync(User(user), ws, Ct)).Filter!.WallIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleted_documents_are_not_found()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var admin = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, admin);
        var document = await db.DocumentAsync(ws);
        await db.Core.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET is_deleted = true, deleted_at = now() WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", ws), ("doc", document));

        (await Pdp(db).AuthorizeAsync(User(admin), ws, Permission.DocumentView, document, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.DocumentNotFound));
    }

    [Fact]
    public async Task Break_glass_needs_role_and_live_activation_and_stays_read_only()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var responder = await db.CreateUserAsync();
        var walledAdmin = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.BreakGlass, responder);
        await db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, walledAdmin);
        await db.AssignAsync(ws, WorkspaceRole.BreakGlass, walledAdmin);
        var document = await db.DocumentAsync(ws, RestrictionClasses.AttorneysEyesOnly);
        await db.WallAsync(ws, users: [walledAdmin], groups: [], documents: [document]);

        (await Pdp(db).AuthorizeAsync(User(responder), ws, Permission.DocumentView, document, Ct))
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.NotAMember), "the role alone is not access");

        await db.ActivateBreakGlassAsync(ws, responder, TimeSpan.FromHours(1), startedAgo: TimeSpan.FromHours(2));
        (await Pdp(db).AuthorizeAsync(User(responder), ws, Permission.DocumentView, document, Ct))
            .Outcome.Should().Be(AuthorizationOutcome.NotFound, "an expired activation is ignored");

        await db.ActivateBreakGlassAsync(ws, responder, TimeSpan.FromMinutes(60));
        await db.ActivateBreakGlassAsync(ws, walledAdmin, TimeSpan.FromMinutes(60));
        (await Pdp(db).AuthorizeAsync(User(responder), ws, Permission.DocumentView, document, Ct)).Should().Be(AuthorizationDecision.Allow(breakGlass: true));
        (await Pdp(db).AuthorizeAsync(User(walledAdmin), ws, Permission.DocumentView, document, Ct)).Should().Be(AuthorizationDecision.Allow(breakGlass: true));
        (await Pdp(db).AuthorizeAsync(User(responder), ws, Permission.CodingWrite, document, Ct)).Outcome.Should().Be(AuthorizationOutcome.Deny);
        (await Pdp(db).AuthorizeAsync(User(responder), ws, Permission.ExportCreate, Ct)).Outcome.Should().Be(AuthorizationOutcome.Deny);
        (await Pdp(db).AuthorizeAsync(User(walledAdmin), ws, Permission.DocumentDownloadNative, document, Ct))
            .Outcome.Should().Be(AuthorizationOutcome.NotFound, "break-glass never lifts walls for downloads, even for an admin");
        (await Pdp(db).GetVisibilityAsync(User(responder), ws, Ct)).Filter.Should().BeEquivalentTo(new VisibilityFilter([], [], BreakGlass: true));
        _audit.Events.Where(e => e.ActorId == responder.ToString() && e.ReasonCode == AuthorizationReasons.PermissionNotGranted)
            .Should().OnlyContain(e => e.AccessPath == AuditAccessPath.BreakGlass);

        await db.Core.ExecuteAsync(
            "UPDATE opportunity.break_glass_activation SET ended_at = now(), ended_reason = 'Ended' WHERE user_id = @user AND ended_at IS NULL",
            ("user", responder));
        (await Pdp(db).AuthorizeAsync(User(responder), ws, Permission.DocumentView, document, Ct)).Outcome.Should().Be(AuthorizationOutcome.NotFound);
    }

    [Fact]
    public async Task Break_glass_activations_are_bounded_and_cannot_be_extended_or_rewritten()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var user = await db.CreateUserAsync();

        var tooLong = () => db.ActivateBreakGlassAsync(ws, user, TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));
        (await tooLong.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("break_glass_activation_duration_ck");

        await db.ActivateBreakGlassAsync(ws, user, TimeSpan.FromHours(4));
        var extend = () => db.Core.ExecuteAsync("UPDATE opportunity.break_glass_activation SET expires_at = expires_at + interval '1 minute'");
        await extend.Should().ThrowAsync<PostgresException>();
        var rewrite = () => db.Core.ExecuteAsync("UPDATE opportunity.break_glass_activation SET reason = 'nothing to see'");
        await rewrite.Should().ThrowAsync<PostgresException>();

        var grantBreakGlassAClass = () => db.Core.ExecuteAsync(
            "INSERT INTO opportunity.restriction_class_grant (workspace_id, class_key, role) VALUES (@ws, 'AttorneysEyesOnly', 'BreakGlass')", ("ws", ws));
        await grantBreakGlassAClass.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Workspace_created_by_the_app_role_is_seeded_under_its_own_context()
    {
        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = Guid.CreateVersion7();
        await db.Core.InWorkspaceAsync(ws, async tx =>
        {
            await using var command = tx.Command(
                "INSERT INTO opportunity.workspace (workspace_id, name, display_time_zone) VALUES (@ws, 'App-created', 'UTC')");
            command.Parameters.AddWithValue("ws", ws);
            await command.ExecuteNonQueryAsync(Ct);
        });

        (await db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.restriction_class_grant WHERE workspace_id = @ws", ("ws", ws)))
            .Should().Be(RestrictionClasses.BuiltIn.Sum(c => c.Roles.Count));
    }

    private AuthorizationService Pdp(AuthorizationDatabase db) => new(db.Reader, _audit, TimeProvider.System);

    private static SecurityPrincipal User(Guid userId, params string[] groups) =>
        new() { UserId = userId, DisplayName = "user " + userId, Groups = groups };
}
