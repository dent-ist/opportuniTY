using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

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
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E12-T07 acceptance against PostgreSQL: the QC gate runs before finalization; each check answers pass or fail with
/// its document-level exceptions; failures block finalization, an authorized override needs a reason, is audited and
/// printed in the QC report, which is retained with the production (named and hashed in its manifest, append-only in
/// the database) and downloadable as CSV and PDF; documents the caller may not see are counted, never listed.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ProductionQcGateTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_check_reports_its_exceptions_failures_block_and_authorized_overrides_are_audited_printed_and_retained()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        // QC0001/QC0002: a family of which only the parent is produced; QC0003 coded Redact without redactions; QC0004 a
        // redacted spreadsheet (produced natively by default); QC0005 without page images or text; QC0006 coded Withhold.
        var docs = await h.FamiliesAsync(ws, "QC", [(1, "pdf"), (1, "pdf")], [(2, "pdf")], [(1, "xlsx")], [(1, "pdf")], [(1, "pdf")]);
        foreach (var doc in new[] { docs[0], docs[1], docs[2], docs[3], docs[5] })
        {
            await RenderAsync(h, ws, doc);
        }

        await NativeAsync(h, ws, docs[3]);
        await PrivilegeAsync(h, ws, user, docs[2], PrivilegeFields.Keys.Redact);
        await PrivilegeAsync(h, ws, user, docs[5], PrivilegeFields.Keys.Withhold);
        var redactions = new RedactionStore(h.Db.AppDataSource);
        var set = (await redactions.ListSetsAsync(ws, Ct)).Single();
        var pageSet = await h.Db.ScalarAsync<Guid>("SELECT active_page_set_id FROM opportunity.document WHERE document_id = @doc", ("doc", docs[3]));
        (await redactions.SaveAsync(ws, docs[3], set.RedactionSetId, 0, null, user,
            [new PlannedRevision(Guid.CreateVersion7(), RedactionOperation.Add, pageSet, 1, new NormalizedRect(100_000, 100_000, 200_000, 100_000), RedactionType.Black, "PII", null)],
            [], Ct)).Should().Be(RedactionWriteStatus.Ok);

        var produced = docs.Where(d => d != docs[1]).ToList();
        var snapshot = await h.SnapshotAsync(ws, user, produced);
        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("QCG"));
        var allocated = await h.AllocateAsync(ws, user, draft.ProductionId);
        allocated.BatesState.Should().Be(BatesAllocationState.Allocated, allocated.BatesReason);
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);

        // The validation summary before finalizing: every check passes or fails with document-level exceptions.
        var check = await service.RunQcAsync(principal, ws, draft.ProductionId, Ct);
        check.Status.Should().Be(ProductionOutcomeStatus.Ok, check.Reason);
        var qc = check.Qc!;
        qc.Purpose.Should().Be(ProductionQcPurpose.Check);
        qc.Outcome.Should().Be(ProductionQcOutcome.Blocked);
        qc.Checks.Where(c => c.Status != ProductionQcStatus.Passed).Select(c => (c.Definition.Check, c.Status, c.Documents)).Should().BeEquivalentTo(new[]
        {
            (ProductionQcCheck.WithheldWithoutPlaceholder, ProductionQcStatus.Failed, 1L),
            (ProductionQcCheck.RedactWithoutRedactions, ProductionQcStatus.Failed, 1L),
            (ProductionQcCheck.RedactedNative, ProductionQcStatus.Failed, 1L),
            (ProductionQcCheck.RenderFailure, ProductionQcStatus.Failed, 1L),
            (ProductionQcCheck.IncompleteFamily, ProductionQcStatus.Warning, 1L),
            (ProductionQcCheck.TextMissing, ProductionQcStatus.Warning, 1L),
            (ProductionQcCheck.BlankConfidentiality, ProductionQcStatus.Warning, 5L),
        });
        var exceptions = await ExceptionsAsync(service, principal, ws, qc.QcRunId);
        exceptions.Where(e => e.Check != ProductionQcCheck.BlankConfidentiality).Select(e => (e.Check, e.ControlNumber, e.Detail)).Should().BeEquivalentTo(new[]
        {
            (ProductionQcCheck.WithheldWithoutPlaceholder, "QC0006", (string?)null),
            (ProductionQcCheck.RedactWithoutRedactions, "QC0003", null),
            (ProductionQcCheck.RedactedNative, "QC0004", BurnInCodes.NativeShipped),
            (ProductionQcCheck.RenderFailure, "QC0005", "PagesWithoutImage:1"),
            (ProductionQcCheck.IncompleteFamily, "QC0001", null),
            (ProductionQcCheck.TextMissing, "QC0005", null),
        });
        exceptions.Should().OnlyContain(e => e.ProdBegBates != null && e.ProdBegBates.StartsWith("QCG", StringComparison.Ordinal));
        (await service.GetQcAsync(ws, draft.ProductionId, Ct))!.QcRunId.Should().Be(qc.QcRunId);

        // Finalizing runs the gate again: blocked, kept and audited; the production stays a draft. The answer names no document.
        var refused = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Acknowledged, Ct);
        refused.Status.Should().Be(ProductionOutcomeStatus.PrivilegeWithheld);
        refused.Reason.Should().NotContain("QC000");
        refused.Qc!.Should().BeEquivalentTo(new { Purpose = ProductionQcPurpose.Finalization, Outcome = ProductionQcOutcome.Blocked });
        (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!.Status.Should().Be(ProductionStatus.Draft);
        (await service.GetQcAsync(ws, draft.ProductionId, Ct))!.QcRunId.Should().Be(refused.Qc.QcRunId, "the blocked finalization's run is kept");

        // A check that may not be overridden cannot be.
        (await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion,
            new ProductionFinalizeOptions([new ProductionQcOverride(ProductionQcCheck.RedactedNative, "Please.")], true), Ct))
            .Status.Should().Be(ProductionOutcomeStatus.Invalid);

        // Fix what cannot be overridden: release the withheld call, produce spreadsheets as images, allocate again.
        await PrivilegeAsync(h, ws, user, docs[5], PrivilegeFields.Keys.NotPrivileged);
        var stored = ProductionSpecificationRules.Deserialize(allocated.SpecificationJson);
        var spec = stored with { FileTypeRules = [], LoadFile = stored.LoadFile! with { Fields = null } };
        var updated = await service.UpdateAsync(principal, ws, draft.ProductionId, allocated.RowVersion, new UpdateProductionRequest(spec), Ct);
        updated.Status.Should().Be(ProductionOutcomeStatus.Ok, JsonSerializer.Serialize(updated.Errors));
        allocated = await h.AllocateAsync(ws, user, draft.ProductionId);

        var stillBlocked = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, ProductionFinalizeOptions.None, Ct);
        stillBlocked.Status.Should().Be(ProductionOutcomeStatus.QcBlocked);
        stillBlocked.Qc!.Failed.Select(c => c.Definition.Check).Should().BeEquivalentTo([ProductionQcCheck.RedactWithoutRedactions, ProductionQcCheck.RenderFailure]);

        var overrides = new[]
        {
            new ProductionQcOverride(ProductionQcCheck.RenderFailure, "QC0005 is a corrupt scan; a Technical Issue page is agreed with opposing counsel."),
            new ProductionQcOverride(ProductionQcCheck.RedactWithoutRedactions, "Second-level review released QC0003 for production in full."),
        };
        var warnings = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, new ProductionFinalizeOptions(overrides, false), Ct);
        warnings.Status.Should().Be(ProductionOutcomeStatus.QcBlocked);
        warnings.Reason.Should().Contain("Acknowledge");

        var finalized = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, new ProductionFinalizeOptions(overrides, true), Ct);
        finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
        finalized.Production!.Status.Should().Be(ProductionStatus.Finalized);
        var final = finalized.Qc!;
        final[ProductionQcCheck.RenderFailure].Should().BeEquivalentTo(new { Status = ProductionQcStatus.Overridden, OverrideReason = overrides[0].Reason });
        final[ProductionQcCheck.TextMissing].Status.Should().Be(ProductionQcStatus.Acknowledged);

        // The manifest names the run, its report hash and every override with its reason.
        using (var manifest = JsonDocument.Parse(finalized.Production.Manifest!))
        {
            var recorded = manifest.RootElement.GetProperty("qc");
            recorded.GetProperty("qcRunId").GetGuid().Should().Be(final.QcRunId);
            recorded.GetProperty("reportSha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(final.ReportJson))));
            recorded.GetProperty("overrides").EnumerateArray().Select(o => (o.GetProperty("check").GetString(), o.GetProperty("reason").GetString()))
                .Should().BeEquivalentTo([("renderFailure", overrides[0].Reason), ("redactWithoutRedactions", overrides[1].Reason)]);
            recorded.GetProperty("warningsAcknowledged").GetBoolean().Should().BeTrue();
        }

        (await h.Db.ColumnAsync(
            $"SELECT (details->>'Check') || '|' || (details->>'Reason') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Production' AND action = 'QcOverride' ORDER BY 1"))
            .Should().Equal($"redactWithoutRedactions|{overrides[1].Reason}", $"renderFailure|{overrides[0].Reason}");
        (await h.Db.ColumnAsync(
            $"SELECT outcome || '|' || (details->>'Purpose') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Production' AND action = 'QcRun' ORDER BY occurred_at"))
            .Should().Equal("Failure|Check", "Failure|Finalization", "Failure|Finalization", "Failure|Finalization", "Success|Finalization");

        // The QC report is retained with the production: the finalization's run stays its run, no further check runs, and the
        // stored runs are append-only.
        (await service.GetQcAsync(ws, draft.ProductionId, Ct))!.QcRunId.Should().Be(final.QcRunId);
        (await service.RunQcAsync(principal, ws, draft.ProductionId, Ct)).Status.Should().Be(ProductionOutcomeStatus.Conflict);
        await using (var connection = await h.Db.AppDataSource.OpenConnectionAsync(Ct))
        {
            foreach (var sql in new[] { "UPDATE opportunity.production_qc_run SET outcome = 1", "DELETE FROM opportunity.production_qc_exception" })
            {
                var write = async () =>
                {
                    await using var tx = await connection.BeginTransactionAsync(Ct);
                    await using var command = new NpgsqlCommand($"SELECT set_config('app.workspace_id', '{ws}', true); {sql}", connection, tx);
                    await command.ExecuteNonQueryAsync(Ct);
                };
                (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
            }
        }

        // CSV and PDF print the overrides with their reasons and the exceptions.
        var production = (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!;
        var csv = await CsvAsync(service, principal, production, final);
        csv.Should().Contain("Check,renderFailure,Blocking,Overridden,1,");
        csv.Should().Contain("Override: " + overrides[0].Reason).And.Contain("Override: " + overrides[1].Reason);
        csv.Should().Contain("Exception,renderFailure,Blocking,Overridden,,QCG").And.Contain(",QC0005,PagesWithoutImage:1");
        csv.Should().Contain("Production,Outcome,,,,,,,Passed");
        using (var pdf = new MemoryStream())
        {
            await service.WriteQcReportPdfAsync(principal, production, final, pdf, Ct);
            var text = Encoding.Latin1.GetString(pdf.ToArray());
            text.Should().StartWith("%PDF-1.4").And.Contain("Production QC report").And.Contain("Overridden with the reason: QC0005 is a corrupt scan;")
                .And.Contain("QC0005   PagesWithoutImage:1");
        }
    }

    [Fact]
    public async Task Documents_the_caller_may_not_see_are_counted_not_listed_and_block_as_inaccessible()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var owner = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var walled = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = await h.FamiliesAsync(ws, "VIS", [(1, "pdf")], [(1, "pdf")]);
        foreach (var doc in docs)
        {
            await RenderAsync(h, ws, doc, text: false);
        }

        var snapshot = await h.SnapshotAsync(ws, owner, docs);
        var draft = await h.CreateOkAsync(ws, owner, snapshot.SnapshotId, ProductionHarness.Spec("VIS"));
        var allocated = await h.AllocateAsync(ws, owner, draft.ProductionId);
        var wall = Guid.CreateVersion7();
        await h.Db.ExecuteAsync("INSERT INTO opportunity.ethical_wall (workspace_id, wall_id, name) VALUES (@ws, @wall, 'QC wall')", ("ws", ws), ("wall", wall));
        await h.Db.ExecuteAsync("INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, user_id) VALUES (@ws, @wall, @id, @user)",
            ("ws", ws), ("wall", wall), ("id", Guid.CreateVersion7()), ("user", walled));
        await h.Db.ExecuteAsync("INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) VALUES (@ws, @doc, @wall)",
            ("ws", ws), ("doc", docs[1]), ("wall", wall));
        var service = h.Service();

        // The walled user's run: the hidden member fails the per-document re-check; it is counted, never listed.
        var run = (await service.RunQcAsync(ProductionHarness.Principal(walled), ws, draft.ProductionId, Ct)).Qc!;
        run[ProductionQcCheck.Inaccessible].Should().BeEquivalentTo(new { Status = ProductionQcStatus.Failed, Documents = 1L });
        run[ProductionQcCheck.TextMissing].Documents.Should().Be(2);
        var (rows, restricted, _) = await service.ListQcExceptionsAsync(ProductionHarness.Principal(walled), ws, run.QcRunId, ProductionQcCheck.TextMissing, null, 50, Ct);
        rows.Select(r => r.ControlNumber).Should().Equal("VIS0001");
        restricted.Should().Be(1);
        var (hiddenRows, hidden, _) = await service.ListQcExceptionsAsync(ProductionHarness.Principal(walled), ws, run.QcRunId, ProductionQcCheck.Inaccessible, null, 50, Ct);
        hiddenRows.Should().BeEmpty();
        hidden.Should().Be(1);
        var walledCsv = await CsvAsync(service, ProductionHarness.Principal(walled), (await h.Store.GetAsync(ws, draft.ProductionId, Ct))!, run);
        walledCsv.Should().NotContain("VIS0002").And.NotContain(docs[1].ToString()).And.Contain("NotListed,inaccessible,,,1,").And.Contain("NotListed,textMissing,,,1,");

        // Finalizing as the walled user is refused; the owner sees both members and finalizes.
        (await service.FinalizeAsync(ProductionHarness.Principal(walled), ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Acknowledged, Ct))
            .Status.Should().Be(ProductionOutcomeStatus.QcBlocked);
        var finalized = await service.FinalizeAsync(ProductionHarness.Principal(owner), ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Acknowledged, Ct);
        finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
        var ownerRows = await ExceptionsAsync(service, ProductionHarness.Principal(owner), ws, finalized.Qc!.QcRunId);
        ownerRows.Where(r => r.Check == ProductionQcCheck.TextMissing).Select(r => r.ControlNumber).Should().Equal("VIS0001", "VIS0002");
    }

    [Fact]
    public async Task Withheld_documents_may_be_produced_as_placeholders_and_a_changed_call_or_page_count_or_overlap_blocks()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = await h.FamiliesAsync(ws, "PH", [(1, "pdf")], [(2, "pdf")], [(1, "pdf")]);
        foreach (var doc in docs)
        {
            await RenderAsync(h, ws, doc);
        }

        await PrivilegeAsync(h, ws, user, docs[0], PrivilegeFields.Keys.Withhold);
        var snapshot = await h.SnapshotAsync(ws, user, docs);
        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("PH") with { WithheldDocuments = WithheldDocumentsResource.Placeholder });
        var allocated = await h.AllocateAsync(ws, user, draft.ProductionId);
        var members = await h.AssignmentAsync(ws, draft.ProductionId);
        members.Select(m => (m.DocumentId, m.Output, m.Units)).Should().Equal(
            (docs[0], ProductionOutputKind.Placeholder, 1), (docs[1], ProductionOutputKind.Image, 2), (docs[2], ProductionOutputKind.Image, 1));
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);
        var qc = (await service.RunQcAsync(principal, ws, draft.ProductionId, Ct)).Qc!;
        qc.Failed.Should().BeEmpty("the withheld document is a placeholder");
        qc.Outcome.Should().Be(ProductionQcOutcome.Passed);

        // The call changed after allocation; the second member was re-rendered with another page count; another production
        // took numbers of this one's range (injected: allocation never lets that happen).
        await PrivilegeAsync(h, ws, user, docs[0], PrivilegeFields.Keys.NotPrivileged);
        await h.Db.ExecuteAsync(
            "UPDATE opportunity.page_set SET page_count = 3 WHERE workspace_id = @ws AND page_set_id = (SELECT active_page_set_id FROM opportunity.document WHERE document_id = @doc)",
            ("ws", ws), ("doc", docs[1]));
        var other = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("PH", start: 100));
        await h.Db.ExecuteAsync(
            "INSERT INTO opportunity.bates_range (workspace_id, range_id, production_id, bates_prefix_key, first_number, last_number, state) VALUES (@ws, @id, @p, 'PH', 4, 4, 2)",
            ("ws", ws), ("id", Guid.CreateVersion7()), ("p", other.ProductionId));
        var blocked = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Acknowledged, Ct);
        blocked.Status.Should().Be(ProductionOutcomeStatus.BatesConflict);
        blocked.Qc!.Failed.Select(c => c.Definition.Check).Should().BeEquivalentTo(
            [ProductionQcCheck.PlaceholderNoLongerWithheld, ProductionQcCheck.PageCountChanged, ProductionQcCheck.RenderFailure, ProductionQcCheck.BatesOverlap]);
        (await ExceptionsAsync(service, principal, ws, blocked.Qc.QcRunId)).Where(e => e.Check != ProductionQcCheck.BlankConfidentiality)
            .Select(e => (e.Check, e.ControlNumber, e.Detail)).Should().BeEquivalentTo(new[]
            {
                (ProductionQcCheck.PlaceholderNoLongerWithheld, "PH0001", (string?)null),
                (ProductionQcCheck.PageCountChanged, "PH0002", "Allocated:2;Now:3"),
                (ProductionQcCheck.RenderFailure, "PH0002", "PagesWithoutImage:1"),
                (ProductionQcCheck.BatesOverlap, "PH0003", other.Name),
            });
    }

    private static async Task<List<ProductionQcExceptionRow>> ExceptionsAsync(
        ProductionService service, Application.Authorization.SecurityPrincipal principal, Guid ws, Guid qcRunId)
    {
        var all = new List<ProductionQcExceptionRow>();
        ProductionQcExceptionCursor? after = null;
        do
        {
            var (rows, _, next) = await service.ListQcExceptionsAsync(principal, ws, qcRunId, null, after, 3, Ct);
            all.AddRange(rows);
            after = next;
        }
        while (after is not null);

        return all;
    }

    private static async Task<string> CsvAsync(
        ProductionService service, Application.Authorization.SecurityPrincipal principal, ProductionRecord production, ProductionQcResult qc)
    {
        using var buffer = new MemoryStream();
        await service.WriteQcReportCsvAsync(principal, production, qc, buffer, Ct);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Gives a document's active page set a stored image per page (as rendering would) and, optionally, extracted text.</summary>
    private static async Task RenderAsync(ProductionHarness h, Guid ws, Guid documentId, bool text = true)
    {
        var (pageSet, pages) = (await h.Db.ScalarAsync<Guid>("SELECT active_page_set_id FROM opportunity.document WHERE document_id = @doc", ("doc", documentId)),
            await h.Db.ScalarAsync<int>(
                "SELECT ps.page_count FROM opportunity.page_set ps JOIN opportunity.document d ON d.active_page_set_id = ps.page_set_id WHERE d.document_id = @doc",
                ("doc", documentId)));
        for (var ordinal = 1; ordinal <= pages; ordinal++)
        {
            var image = await ObjectAsync(h, ws, documentId, 3);
            await h.Db.ExecuteAsync(
                """
                INSERT INTO opportunity.page (workspace_id, page_set_id, ordinal, document_id, width_pt, height_pt, color_mode) VALUES (@ws, @ps, @n, @doc, 612, 792, 1);
                INSERT INTO opportunity.page_image (workspace_id, page_set_id, ordinal, purpose, object_id, width_px, height_px, dpi_x, dpi_y, format)
                VALUES (@ws, @ps, @n, 1, @img, 1275, 1650, 150, 150, 1);
                """,
                ("ws", ws), ("ps", pageSet), ("n", ordinal), ("doc", documentId), ("img", image));
        }

        if (text)
        {
            var textObject = await ObjectAsync(h, ws, documentId, 2);
            await h.Db.ExecuteAsync("UPDATE opportunity.document SET text_object_id = @t WHERE workspace_id = @ws AND document_id = @doc",
                ("ws", ws), ("t", textObject), ("doc", documentId));
        }
    }

    private static async Task NativeAsync(ProductionHarness h, Guid ws, Guid documentId)
    {
        var native = await ObjectAsync(h, ws, documentId, 1);
        await h.Db.ExecuteAsync("UPDATE opportunity.document SET native_object_id = @n WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", ws), ("n", native), ("doc", documentId));
    }

    private static async Task<Guid> ObjectAsync(ProductionHarness h, Guid ws, Guid documentId, short area)
    {
        var id = Guid.CreateVersion7();
        await h.Db.ExecuteAsync(
            """
            INSERT INTO opportunity.stored_object (workspace_id, object_id, logical_key, area, document_id, sha256, size_bytes, key_id, encryption_scheme, state)
            VALUES (@ws, @id, @key, @area, @doc, sha256(@id::text::bytea), 1, 'installation', 1, 1)
            """,
            ("ws", ws), ("id", id), ("key", $"ws/{ws:N}/docs/{documentId:N}/qc/{id:N}"), ("area", area), ("doc", documentId));
        return id;
    }

    private static async Task PrivilegeAsync(ProductionHarness h, Guid ws, Guid user, Guid documentId, string key)
    {
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var status = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value;
        var basis = PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.AttorneyClient)!.Value;
        (await h.Db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "qc-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(user, CodingActorType.Human),
            Documents = [new CodingTarget(documentId)],
            Operations = key == PrivilegeFields.Keys.NotPrivileged
                ? [CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(status))]
                : [CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(status)), CodingFieldOperation.AddChoices(PrivilegeFields.Basis, basis)],
        }, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);
    }
}
