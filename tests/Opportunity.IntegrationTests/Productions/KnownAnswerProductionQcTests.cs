using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Productions;
using Opportunity.Application.Redactions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.Core.Security;
using Opportunity.Data.Redactions;
using Opportunity.DataGenerator.Corpus.KnownAnswer;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E17-T09 for E12-T07: the generated known-answer production (withheld family members, Redact with and without
/// redactions, spreadsheets produced natively with and without their native, render failures, missing text, a family
/// member left out, confidentiality designations) is seeded as coding, redactions, page images and stored objects;
/// the real QC gate's per-check statuses, document counts, exceptions with their details, blocking/warning counts and
/// outcome equal the generator's expected results.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class KnownAnswerProductionQcTests(MigrationPostgresFixture postgres)
{
    private const ulong Seed = 147;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_qc_gate_on_the_generated_production_reports_the_expected_results()
    {
        var fixture = QcFixtureGenerator.Generate(Seed);
        var expected = QcFixtureGenerator.Expected(fixture);
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);

        // Families in load order; the harness numbers them <prefix>0001… exactly as the fixture does.
        var families = fixture.Documents.GroupBy(d => d.FamilyControlNumber, StringComparer.Ordinal)
            .Select(g => g.Select(d => (d.Pages, d.Extension)).ToArray()).ToArray();
        var ids = await h.FamiliesAsync(ws, fixture.Options.ControlNumberPrefix, families);
        var byNumber = fixture.Documents.Select((d, i) => (d.ControlNumber, Id: ids[i])).ToDictionary(x => x.ControlNumber, x => x.Id, StringComparer.Ordinal);
        (await h.Db.ColumnAsync($"SELECT control_number FROM opportunity.document WHERE workspace_id = '{ws}' ORDER BY control_number"))
            .Should().Equal(fixture.Documents.Select(d => d.ControlNumber));

        foreach (var document in fixture.Documents)
        {
            var id = byNumber[document.ControlNumber];
            await ProductionQcGateTests.RenderAsync(h, ws, id, document.Text, document.ImagedPages);
            if (document.Native)
            {
                await ProductionQcGateTests.NativeAsync(h, ws, id);
            }
        }

        await CodeAsync(h, ws, user, fixture.Documents, byNumber);
        var redactions = new RedactionStore(h.Db.AppDataSource);
        var set = (await redactions.ListSetsAsync(ws, Ct)).Single();
        foreach (var document in fixture.Documents.Where(d => d.Redactions > 0))
        {
            var pageSet = await h.Db.ScalarAsync<Guid>("SELECT active_page_set_id FROM opportunity.document WHERE document_id = @doc", ("doc", byNumber[document.ControlNumber]));
            var planned = Enumerable.Range(0, document.Redactions).Select(i => new PlannedRevision(Guid.CreateVersion7(), RedactionOperation.Add, pageSet, 1,
                new NormalizedRect(100_000 + (i * 50_000), 100_000, 40_000, 40_000), RedactionType.Black, "PII", null)).ToList();
            (await redactions.SaveAsync(ws, byNumber[document.ControlNumber], set.RedactionSetId, 0, null, user, planned, [], Ct))
                .Should().Be(RedactionWriteStatus.Ok);
        }

        var production = fixture.Production;
        production.WithheldDocuments.Should().Be("block");
        var snapshot = await h.SnapshotAsync(ws, user, [.. fixture.Documents.Where(d => d.Produced).Select(d => byNumber[d.ControlNumber])]);
        var spec = ProductionHarness.Spec(production.BatesPrefix) with
        {
            IncludeText = production.IncludeText,
            Endorsements = production.StampsConfidentiality
                ? new ProductionEndorsementSettings([new ProductionEndorsement(EndorsementPositionResource.BottomLeft, "{confidentiality}")])
                : null,
        };
        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, spec);
        (await h.AllocateAsync(ws, user, draft.ProductionId)).BatesState.Should().Be(BatesAllocationState.Allocated);

        var service = h.Service();
        var principal = ProductionHarness.Principal(user);
        var run = await service.RunQcAsync(principal, ws, draft.ProductionId, Ct);
        run.Status.Should().Be(ProductionOutcomeStatus.Ok, run.Reason);
        var qc = run.Qc!;

        static string Status(ProductionQcStatus status) => status switch
        {
            ProductionQcStatus.Passed => "passed",
            ProductionQcStatus.Failed => "failed",
            ProductionQcStatus.Warning => "warning",
            _ => status.ToString(),
        };
        qc.Checks.Select(c => (c.Definition.Key, (int)c.Definition.Check, c.Definition.Severity == ProductionQcSeverity.Blocking ? "blocking" : "warning",
                c.Definition.Overridable, Status(c.Status), c.Documents))
            .Should().Equal(expected.Checks.Select(c => (c.Check, c.Code, c.Severity, c.Overridable, c.Status, c.Documents)));
        var exceptions = await ProductionQcGateTests.ExceptionsAsync(service, principal, ws, qc.QcRunId);
        exceptions.Select(e => (e.Check.ToString(), e.ControlNumber, e.Detail)).Should().BeEquivalentTo(
            expected.Checks.SelectMany(c => c.Exceptions.Select(e => (Enum.Parse<ProductionQcCheck>(c.Check, ignoreCase: true).ToString(), (string?)e.ControlNumber, e.Detail))));
        exceptions.Should().OnlyContain(e => e.ProdBegBates != null && e.ProdBegBates.StartsWith(production.BatesPrefix, StringComparison.Ordinal));

        (qc.Outcome == ProductionQcOutcome.Blocked ? "blocked" : "passed").Should().Be(expected.Outcome);
        qc.Failed.Count().Should().Be(expected.BlockingFailures);
        qc.Warnings.Count().Should().Be(expected.Warnings);
        qc.Failed.Sum(c => c.Documents).Should().Be(expected.BlockingExceptions);
        qc.Warnings.Sum(c => c.Documents).Should().Be(expected.WarningExceptions);
        (await h.AssignmentAsync(ws, draft.ProductionId)).Should().HaveCount((int)expected.Members);
    }

    /// <summary>Codes Privilege Status (with a basis where the call needs one) and Confidentiality Designation, one write per value.</summary>
    private static async Task CodeAsync(ProductionHarness h, Guid ws, Guid user, IReadOnlyList<QcDocument> documents, Dictionary<string, Guid> ids)
    {
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var basis = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;
        var designation = catalog.Fields.Single(f => f.SecurityClass == SecurityClass.ConfidentialityDesignation && !f.IsDeleted);
        var choices = catalog.ChoicesOf(designation.FieldId);
        var writes = new List<(IEnumerable<QcDocument> Documents, CodingFieldOperation[] Operations)>();
        foreach (var (privilege, key) in new[]
        {
            (QcPrivilege.NotPrivileged, PrivilegeFields.Keys.NotPrivileged), (QcPrivilege.Withhold, PrivilegeFields.Keys.Withhold),
            (QcPrivilege.Redact, PrivilegeFields.Keys.Redact),
        })
        {
            var status = JsonValue.Create(PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value);
            writes.Add((documents.Where(d => d.Privilege == privilege), privilege == QcPrivilege.NotPrivileged
                ? [CodingFieldOperation.Set(PrivilegeFields.Status, status)]
                : [CodingFieldOperation.Set(PrivilegeFields.Status, status), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, basis)]));
        }

        foreach (var (value, name) in new[] { (QcDesignation.None, "None"), (QcDesignation.Confidential, "CONFIDENTIAL") })
        {
            var choice = choices.Single(c => c.Name == name).ChoiceId;
            writes.Add((documents.Where(d => d.Designation == value), [CodingFieldOperation.Set(designation.FieldId, JsonValue.Create(choice))]));
        }

        foreach (var (targets, operations) in writes)
        {
            var list = targets.Select(d => new CodingTarget(ids[d.ControlNumber])).ToList();
            if (list.Count == 0)
            {
                continue;
            }

            (await h.Db.Coding.ApplyAsync(new CodingWriteRequest
            {
                WorkspaceId = ws,
                IdempotencyKey = "known-answer-" + Guid.CreateVersion7().ToString("N"),
                Actor = new CodingActor(user, CodingActorType.Human),
                Documents = list,
                Operations = operations,
            }, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);
        }
    }
}
