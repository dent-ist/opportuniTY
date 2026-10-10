using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E12-T04 against PostgreSQL: designations are frozen per member at finalization under the production's family rule
/// (configurable, audited) and overrides (with an audited reason); a designated member without a stamp, or with a
/// designation the specification does not list, blocks finalization; a designation change after production yields a
/// re-designation report and an overlay load file; AEO documents are visible to the designated roles only.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class DesignationProductionTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Finalization_freezes_each_documents_designation_by_the_family_rule_as_the_resolver_decides_and_audits_it()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var (ws, user, designation) = await WorkspaceAsync(h);
        // Families: A (parent, child), B (alone), C (parent, child), D (parent, child, child).
        var docs = await h.FamiliesAsync(ws, "DES", [(2, "pdf"), (1, "pdf")], [(1, "pdf")], [(1, "pdf"), (1, "pdf")], [(1, "pdf"), (1, "pdf"), (2, "pdf")]);
        await CodeAsync(h, ws, user, designation.FieldId, (docs[0], designation.Confidential), (docs[1], designation.Aeo), (docs[2], designation.Confidential),
            (docs[3], designation.None), (docs[6], designation.None), (docs[7], designation.Confidential));
        var snapshot = await h.SnapshotAsync(ws, user, docs);
        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("DES"));
        var allocated = await h.AllocateAsync(ws, user, draft.ProductionId);
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);

        // The draft's designations are computed live, exactly as the pure resolver decides.
        var plan = ProductionService.PlanOf(allocated);
        plan.Should().BeEquivalentTo(new { designation.FieldId, Rule = DesignationFamilyRule.HighestInFamily, StampsDesignation = true });
        var (live, restricted, _) = await service.ListDesignationsAsync(principal, allocated, 0, 100, Ct);
        restricted.Should().Be(0);
        var members = await h.AssignmentAsync(ws, draft.ProductionId);
        var own = new Dictionary<Guid, int?>
        {
            [docs[0]] = designation.Confidential,
            [docs[1]] = designation.Aeo,
            [docs[2]] = designation.Confidential,
            [docs[3]] = designation.None,
            [docs[6]] = designation.None,
            [docs[7]] = designation.Confidential,
        };
        var expected = DesignationResolver.Resolve(
            [.. members.Select(m => new DesignationMember(m.Sequence, m.FamilyKey, own.GetValueOrDefault(m.DocumentId)))], plan.Levels, plan.Rule);
        live.Select(r => (r.Sequence, r.ChoiceId, r.Legend, r.Source)).Should().Equal(expected.Select(e => (e.Sequence, e.ChoiceId, e.Legend, e.Source)));

        var finalized = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Unimaged(), Ct);
        finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);

        var frozen = (await h.AssignmentAsync(ws, draft.ProductionId)).ToDictionary(r => r.DocumentId);
        frozen[docs[0]].Should().BeEquivalentTo(new { Designation = "HIGHLY CONFIDENTIAL – AEO", DesignationSource = DesignationSource.Family }, "A's parent takes its child's AEO");
        frozen[docs[1]].Should().BeEquivalentTo(new { Designation = "HIGHLY CONFIDENTIAL – AEO", DesignationSource = DesignationSource.Document });
        frozen[docs[2]].Should().BeEquivalentTo(new { Designation = "CONFIDENTIAL", DesignationSource = DesignationSource.Document });
        frozen[docs[3]].Should().BeEquivalentTo(new { Designation = string.Empty, DesignationSource = DesignationSource.None, DesignationChoiceId = (int?)designation.None });
        frozen[docs[4]].Should().BeEquivalentTo(new { Designation = string.Empty, DesignationSource = DesignationSource.None, DesignationChoiceId = (int?)designation.None },
            "None is the highest designation in C, and stamps nothing");
        frozen[docs[5]].Designation.Should().Be("CONFIDENTIAL");
        frozen[docs[5]].DesignationSource.Should().Be(DesignationSource.Family);
        frozen[docs[6]].Should().BeEquivalentTo(new { Designation = "CONFIDENTIAL", DesignationSource = DesignationSource.Family }, "None is lower than D's CONFIDENTIAL");
        frozen[docs[7]].DesignationSource.Should().Be(DesignationSource.Document);

        // The frozen values are what the volume writer stamps on every page and writes to the load file (QC passes).
        var spec = ProductionSpecificationRules.Deserialize(allocated.SpecificationJson);
        foreach (var member in frozen.Values)
        {
            var pages = EndorsementPlanner.ForMember(spec, allocated.Name, member);
            pages.Should().HaveCount(member.Units);
            DesignationQc.CheckMember(spec, member, [.. pages.Select(p => p.Stamps)], member.Designation).Should().BeEmpty();
        }

        // Frozen for the life of the matter, and the listing shows the frozen values even after the coding changes.
        await CodeAsync(h, ws, user, designation.FieldId, (docs[2], designation.Aeo));
        var (after, _, _) = await service.ListDesignationsAsync(principal, (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!, 0, 100, Ct);
        after.Single(r => r.DocumentId == docs[2]).Should().BeEquivalentTo(new { Legend = "CONFIDENTIAL", OwnChoiceId = (int?)designation.Aeo });
        var tamper = () => h.Db.InWorkspaceAsync(ws, async tx =>
        {
            await using var command = tx.Command("UPDATE opportunity.production_document SET designation = 'CONFIDENTIAL' WHERE workspace_id = @ws AND production_id = @id");
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", draft.ProductionId);
            await command.ExecuteNonQueryAsync(Ct);
        });
        (await tamper.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);

        (await h.Db.ColumnAsync(
            $"SELECT details::text FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Production' AND action = 'DesignationsFrozen'"))
            .Should().ContainSingle().Which.Should().Contain("\"DesignationRule\": \"highestInFamily\"").And.Contain("\"ByFamily\": \"3\"")
            .And.Contain("\"ByDocument\": \"3\"").And.Contain("\"Designated\": \"6\"");
    }

    [Fact]
    public async Task The_family_rule_is_configurable_and_overrides_need_a_reason_are_audited_and_freeze_with_the_production()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var (ws, user, designation) = await WorkspaceAsync(h);
        var docs = await h.FamiliesAsync(ws, "RUL", [(1, "pdf"), (1, "pdf")], [(1, "pdf")]);
        await CodeAsync(h, ws, user, designation.FieldId, (docs[0], designation.Confidential), (docs[1], designation.Aeo));
        var snapshot = await h.SnapshotAsync(ws, user, docs);
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);

        // Per document: the parent keeps CONFIDENTIAL. The rule is audited with the specification.
        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("RUL") with
        {
            Designations = new ProductionDesignationSettings(FamilyRule: DesignationFamilyRuleResource.Document),
        });
        draft = await h.AllocateAsync(ws, user, draft.ProductionId);
        (await service.ListDesignationsAsync(principal, draft, 0, 10, Ct)).Rows.Select(r => r.Legend)
            .Should().Equal("CONFIDENTIAL", "HIGHLY CONFIDENTIAL – AEO", string.Empty);
        var updated = await service.UpdateAsync(principal, ws, draft.ProductionId, draft.RowVersion,
            new UpdateProductionRequest(ProductionHarness.Spec("RUL")), Ct);
        updated.Status.Should().Be(ProductionOutcomeStatus.Ok, updated.Reason);
        (await h.Db.ColumnAsync(
            $"SELECT details->>'DesignationRule' || '<' || coalesce(details->>'PreviousDesignationRule', '') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Production' AND action IN ('Created', 'Modified') ORDER BY occurred_at"))
            .Should().Equal("document<", "highestInFamily<document");
        draft = await h.AllocateAsync(ws, user, draft.ProductionId);
        (await service.ListDesignationsAsync(principal, draft, 0, 10, Ct)).Rows.Select(r => (r.Legend, r.Source)).Should().Equal(
            ("HIGHLY CONFIDENTIAL – AEO", DesignationSource.Family), ("HIGHLY CONFIDENTIAL – AEO", DesignationSource.Document), (string.Empty, DesignationSource.None));

        // An override needs a reason and a level of the specification; it is audited and listed with its reason.
        (await service.OverrideDesignationAsync(principal, ws, draft.ProductionId, docs[0], new DesignationOverrideRequest(designation.Confidential, " "), Ct))
            .Outcome.Status.Should().Be(ProductionOutcomeStatus.Invalid);
        (await service.OverrideDesignationAsync(principal, ws, draft.ProductionId, docs[0], new DesignationOverrideRequest(987654, "Typo"), Ct))
            .Outcome.Status.Should().Be(ProductionOutcomeStatus.Invalid);
        var stranger = (await h.FamiliesAsync(ws, "OUT", [(1, "pdf")]))[0];
        (await service.OverrideDesignationAsync(principal, ws, draft.ProductionId, stranger, new DesignationOverrideRequest(null, "Not a member"), Ct))
            .Outcome.Status.Should().Be(ProductionOutcomeStatus.Invalid, "only a member of the frozen set is overridden");
        var (outcome, row) = await service.OverrideDesignationAsync(principal, ws, draft.ProductionId, docs[0],
            new DesignationOverrideRequest(designation.Confidential, "Parent letter is public; agreed with opposing counsel on 2026-10-01."), Ct);
        outcome.Status.Should().Be(ProductionOutcomeStatus.Ok, outcome.Reason);
        row.Should().BeEquivalentTo(new { DocumentId = docs[0], ChoiceId = (int?)designation.Confidential, CreatedBy = user });
        draft = (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!;
        (await service.ListDesignationsAsync(principal, draft, 0, 10, Ct)).Rows[0].Should().BeEquivalentTo(new
        {
            Legend = "CONFIDENTIAL",
            Source = DesignationSource.Override,
            OverrideReason = "Parent letter is public; agreed with opposing counsel on 2026-10-01.",
        });
        (await service.OverrideDesignationAsync(principal, ws, draft.ProductionId, docs[2], new DesignationOverrideRequest(null, "Remove later"), Ct))
            .Outcome.Status.Should().Be(ProductionOutcomeStatus.Ok);
        (await service.RemoveDesignationOverrideAsync(principal, ws, draft.ProductionId, docs[2], Ct)).Status.Should().Be(ProductionOutcomeStatus.Ok);
        (await service.RemoveDesignationOverrideAsync(principal, ws, draft.ProductionId, docs[2], Ct)).Status.Should().Be(ProductionOutcomeStatus.NotFound);
        (await h.Db.ColumnAsync(
            $"SELECT action || ':' || coalesce(details->>'Reason', '-') || ':' || (details->>'DocumentId') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Production' AND action LIKE 'DesignationOverrid%' ORDER BY occurred_at"))
            .Should().Equal(
                $"DesignationOverridden:Parent letter is public; agreed with opposing counsel on 2026-10-01.:{docs[0]}",
                $"DesignationOverridden:Remove later:{docs[2]}",
                $"DesignationOverrideRemoved:-:{docs[2]}");

        draft = (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!;
        (await service.FinalizeAsync(principal, ws, draft.ProductionId, draft.RowVersion, ProductionHarness.Unimaged(), Ct)).Status.Should().Be(ProductionOutcomeStatus.Ok);
        var frozen = await h.AssignmentAsync(ws, draft.ProductionId);
        frozen.Select(r => (r.Designation, r.DesignationSource)).Should().Equal(
            ("CONFIDENTIAL", DesignationSource.Override), ("HIGHLY CONFIDENTIAL – AEO", DesignationSource.Document), (string.Empty, DesignationSource.None));

        // Overrides are frozen with the production: refused by the service and by the database.
        (await service.OverrideDesignationAsync(principal, ws, draft.ProductionId, docs[1], new DesignationOverrideRequest(null, "Too late"), Ct))
            .Outcome.Status.Should().Be(ProductionOutcomeStatus.Conflict);
        var tamper = () => h.Db.InWorkspaceAsync(ws, async tx =>
        {
            await using var command = tx.Command(
                "UPDATE opportunity.production_designation_override SET reason = 'changed' WHERE workspace_id = @ws AND production_id = @id");
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", draft.ProductionId);
            await command.ExecuteNonQueryAsync(Ct);
        });
        (await tamper.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.IntegrityConstraintViolation);
    }

    [Fact]
    public async Task A_designated_document_is_never_produced_without_its_stamp_or_with_an_unlisted_designation()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var (ws, user, designation) = await WorkspaceAsync(h);
        var docs = await h.FamiliesAsync(ws, "QC", [(1, "pdf")], [(1, "pdf")]);
        await CodeAsync(h, ws, user, designation.FieldId, (docs[0], designation.Confidential));
        var snapshot = await h.SnapshotAsync(ws, user, docs);
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);

        // Only a Bates stamp: a designated page would carry no legend.
        var batesOnly = ProductionHarness.Spec("QCA") with
        {
            Endorsements = new ProductionEndorsementSettings([new ProductionEndorsement(EndorsementPositionResource.BottomRight, "{bates}")]),
        };
        var draft = await h.AllocateAsync(ws, user, (await h.CreateOkAsync(ws, user, snapshot.SnapshotId, batesOnly)).ProductionId);
        var refused = await service.FinalizeAsync(principal, ws, draft.ProductionId, draft.RowVersion, Ct);
        refused.Status.Should().Be(ProductionOutcomeStatus.DesignationRefused);
        refused.Reason.Should().Contain("{confidentiality}");
        (await h.AssignmentAsync(ws, draft.ProductionId)).Should().OnlyContain(r => r.DesignationSource == null, "nothing was frozen");

        // A designation added to the field after the specification was written is not listed in its levels.
        var listed = await h.AllocateAsync(ws, user, (await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("QCB"))).ProductionId);
        var added = await h.Db.ScalarAsync<int>(
            """
            WITH id AS (UPDATE opportunity.field_catalog_counter SET next_choice_id = next_choice_id + 1 WHERE workspace_id = @ws RETURNING next_choice_id - 1 AS choice_id)
            INSERT INTO opportunity.choice (workspace_id, field_id, choice_id, name, sort_order) SELECT @ws, @field, choice_id, 'RESTRICTED – OUTSIDE COUNSEL', 9 FROM id
            RETURNING choice_id
            """,
            ("ws", ws), ("field", designation.FieldId));
        await CodeAsync(h, ws, user, designation.FieldId, (docs[1], added));
        var unlisted = await service.FinalizeAsync(principal, ws, listed.ProductionId, listed.RowVersion, Ct);
        unlisted.Status.Should().Be(ProductionOutcomeStatus.DesignationRefused);
        unlisted.Reason.Should().Contain("do not list");

        // Listing it in the specification (a change of the draft) lets it finalize.
        var withLevel = ProductionHarness.Spec("QCB") with
        {
            Designations = new ProductionDesignationSettings(designation.FieldId, null,
                [new(designation.None), new(designation.Confidential), new(added), new(designation.Aeo)]),
        };
        (await service.UpdateAsync(principal, ws, listed.ProductionId, listed.RowVersion, new UpdateProductionRequest(withLevel), Ct)).Status
            .Should().Be(ProductionOutcomeStatus.Ok);
        listed = await h.AllocateAsync(ws, user, listed.ProductionId);
        (await service.FinalizeAsync(principal, ws, listed.ProductionId, listed.RowVersion, ProductionHarness.Unimaged(), Ct)).Status.Should().Be(ProductionOutcomeStatus.Ok);
        (await h.AssignmentAsync(ws, listed.ProductionId)).Select(r => r.Designation).Should().Equal("CONFIDENTIAL", "RESTRICTED – OUTSIDE COUNSEL");
    }

    [Fact]
    public async Task A_designation_change_after_production_yields_a_redesignation_report_and_an_overlay_load_file()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var (ws, user, designation) = await WorkspaceAsync(h);
        var docs = await h.FamiliesAsync(ws, "RED", [(2, "pdf"), (1, "pdf")], [(1, "pdf")], [(3, "pdf")]);
        await CodeAsync(h, ws, user, designation.FieldId, (docs[0], designation.Confidential), (docs[1], designation.Aeo), (docs[2], designation.Confidential));
        var snapshot = await h.SnapshotAsync(ws, user, docs);
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);
        var draft = await h.AllocateAsync(ws, user, (await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("RED", padding: 4))).ProductionId);
        (await service.RedesignationReportAsync(principal, draft, 0, 50, Ct)).Outcome.Status.Should().Be(ProductionOutcomeStatus.Conflict, "a draft has nothing produced");
        (await service.FinalizeAsync(principal, ws, draft.ProductionId, draft.RowVersion, ProductionHarness.Unimaged(), Ct)).Status.Should().Be(ProductionOutcomeStatus.Ok);
        var production = (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!;
        (await service.RedesignationReportAsync(principal, production, 0, 50, Ct)).Report!.Rows.Should().BeEmpty("nothing changed yet");

        // After production: A's child is lowered to None (A drops to CONFIDENTIAL), B is raised to AEO, C gets CONFIDENTIAL.
        await CodeAsync(h, ws, user, designation.FieldId, (docs[1], designation.None), (docs[2], designation.Aeo), (docs[3], designation.Confidential));
        var (outcome, report) = await service.RedesignationReportAsync(principal, production, 0, 50, Ct);
        outcome.Status.Should().Be(ProductionOutcomeStatus.Ok);
        report!.Restricted.Should().Be(0);
        report.NextAfter.Should().BeNull();
        report.Rows.Select(r => (r.ProdBegBates, r.ProdEndBates, r.ProducedLegend, r.CurrentLegend)).Should().Equal(
            ("RED0001", "RED0002", "HIGHLY CONFIDENTIAL – AEO", "CONFIDENTIAL"),
            ("RED0003", "RED0003", "HIGHLY CONFIDENTIAL – AEO", "CONFIDENTIAL"),
            ("RED0004", "RED0004", "CONFIDENTIAL", "HIGHLY CONFIDENTIAL – AEO"),
            ("RED0005", "RED0007", string.Empty, "CONFIDENTIAL"));

        // Paged: two rows, then the rest after the cursor.
        var (_, first) = await service.RedesignationReportAsync(principal, production, 0, 2, Ct);
        first!.Rows.Should().HaveCount(2);
        var (_, rest) = await service.RedesignationReportAsync(principal, production, first.NextAfter!.Value, 2, Ct);
        rest!.Rows.Select(r => r.ProdBegBates).Should().Equal("RED0004", "RED0005");

        // The overlay: the production's DAT conventions, ProdBegBates key, the new legend.
        using var overlay = new MemoryStream();
        var (written, left) = await service.WriteRedesignationOverlayAsync(principal, production, overlay, Ct);
        (written, left).Should().Be((4L, 0L));
        var bytes = overlay.ToArray();
        bytes.AsSpan(0, 3).ToArray().Should().Equal(0xEF, 0xBB, 0xBF);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Equal(
            "þProdBegBatesþ\u0014þProdEndBatesþ\u0014þConfidentiality Designationþ",
            "þRED0001þ\u0014þRED0002þ\u0014þCONFIDENTIALþ",
            "þRED0003þ\u0014þRED0003þ\u0014þCONFIDENTIALþ",
            "þRED0004þ\u0014þRED0004þ\u0014þHIGHLY CONFIDENTIAL – AEOþ",
            "þRED0005þ\u0014þRED0007þ\u0014þCONFIDENTIALþ");

        // Q-52: a reviewer who may not see AEO documents gets them counted, never listed (report and overlay alike).
        var reviewer = ProductionHarness.Principal(await h.UserAsync(ws, WorkspaceRole.Reviewer));
        var (_, limited) = await service.RedesignationReportAsync(reviewer, production, 0, 50, Ct);
        limited!.Rows.Select(r => r.ProdBegBates).Should().Equal("RED0001", "RED0003", "RED0005");
        limited.Restricted.Should().Be(1);
        using var limitedOverlay = new MemoryStream();
        (await service.WriteRedesignationOverlayAsync(reviewer, production, limitedOverlay, Ct)).Should().Be((3L, 1L));
    }

    [Fact]
    public async Task AEO_documents_are_visible_to_the_designated_roles_only()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var (ws, manager, designation) = await WorkspaceAsync(h);
        var docs = await h.FamiliesAsync(ws, "AEO", [(1, "pdf")], [(1, "pdf")]);
        await CodeAsync(h, ws, manager, designation.FieldId, (docs[0], designation.Aeo), (docs[1], designation.Confidential));
        var pdp = h.Pdp();
        foreach (var (role, seesAeo) in new[] { (WorkspaceRole.Reviewer, false), (WorkspaceRole.QcReviewer, false), (WorkspaceRole.ProductionManager, true), (WorkspaceRole.PrivilegeReviewer, true) })
        {
            var user = ProductionHarness.Principal(await h.UserAsync(ws, role));
            (await pdp.AuthorizeAsync(user, ws, Permission.DocumentView, docs[0], Ct)).IsAllowed.Should().Be(seesAeo, role.ToString());
            (await pdp.AuthorizeAsync(user, ws, Permission.DocumentView, docs[1], Ct)).IsAllowed.Should().BeTrue(role.ToString());
        }
    }

    private sealed record DesignationField(int FieldId, int None, int Confidential, int Aeo);

    /// <summary>A workspace with the default template (Confidentiality Designation bound to the Confidential and AEO classes) and a Production Manager.</summary>
    private static async Task<(Guid Ws, Guid User, DesignationField Field)> WorkspaceAsync(ProductionHarness h)
    {
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var field = catalog.Fields.Single(f => f.SecurityClass == SecurityClass.ConfidentialityDesignation);
        int Choice(string name) => catalog.ChoicesOf(field.FieldId).Single(c => c.Name == name).ChoiceId;
        return (ws, user, new DesignationField(field.FieldId, Choice("None"), Choice("CONFIDENTIAL"), Choice("HIGHLY CONFIDENTIAL – AEO")));
    }

    private static async Task CodeAsync(ProductionHarness h, Guid ws, Guid user, int fieldId, params (Guid Document, int Choice)[] values)
    {
        foreach (var (document, choice) in values)
        {
            var result = await h.Db.Coding.ApplyAsync(new CodingWriteRequest
            {
                WorkspaceId = ws,
                IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
                Actor = new CodingActor(user, CodingActorType.Human),
                Documents = [new CodingTarget(document)],
                Operations = [CodingFieldOperation.Set(fieldId, JsonValue.Create(choice))],
            }, Ct);
            result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        }
    }
}
