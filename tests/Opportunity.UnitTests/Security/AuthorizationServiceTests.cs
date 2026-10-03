using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;

namespace Opportunity.UnitTests.Security;

/// <summary>The PDP service: audit of every denial, batching, per-scope state reuse and no document reads for outsiders.</summary>
public sealed class AuthorizationServiceTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Wall = Guid.CreateVersion7();
    private static readonly SecurityPrincipal Alice = new() { UserId = Guid.CreateVersion7(), DisplayName = "Alice", CorrelationId = "corr-1" };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAuditEventWriter _audit = new();
    private readonly FakeReader _reader = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Allow_writes_no_audit_and_deny_writes_authz_denied_with_the_true_reason()
    {
        _reader.State = PolicyEvaluatorTests.State([WorkspaceRole.Reviewer]);
        var pdp = Create();

        (await pdp.AuthorizeAsync(Alice, Workspace, Permission.DocumentView, Ct)).IsAllowed.Should().BeTrue();
        _audit.Events.Should().BeEmpty();

        (await pdp.AuthorizeAsync(Alice, Workspace, Permission.ExportCreate, Ct)).Outcome.Should().Be(AuthorizationOutcome.Deny);
        var denied = _audit.Events.Should().ContainSingle().Subject;
        denied.Category.Should().Be("AuthZ");
        denied.Action.Should().Be("Denied");
        denied.Outcome.Should().Be(AuditOutcome.Denied);
        denied.ReasonCode.Should().Be(AuthorizationReasons.PermissionNotGranted);
        denied.WorkspaceId.Should().Be(Workspace);
        denied.ActorId.Should().Be(Alice.UserId.ToString());
        denied.CorrelationId.Should().Be("corr-1");
        denied.Details["permission"].Should().Be("Export.Create");
        denied.AccessPath.Should().Be(AuditAccessPath.Normal);
    }

    [Fact]
    public async Task Non_member_gets_not_found_audited_and_no_document_is_read()
    {
        _reader.State = PolicyEvaluatorTests.State([]);
        _reader.Documents[Guid.CreateVersion7()] = DocumentSecurityAttributes.Unrestricted;
        var pdp = Create();
        var ids = _reader.Documents.Keys.ToList();

        var first = await pdp.AuthorizeManyAsync(Alice, Workspace, Permission.DocumentView, ids, cancellationToken: Ct);
        first.Values.Should().OnlyContain(d => d.Outcome == AuthorizationOutcome.NotFound && d.Reason == AuthorizationReasons.NotAMember);

        // Second call in the same scope: the cached principal state already fails, so documents are not even read.
        _reader.DocumentReads = 0;
        await pdp.AuthorizeManyAsync(Alice, Workspace, Permission.DocumentView, ids, cancellationToken: Ct);
        _reader.DocumentReads.Should().Be(0);

        _audit.Events.Should().HaveCount(2).And.OnlyContain(e => e.ReasonCode == AuthorizationReasons.NotAMember && e.ResourceType == "Workspace");
    }

    [Fact]
    public async Task Authorize_many_decides_every_id_and_audits_each_denied_document()
    {
        var visible = Guid.CreateVersion7();
        var walled = Guid.CreateVersion7();
        var aeo = Guid.CreateVersion7();
        var missing = Guid.CreateVersion7();
        _reader.State = PolicyEvaluatorTests.State([WorkspaceRole.Reviewer], walls: [Wall]);
        _reader.Documents[visible] = DocumentSecurityAttributes.Unrestricted;
        _reader.Documents[walled] = PolicyEvaluatorTests.Doc(walls: [Wall]);
        _reader.Documents[aeo] = PolicyEvaluatorTests.Doc(classes: [RestrictionClasses.AttorneysEyesOnly]);
        var pdp = Create();

        var result = await pdp.AuthorizeManyAsync(Alice, Workspace, Permission.DocumentView, [visible, walled, aeo, missing, visible], cancellationToken: Ct);

        result.Should().HaveCount(4);
        result[visible].IsAllowed.Should().BeTrue();
        result[walled].Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.EthicalWall));
        result[aeo].Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.RestrictionClass));
        result[missing].Should().Be(AuthorizationDecision.NotFound(AuthorizationReasons.DocumentNotFound));
        _audit.Events.Should().HaveCount(3).And.OnlyContain(e => e.ResourceType == "Document");
        _audit.Events.Select(e => e.ResourceId).Should().BeEquivalentTo([walled.ToString(), aeo.ToString(), missing.ToString()]);
        _reader.PrincipalReads.Should().Be(1);
        _reader.DocumentReads.Should().Be(1, "one batched read per call");
    }

    [Fact]
    public async Task Summary_mode_writes_one_event_with_counts_for_the_search_post_filter()
    {
        _reader.State = PolicyEvaluatorTests.State([WorkspaceRole.Reviewer], walls: [Wall]);
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.CreateVersion7()).ToList();
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
        {
            _reader.Documents[id] = i % 10 == 0 ? PolicyEvaluatorTests.Doc(walls: [Wall]) : DocumentSecurityAttributes.Unrestricted;
        }

        var result = await Create().AuthorizeManyAsync(Alice, Workspace, Permission.DocumentView, ids, DenialAudit.Summary, Ct);

        result.Values.Count(d => d.IsAllowed).Should().Be(90);
        var summary = _audit.Events.Should().ContainSingle().Subject;
        summary.ReasonCode.Should().Be(AuthorizationReasons.EthicalWall);
        summary.Details["denied"].Should().Be("10");
        summary.Details["requested"].Should().Be("100");
        summary.Details["denied.EthicalWall"].Should().Be("10");
    }

    [Fact]
    public async Task Principal_state_is_read_once_per_scope_and_fresh_in_the_next()
    {
        _reader.State = PolicyEvaluatorTests.State([WorkspaceRole.Reviewer]);
        var pdp = Create();
        await pdp.AuthorizeAsync(Alice, Workspace, Permission.DocumentView, Ct);
        await pdp.AuthorizeMembershipAsync(Alice, Workspace, Ct);
        await pdp.GetVisibilityAsync(Alice, Workspace, Ct);
        _reader.PrincipalReads.Should().Be(1);

        // Role revoked between requests: the next scope sees it immediately (no cross-request cache, D5.5).
        _reader.State = PolicyEvaluatorTests.State([]);
        (await Create().AuthorizeAsync(Alice, Workspace, Permission.DocumentView, Ct)).Outcome.Should().Be(AuthorizationOutcome.NotFound);
        _reader.PrincipalReads.Should().Be(2);
    }

    [Fact]
    public async Task Anonymous_principal_is_denied_without_reading_state()
    {
        var anonymous = new SecurityPrincipal { UserId = Guid.Empty, DisplayName = "anonymous" };
        var pdp = Create();

        (await pdp.AuthorizeAsync(anonymous, Workspace, Permission.DocumentView, Ct)).Reason.Should().Be(AuthorizationReasons.Unauthenticated);
        (await pdp.AuthorizeManyAsync(anonymous, Workspace, Permission.DocumentView, [Guid.CreateVersion7()], cancellationToken: Ct))
            .Values.Should().OnlyContain(d => !d.IsAllowed);
        (await pdp.GetVisibilityAsync(anonymous, Workspace, Ct)).Filter.Should().BeNull();
        _reader.PrincipalReads.Should().Be(0);
        _reader.DocumentReads.Should().Be(0);
    }

    [Fact]
    public async Task Visibility_requires_search_and_carries_the_filter()
    {
        _reader.State = PolicyEvaluatorTests.State([WorkspaceRole.Reviewer], walls: [Wall]);
        var result = await Create().GetVisibilityAsync(Alice, Workspace, Ct);
        result.Decision.IsAllowed.Should().BeTrue();
        result.Filter!.DeniedClasses.Should().Equal(RestrictionClasses.AttorneysEyesOnly);
        result.Filter.WallIds.Should().Equal(Wall);

        _reader.State = PolicyEvaluatorTests.State([]);
        var outsider = await Create().GetVisibilityAsync(Alice, Workspace, Ct);
        outsider.Decision.Outcome.Should().Be(AuthorizationOutcome.NotFound);
        outsider.Filter.Should().BeNull();
    }

    [Fact]
    public async Task Denials_during_break_glass_carry_the_break_glass_access_path()
    {
        _reader.State = PolicyEvaluatorTests.State([WorkspaceRole.BreakGlass]) with { BreakGlassExpiresAt = _time.GetUtcNow().AddMinutes(60) };
        var pdp = Create();

        (await pdp.AuthorizeAsync(Alice, Workspace, Permission.DocumentView, Ct)).Should().Be(AuthorizationDecision.Allow(breakGlass: true));
        (await pdp.AuthorizeAsync(Alice, Workspace, Permission.CodingWrite, Ct)).Outcome.Should().Be(AuthorizationOutcome.Deny);
        _audit.Events.Should().ContainSingle().Which.AccessPath.Should().Be(AuditAccessPath.BreakGlass);

        _time.Advance(TimeSpan.FromMinutes(61));
        (await pdp.AuthorizeAsync(Alice, Workspace, Permission.DocumentView, Ct)).Outcome.Should().Be(AuthorizationOutcome.NotFound,
            "the activation expires mid-scope, and expiry is re-evaluated on every decision");
    }

    [Fact]
    public async Task Empty_batch_reads_nothing()
    {
        (await Create().AuthorizeManyAsync(Alice, Workspace, Permission.DocumentView, [], cancellationToken: Ct)).Should().BeEmpty();
        _reader.PrincipalReads.Should().Be(0);
    }

    private AuthorizationService Create() => new(_reader, _audit, _time);

    private sealed class FakeReader : ISecurityStateReader
    {
        public PrincipalSecurityState? State { get; set; }

        public Dictionary<Guid, DocumentSecurityAttributes> Documents { get; } = [];

        public int PrincipalReads { get; set; }

        public int DocumentReads { get; set; }

        public Task<SecurityStateRead> ReadAsync(
            Guid workspaceId, SecurityPrincipal principal, bool includePrincipal, IReadOnlyCollection<Guid>? documentIds, CancellationToken cancellationToken = default)
        {
            PrincipalReads += includePrincipal ? 1 : 0;
            DocumentReads += documentIds is { Count: > 0 } ? 1 : 0;
            var documents = (documentIds ?? []).Where(Documents.ContainsKey).Distinct().ToDictionary(id => id, id => Documents[id]);
            return Task.FromResult(new SecurityStateRead(includePrincipal ? State : null, documents));
        }
    }
}
