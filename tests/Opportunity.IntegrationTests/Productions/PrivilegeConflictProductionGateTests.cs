using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Productions;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data.Coding;
using Opportunity.Data.Identity;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E13-T02 AC 2: a production whose members' families or duplicate groups have unresolved privilege conflicts cannot be
/// finalized, unless an authorized override with a reason is given; the override is recorded in the manifest and
/// audited (<c>Privilege.ConflictOverride</c>). The refusal names and counts nothing (Q-52). The report scoped to the
/// production lists only conflicts that touch its members.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PrivilegeConflictProductionGateTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unresolved_conflicts_block_finalization_until_resolved_or_overridden_with_an_audited_reason()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        // A: GATE0001 + GATE0002; B: GATE0003; C: GATE0004; D: GATE0005 + GATE0006 (not produced); E: GATE0007.
        var docs = await h.FamiliesAsync(ws, "GATE", [(1, "pdf"), (1, "pdf")], [(1, "pdf")], [(1, "pdf")], [(1, "pdf"), (1, "pdf")], [(1, "pdf")]);
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        int Status(string key) => PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value;
        var basis = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;
        async Task CodeAsync(Guid document, string key) => (await h.Db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(user, CodingActorType.Human),
            Documents = [new CodingTarget(document)],
            Operations = key == PrivilegeFields.Keys.NotPrivileged
                ? [CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(Status(key)))]
                : [CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(Status(key))), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, basis)],
        }, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);

        // The production holds A's parent and B; A's attachment is withheld (a misleading family, Q-14). D conflicts too, outside it.
        await CodeAsync(docs[1], PrivilegeFields.Keys.Withhold);
        await CodeAsync(docs[4], PrivilegeFields.Keys.Withhold);
        var snapshot = await h.SnapshotAsync(ws, user, [docs[0], docs[2]]);
        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("GATE"));
        var allocated = await h.AllocateAsync(ws, user, draft.ProductionId);
        allocated.BatesState.Should().Be(BatesAllocationState.Allocated, allocated.BatesReason);
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);

        var refused = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, Ct);
        refused.Status.Should().Be(ProductionOutcomeStatus.PrivilegeConflicts);
        refused.Reason.Should().NotContain("GATE").And.NotMatchRegex("[0-9]");

        // The report scoped to the production shows family A (its produced member flagged), not D.
        var report = (await Reports(h).ReportAsync(principal, ws, null, draft.ProductionId, Ct)).Report!;
        report.Groups.Should().ContainSingle().Which.GroupId.Should().Be(docs[0]);
        report.Groups[0].Members.Select(m => (m.DocumentId, m.InProduction)).Should().Equal((docs[0], true), (docs[1], false));
        (await Reports(h).ReportAsync(principal, ws, null, null, Ct)).Report!.Groups.Select(g => g.GroupId).Should().Equal(docs[0], docs[4]);

        // Resolving the family clears it; a duplicate of B coded Withhold is a new conflict (B would go out unwithheld).
        await CodeAsync(docs[1], PrivilegeFields.Keys.NotPrivileged);
        var group = Guid.CreateVersion7();
        await h.Db.ExecuteAsync(
            "INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @group, 1, 1, @value)",
            ("ws", ws), ("group", group), ("value", "DUP-" + group.ToString("N")));
        await h.Db.ExecuteAsync(
            "UPDATE opportunity.document SET duplicate_group_id = @group, is_duplicate_primary = (document_id = @primary) WHERE workspace_id = @ws AND document_id = ANY(@docs)",
            ("ws", ws), ("group", group), ("primary", docs[2]), ("docs", new[] { docs[2], docs[3] }));
        await CodeAsync(docs[3], PrivilegeFields.Keys.Withhold);
        (await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, Ct)).Status
            .Should().Be(ProductionOutcomeStatus.PrivilegeConflicts, "a duplicate of a member is withheld");

        (await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, "  ", Ct)).Status
            .Should().Be(ProductionOutcomeStatus.Invalid, "an override needs a reason");

        var finalized = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion,
            "Duplicate withheld under the clawback agreement; produced copy is the public filing.", Ct);
        finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
        var stored = (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!;
        stored.Status.Should().Be(ProductionStatus.Finalized);
        using (var manifest = JsonDocument.Parse(stored.Manifest!))
        {
            var recorded = manifest.RootElement.GetProperty("privilegeConflictOverride");
            recorded.GetProperty("reason").GetString().Should().StartWith("Duplicate withheld under the clawback agreement");
            recorded.GetProperty("by").GetGuid().Should().Be(user);
        }

        Convert.ToHexStringLower(stored.ManifestSha256!).Should().Be(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stored.Manifest!))));
        (await h.Db.ScalarAsync<string>(
            """
            SELECT details->>'Reason' FROM audit.audit_event
            WHERE workspace_id = @ws AND category = 'Privilege' AND action = 'ConflictOverride' AND resource_id = @id
            """, ("ws", ws), ("id", draft.ProductionId.ToString())))
            .Should().StartWith("Duplicate withheld under the clawback agreement");

        // An override given when nothing conflicts is not used: the manifest records none and nothing is audited.
        var clean = await h.SnapshotAsync(ws, user, [docs[6]]);
        var second = await h.CreateOkAsync(ws, user, clean.SnapshotId, ProductionHarness.Spec("GATEB"));
        var secondAllocated = await h.AllocateAsync(ws, user, second.ProductionId);
        (await service.FinalizeAsync(principal, ws, second.ProductionId, secondAllocated.RowVersion, "Just in case.", Ct)).Status
            .Should().Be(ProductionOutcomeStatus.Ok);
        (await h.Store.GetAsync(ws, second.ProductionId, Ct))!.Manifest.Should().NotContain("privilegeConflictOverride");
        (await h.Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Privilege' AND action = 'ConflictOverride'",
            ("ws", ws))).Should().Be(1);
    }

    private static PrivilegeConflictService Reports(ProductionHarness h) => new(
        h.Db.Coding,
        new CodingPropagationRepository(h.Db.AppDataSource),
        h.Db.Fields,
        new UnrestrictedFieldAccess(),
        h.Pdp(),
        new PostgresUserDirectory(h.Db.AppDataSource),
        h.Store,
        null!,
        null!,
        TimeProvider.System);
}
