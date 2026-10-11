using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Productions;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Security;
using Opportunity.Data;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E13-T01 AC 3: changing a member to Privilege Status = Withhold blocks finalizing an unfinalized production at once,
/// read from the coding store (no search index involved), and a finalization waits for Privilege Status changes still
/// in flight before it checks.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PrivilegeProductionGateTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_withheld_member_blocks_finalization_until_its_privilege_call_changes()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = await h.FamiliesAsync(ws, "DOC", [(1, "pdf")], [(2, "pdf")]);
        var snapshot = await h.SnapshotAsync(ws, user, docs);
        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("PRIV"));
        var allocated = await h.AllocateAsync(ws, user, draft.ProductionId);
        allocated.BatesState.Should().Be(BatesAllocationState.Allocated, allocated.BatesReason);

        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        int Status(string key) => PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value;
        var basis = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;

        // Withheld after the frozen set and the Bates numbers were made: the very next finalization is refused.
        (await CodeAsync(h, ws, user, docs[1],
            CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(Status(PrivilegeFields.Keys.Withhold))),
            CodingFieldOperation.AddChoices(PrivilegeFields.Basis, basis))).Outcome.Should().Be(CodingWriteOutcome.Applied);
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);
        var refused = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, Ct);
        refused.Status.Should().Be(ProductionOutcomeStatus.PrivilegeWithheld);
        refused.Reason.Should().Contain("Withhold");
        (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!.Status.Should().Be(ProductionStatus.Draft);

        // A Privilege Status change still in flight (its transaction holds the gate) is waited for.
        await using (var inFlight = await WorkspaceTransaction.BeginAsync(h.Db.AppDataSource, ws, Ct))
        {
            await using (var gate = inFlight.Command(
                "SELECT pg_advisory_xact_lock_shared(hashtextextended('opportunity.privilege-gate ' || @ws::text, 0))"))
            {
                gate.Parameters.AddWithValue("ws", ws);
                await gate.ExecuteNonQueryAsync(Ct);
            }

            (await CodeAsync(h, ws, user, docs[1],
                CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(Status(PrivilegeFields.Keys.Redact))))).Outcome
                .Should().Be(CodingWriteOutcome.Applied, "coding writes share the gate");
            var finalizing = service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion,
                ProductionHarness.Unimaged(new ProductionQcOverride(ProductionQcCheck.RedactWithoutRedactions, "Redactions are drawn on the produced copy.")), Ct);
            (await Task.WhenAny(finalizing, Task.Delay(TimeSpan.FromMilliseconds(750), Ct))).Should().NotBeSameAs(finalizing,
                "finalization waits for the in-flight privilege change");
            await inFlight.CommitAsync(Ct);

            var finalized = await finalizing;
            finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
        }
    }

    private static Task<CodingWriteResult> CodeAsync(ProductionHarness h, Guid ws, Guid user, Guid documentId, params CodingFieldOperation[] operations) =>
        h.Db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(user, CodingActorType.Human),
            Documents = [new CodingTarget(documentId)],
            Operations = operations,
        }, Ct);
}
