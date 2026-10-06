using AwesomeAssertions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Security;
using Opportunity.Core.Fields;
using Opportunity.Core.QueryLanguage;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;
using Opportunity.Search.Querying;
using Opportunity.Security.Authorization;
using Opportunity.UnitTests.Search;

namespace Opportunity.UnitTests.Security;

/// <summary>
/// E05-T06 model tests: the PDP against an independent reference model of ADR-015 D5.2 over randomized principals and
/// documents (walls deny over every grant, classes need a granted role, break-glass lifts both for reads only), the
/// field-level policy against its definition, and hidden fields binding exactly like fields that do not exist.
/// </summary>
public sealed class DocumentSecurityModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly WorkspaceRole[] Roles = Enum.GetValues<WorkspaceRole>();
    private static readonly Permission[] Permissions = Enum.GetValues<Permission>();
    private static readonly string[] Classes = ["Privileged", "Confidential", "AttorneysEyesOnly", "MatterB"];
    private static readonly Guid[] Walls = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Document_decisions_match_the_reference_model_for_random_principals_and_documents()
    {
        var random = new Random(51);
        for (var i = 0; i < 5_000; i++)
        {
            var roles = Subset(random, Roles).ToHashSet();
            var grants = Classes.ToDictionary(c => c, _ => (IReadOnlySet<WorkspaceRole>)Subset(random, Roles.Where(r => r != WorkspaceRole.BreakGlass)).ToHashSet());
            var memberWalls = Subset(random, Walls).ToHashSet();
            DateTimeOffset? breakGlass = random.Next(4) switch { 0 => Now.AddMinutes(30), 1 => Now.AddMinutes(-1), _ => null };
            var state = new PrincipalSecurityState(WorkspaceStatus.Active, roles, grants, memberWalls, breakGlass);
            var document = new DocumentSecurityAttributes([.. Subset(random, Classes)], [.. Subset(random, Walls)]);
            var permission = Permissions[random.Next(Permissions.Length)];

            var actual = PolicyEvaluator.EvaluateDocument(state, permission, document, Now);
            var expected = Reference(state, permission, document);
            actual.Outcome.Should().Be(expected, "case {0}: roles {1}, permission {2}, classes {3}, walls {4}/{5}, break-glass {6}",
                i, string.Join(',', roles), permission.Name(), string.Join(',', document.RestrictionClasses), document.WallIds.Count,
                memberWalls.Count, breakGlass);
        }
    }

    [Fact]
    public void A_wall_naming_the_principal_hides_the_document_whatever_its_roles_unless_break_glass_reads()
    {
        var random = new Random(13);
        foreach (var permission in Permissions)
        {
            for (var i = 0; i < 50; i++)
            {
                var roles = Subset(random, Roles).Append(WorkspaceRole.WorkspaceAdmin).ToHashSet();
                var state = new PrincipalSecurityState(WorkspaceStatus.Active, roles, new Dictionary<string, IReadOnlySet<WorkspaceRole>>(),
                    new HashSet<Guid> { Walls[0] }, BreakGlassExpiresAt: null);
                PolicyEvaluator.EvaluateDocument(state, permission, new DocumentSecurityAttributes([], [Walls[0]]), Now).Outcome
                    .Should().NotBe(AuthorizationOutcome.Allow, "{0} with {1}", permission.Name(), string.Join(',', roles));
            }
        }
    }

    [Fact]
    public void Break_glass_holder_check_needs_the_assignment_not_membership_or_an_activation()
    {
        static PrincipalSecurityState S(params WorkspaceRole[] roles) =>
            new(WorkspaceStatus.Active, roles.ToHashSet(), new Dictionary<string, IReadOnlySet<WorkspaceRole>>(), new HashSet<Guid>(), null);

        PolicyEvaluator.EvaluateBreakGlassHolder(S(WorkspaceRole.BreakGlass)).IsAllowed.Should().BeTrue();
        PolicyEvaluator.EvaluateBreakGlassHolder(S(WorkspaceRole.WorkspaceAdmin)).Should().Be(AuthorizationDecision.Deny(AuthorizationReasons.PermissionNotGranted));
        PolicyEvaluator.EvaluateBreakGlassHolder(S()).Outcome.Should().Be(AuthorizationOutcome.NotFound);
        PolicyEvaluator.EvaluateBreakGlassHolder(null).Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.WorkspaceNotFound));
        PolicyEvaluator.EvaluateBreakGlassHolder(S(WorkspaceRole.BreakGlass) with { WorkspaceStatus = WorkspaceStatus.Deleting })
            .Outcome.Should().Be(AuthorizationOutcome.NotFound);
    }

    [Fact]
    public void Field_access_matches_its_definition_for_random_restrictions_and_roles()
    {
        var random = new Random(7);
        var catalog = new FieldCatalog([.. Enumerable.Range(1000, 12).Select(Field)], []);
        for (var i = 0; i < 2_000; i++)
        {
            var held = Subset(random, Roles).ToHashSet();
            var restrictions = Enumerable.Range(1000, 14)
                .Where(_ => random.Next(2) == 0)
                .Select(id =>
                {
                    var visible = Subset(random, Roles.Where(r => r != WorkspaceRole.BreakGlass)).ToList();
                    return new FieldRestrictionState(id, visible, [.. Subset(random, visible)], Now, 1);
                })
                .ToList();

            var (hidden, readOnly) = FieldAccessPolicy.Evaluate(new FieldAccessState(held, restrictions), catalog);

            foreach (var r in restrictions.Where(r => catalog.Find(r.FieldId) is not null))
            {
                var sees = r.VisibleTo.Any(held.Contains);
                var edits = r.EditableBy.Any(held.Contains);
                hidden.Contains(r.FieldId).Should().Be(!sees);
                readOnly.Contains(r.FieldId).Should().Be(sees && !edits);
            }

            hidden.Concat(readOnly).Should().OnlyContain(id => restrictions.Any(r => r.FieldId == id) && catalog.Find(id) != null,
                "unrestricted and unknown fields are never affected");
            hidden.Overlaps(readOnly).Should().BeFalse();
        }
    }

    [Fact]
    public void Break_glass_never_grants_a_restricted_field()
    {
        var catalog = new FieldCatalog([Field(1000)], []);
        var restriction = new FieldRestrictionState(1000, [WorkspaceRole.WorkspaceAdmin, WorkspaceRole.BreakGlass], [WorkspaceRole.BreakGlass], Now, 1);
        var (hidden, _) = FieldAccessPolicy.Evaluate(new FieldAccessState(new HashSet<WorkspaceRole> { WorkspaceRole.BreakGlass }, [restriction]), catalog);
        hidden.Should().Equal(1000);
    }

    [Fact]
    public async Task A_hidden_field_binds_exactly_like_a_field_that_does_not_exist()
    {
        var planner = PlannerFixture.Planner();
        var hidden = new HashSet<int> { PlannerFixture.Hot };
        async Task<SearchTranslation> Plan(string text, IReadOnlySet<int>? hide) => await planner.TranslateAsync(
            QueryParser.Parse(text).Ast!, new SearchTranslationContext(PlannerFixture.Workspace, 2, QueryLimits.Default, HiddenFieldIds: hide), Ct);

        (await Plan("hot:true", null)).Success.Should().BeTrue();
        var asHidden = await Plan("hot:true", hidden);
        var asMissing = await Plan("hoz:true", null);
        asHidden.Success.Should().BeFalse();
        asHidden.Errors.Select(e => e.Code).Should().Equal(asMissing.Errors.Select(e => e.Code));
        asHidden.Errors.Single().Code.Should().Be(SearchQueryErrorCodes.UnknownField);
        asHidden.Errors.Single().Expected.Should().NotContain(e => e.Contains("hot", StringComparison.OrdinalIgnoreCase),
            "suggestions never name a hidden field");
    }

    [Fact]
    public void Catalog_without_drops_the_fields_and_their_choices()
    {
        var catalog = PlannerFixture.Catalog;
        var without = catalog.Without(new HashSet<int> { PlannerFixture.Issues, 999_999 });
        without.Find(PlannerFixture.Issues).Should().BeNull();
        without.ChoicesOf(PlannerFixture.Issues).Should().BeEmpty();
        without.Find(PlannerFixture.Hot).Should().NotBeNull();
        catalog.Find(PlannerFixture.Issues).Should().NotBeNull("the cached catalogue is not changed");
        catalog.Without(new HashSet<int>()).Should().BeSameAs(catalog);
    }

    /// <summary>ADR-015 D5.2 written independently of the evaluator.</summary>
    private static AuthorizationOutcome Reference(PrincipalSecurityState s, Permission p, DocumentSecurityAttributes d)
    {
        var normalRoles = s.Roles.Where(r => r != WorkspaceRole.BreakGlass).ToList();
        var breakGlass = s.Roles.Contains(WorkspaceRole.BreakGlass) && s.BreakGlassExpiresAt > Now;
        if (normalRoles.Count == 0 && !breakGlass)
        {
            return AuthorizationOutcome.NotFound;
        }

        var readPermission = p is Permission.DocumentView or Permission.SearchExecute or Permission.AuditRead;
        var normallyGranted = normalRoles.Any(r => RoleCatalog.Get(r).Grants.Contains(p));
        if (!normallyGranted && !(breakGlass && readPermission))
        {
            return AuthorizationOutcome.Deny;
        }

        var walled = d.WallIds.Any(s.WallIds.Contains);
        var classHidden = d.RestrictionClasses.Any(c => !s.ClassGrants.TryGetValue(c, out var granted) || !normalRoles.Any(granted.Contains));
        if (walled || classHidden)
        {
            return breakGlass && readPermission ? AuthorizationOutcome.Allow : AuthorizationOutcome.NotFound;
        }

        return AuthorizationOutcome.Allow;
    }

    private static IEnumerable<T> Subset<T>(Random random, IEnumerable<T> items) => items.Where(_ => random.Next(3) == 0);

    private static FieldDefinition Field(int id) => new()
    {
        FieldId = id,
        Name = "Field " + id,
        Type = FieldType.Keyword,
        Storage = FieldStorage.Coding,
    };
}
