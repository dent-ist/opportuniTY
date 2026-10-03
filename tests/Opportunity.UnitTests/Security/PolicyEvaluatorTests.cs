using AwesomeAssertions;

using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;
using Opportunity.Security.Authorization;

namespace Opportunity.UnitTests.Security;

/// <summary>ADR-015 D5.2 evaluation order, deny precedence, walls over grants and break-glass limits (Q-11, Q-13, Q-45).</summary>
public sealed class PolicyEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid WallA = Guid.CreateVersion7();
    private static readonly Guid WallB = Guid.CreateVersion7();

    [Fact]
    public void Missing_or_deleting_workspace_is_not_found()
    {
        PolicyEvaluator.EvaluateWorkspace(null, Permission.DocumentView, Now)
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.WorkspaceNotFound));
        foreach (var status in new[] { WorkspaceStatus.Deleting, WorkspaceStatus.Purged })
        {
            PolicyEvaluator.EvaluateWorkspace(State(status: status, roles: [WorkspaceRole.WorkspaceAdmin]), Permission.DocumentView, Now)
                .Outcome.Should().Be(AuthorizationOutcome.NotFound);
        }
    }

    [Fact]
    public void Non_member_is_not_found_not_forbidden()
    {
        var decision = PolicyEvaluator.EvaluateWorkspace(State(roles: []), Permission.DocumentView, Now);
        decision.Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.NotAMember));
        PolicyEvaluator.EvaluateWorkspace(State(roles: []), null, Now).Outcome.Should().Be(AuthorizationOutcome.NotFound);
    }

    [Fact]
    public void Default_deny_member_without_the_permission_is_forbidden()
    {
        var reviewer = State(roles: [WorkspaceRole.Reviewer]);
        PolicyEvaluator.EvaluateWorkspace(reviewer, Permission.CodingWrite, Now).IsAllowed.Should().BeTrue();
        foreach (var permission in new[]
                 {
                     Permission.CodingWritePrivilege, Permission.ExportCreate, Permission.DocumentDownloadNative,
                     Permission.WorkspaceManageUsers, Permission.WorkspaceManageSecurity, Permission.JobManage,
                 })
        {
            PolicyEvaluator.EvaluateWorkspace(reviewer, permission, Now)
                .Should().Be(AuthorizationDecision.Deny(AuthorizationReasons.PermissionNotGranted), permission.Name());
        }
    }

    [Fact]
    public void Permissions_are_the_union_of_all_held_roles()
    {
        var both = State(roles: [WorkspaceRole.Reviewer, WorkspaceRole.Auditor]);
        PolicyEvaluator.EvaluateWorkspace(both, Permission.CodingWrite, Now).IsAllowed.Should().BeTrue();
        PolicyEvaluator.EvaluateWorkspace(both, Permission.AuditRead, Now).IsAllowed.Should().BeTrue();
        PolicyEvaluator.EvaluateWorkspace(both, Permission.ExportCreate, Now).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Document_must_exist()
    {
        PolicyEvaluator.EvaluateDocument(State(roles: [WorkspaceRole.WorkspaceAdmin]), Permission.DocumentView, null, Now)
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.DocumentNotFound));
    }

    [Fact]
    public void Restriction_class_hides_documents_from_roles_without_the_grant()
    {
        var aeo = Doc(classes: [RestrictionClasses.AttorneysEyesOnly]);
        PolicyEvaluator.EvaluateDocument(State(roles: [WorkspaceRole.Reviewer]), Permission.DocumentView, aeo, Now)
            .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.RestrictionClass));
        PolicyEvaluator.EvaluateDocument(State(roles: [WorkspaceRole.PrivilegeReviewer]), Permission.DocumentView, aeo, Now)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Every_class_on_a_document_must_be_granted()
    {
        var state = State(roles: [WorkspaceRole.Reviewer]);
        var both = Doc(classes: [RestrictionClasses.Privileged, RestrictionClasses.AttorneysEyesOnly]);
        PolicyEvaluator.EvaluateDocument(state, Permission.DocumentView, both, Now).Outcome.Should().Be(AuthorizationOutcome.NotFound);
        PolicyEvaluator.EvaluateDocument(state, Permission.DocumentView, Doc(classes: [RestrictionClasses.Privileged]), Now)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void A_class_without_grants_or_unknown_to_the_workspace_is_visible_to_nobody()
    {
        var admin = State(roles: [WorkspaceRole.WorkspaceAdmin]) with
        {
            ClassGrants = new Dictionary<string, IReadOnlySet<WorkspaceRole>> { ["Locked"] = new HashSet<WorkspaceRole>() },
        };
        PolicyEvaluator.EvaluateDocument(admin, Permission.DocumentView, Doc(classes: ["Locked"]), Now).Outcome.Should().Be(AuthorizationOutcome.NotFound);
        PolicyEvaluator.EvaluateDocument(admin, Permission.DocumentView, Doc(classes: ["Unknown"]), Now).Outcome.Should().Be(AuthorizationOutcome.NotFound);
    }

    [Fact]
    public void Wall_overrides_every_grant_including_workspace_admin()
    {
        var walledAdmin = State(roles: [WorkspaceRole.WorkspaceAdmin], walls: [WallA]);
        foreach (var permission in Enum.GetValues<Permission>())
        {
            PolicyEvaluator.EvaluateDocument(walledAdmin, permission, Doc(walls: [WallA]), Now)
                .Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.EthicalWall), permission.Name());
        }

        PolicyEvaluator.EvaluateDocument(walledAdmin, Permission.DocumentView, Doc(walls: [WallB]), Now)
            .IsAllowed.Should().BeTrue("a wall the principal is not a member of does not apply");
        PolicyEvaluator.EvaluateWorkspace(walledAdmin, Permission.WorkspaceManageUsers, Now)
            .IsAllowed.Should().BeTrue("walls deny documents, not workspace administration");
    }

    [Fact]
    public void Break_glass_role_without_activation_is_not_membership()
    {
        var holder = State(roles: [WorkspaceRole.BreakGlass]);
        PolicyEvaluator.EvaluateWorkspace(holder, Permission.DocumentView, Now).Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.NotAMember));

        var expired = holder with { BreakGlassExpiresAt = Now };
        PolicyEvaluator.EvaluateWorkspace(expired, Permission.DocumentView, Now).Outcome.Should().Be(AuthorizationOutcome.NotFound, "expiry is exclusive");
    }

    [Fact]
    public void Activation_without_the_break_glass_role_lifts_nothing()
    {
        var reviewer = State(roles: [WorkspaceRole.Reviewer], walls: [WallA]) with { BreakGlassExpiresAt = Now.AddMinutes(30) };
        PolicyEvaluator.EvaluateDocument(reviewer, Permission.DocumentView, Doc(walls: [WallA]), Now).Outcome.Should().Be(AuthorizationOutcome.NotFound);
    }

    [Fact]
    public void Active_break_glass_is_read_only()
    {
        var active = State(roles: [WorkspaceRole.BreakGlass]) with { BreakGlassExpiresAt = Now.AddMinutes(60) };
        foreach (var permission in Enum.GetValues<Permission>())
        {
            var decision = PolicyEvaluator.EvaluateWorkspace(active, permission, Now);
            var readOnly = permission is Permission.DocumentView or Permission.SearchExecute or Permission.AuditRead;
            decision.Should().Be(readOnly ? AuthorizationDecision.Allow(breakGlass: true) : AuthorizationDecision.Deny(AuthorizationReasons.PermissionNotGranted), permission.Name());
        }
    }

    [Fact]
    public void Active_break_glass_lifts_classes_and_walls_for_reads_only()
    {
        var walledAdmin = State(roles: [WorkspaceRole.WorkspaceAdmin, WorkspaceRole.BreakGlass], walls: [WallA])
            with
        { BreakGlassExpiresAt = Now.AddMinutes(5) };
        var document = Doc(classes: ["Locked"], walls: [WallA]);

        PolicyEvaluator.EvaluateDocument(walledAdmin, Permission.DocumentView, document, Now)
            .Should().Be(AuthorizationDecision.Allow(breakGlass: true));
        foreach (var write in new[] { Permission.CodingWrite, Permission.DocumentDownloadNative, Permission.DocumentPrint, Permission.ExportCreate, Permission.ProductionCreate, Permission.RedactionApply })
        {
            PolicyEvaluator.EvaluateDocument(walledAdmin, write, document, Now)
                .Outcome.Should().Be(AuthorizationOutcome.NotFound, "{0} is never lifted by break-glass (Q-45)", write.Name());
        }

        PolicyEvaluator.EvaluateDocument(walledAdmin, Permission.DocumentView, document, Now.AddMinutes(6))
            .Outcome.Should().Be(AuthorizationOutcome.NotFound, "an expired activation lifts nothing");
    }

    [Fact]
    public void Normal_allow_does_not_claim_break_glass()
    {
        var both = State(roles: [WorkspaceRole.Reviewer, WorkspaceRole.BreakGlass]) with { BreakGlassExpiresAt = Now.AddMinutes(5) };
        PolicyEvaluator.EvaluateDocument(both, Permission.DocumentView, Doc(), Now).Should().Be(AuthorizationDecision.Allow());
    }

    [Fact]
    public void Visibility_lists_denied_classes_and_applicable_walls()
    {
        var reviewer = State(roles: [WorkspaceRole.Reviewer], walls: [WallB, WallA]);
        var filter = PolicyEvaluator.Visibility(reviewer, Now);
        filter.DeniedClasses.Should().Equal(RestrictionClasses.AttorneysEyesOnly);
        filter.WallIds.Should().BeEquivalentTo([WallA, WallB]);
        filter.BreakGlass.Should().BeFalse();

        var admin = PolicyEvaluator.Visibility(State(roles: [WorkspaceRole.WorkspaceAdmin]), Now);
        admin.DeniedClasses.Should().BeEmpty();

        var glass = PolicyEvaluator.Visibility(reviewer with { Roles = new HashSet<WorkspaceRole> { WorkspaceRole.BreakGlass }, BreakGlassExpiresAt = Now.AddHours(1) }, Now);
        glass.Should().BeEquivalentTo(new VisibilityFilter([], [], BreakGlass: true));
    }

    internal static PrincipalSecurityState State(
        IEnumerable<WorkspaceRole> roles, IEnumerable<Guid>? walls = null, WorkspaceStatus status = WorkspaceStatus.Active) =>
        new(
            status,
            roles.ToHashSet(),
            RestrictionClasses.BuiltIn.ToDictionary(c => c.ClassKey, c => (IReadOnlySet<WorkspaceRole>)c.Roles.ToHashSet()),
            (walls ?? []).ToHashSet(),
            BreakGlassExpiresAt: null);

    internal static DocumentSecurityAttributes Doc(IEnumerable<string>? classes = null, IEnumerable<Guid>? walls = null) =>
        new([.. classes ?? []], [.. walls ?? []]);
}
