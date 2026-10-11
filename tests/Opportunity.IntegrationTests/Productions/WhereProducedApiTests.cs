using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Application.Jobs;
using Opportunity.Application.Productions;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E13-T04 through the real API host: the where-produced lookup returns every produced (finalized or voided) production,
/// volume and Bates range of a document, its family members and its duplicates, never a draft's numbers (AC 1); the
/// cross-reference CSV (matter, one production or one lookup) is available to Production.Create holders only and every
/// download is audited (AC 2). Documents the caller may not see are omitted entirely (Q-52).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class WhereProducedApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_lookup_finds_every_produced_volume_and_bates_range_of_a_document_its_family_and_duplicates_and_the_export_is_audited()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var db = h.Db;
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var admin = await MemberAsync(db, ws, WorkspaceRole.WorkspaceAdmin, "Ada Admin");
        var walled = await MemberAsync(db, ws, WorkspaceRole.WorkspaceAdmin, "Wally Walled");
        var reviewer = await MemberAsync(db, ws, WorkspaceRole.Reviewer, "Rae Reviewer");

        // A (0) with attachment B (1); C (2); D (3) is an exact duplicate of A, walled from Wally; E (4).
        var docs = await h.FamiliesAsync(ws, "WPD", [(1, "pdf"), (1, "pdf")], [(1, "pdf")], [(1, "pdf")], [(1, "pdf")]);
        var group = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value) VALUES (@ws, @group, 1, 1, 'WPD-DUP');
            UPDATE opportunity.document SET duplicate_group_id = @group, is_duplicate_primary = (document_id = @a)
             WHERE workspace_id = @ws AND document_id IN (@a, @d);
            INSERT INTO opportunity.ethical_wall (workspace_id, wall_id, name) VALUES (@ws, @wall, 'Wall');
            INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, user_id) VALUES (@ws, @wall, @member, @user);
            INSERT INTO opportunity.ethical_wall_scope (workspace_id, wall_id, scope_id, kind, document_id) VALUES (@ws, @wall, @scope, 1, @d);
            INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) VALUES (@ws, @d, @wall);
            """,
            ("ws", ws), ("group", group), ("a", docs[0]), ("d", docs[3]), ("wall", Guid.CreateVersion7()), ("member", Guid.CreateVersion7()),
            ("user", walled), ("scope", Guid.CreateVersion7()));

        // P1 (A, B, C) finalized with a completed volume; P2 (D, E) finalized, then voided; P3 (A) allocated but never produced.
        var service = h.Service();
        var principal = ProductionHarness.Principal(admin);
        async Task<ProductionRecord> ProduceAsync(string prefix, params Guid[] members)
        {
            var snapshot = await h.SnapshotAsync(ws, admin, members);
            var draft = await h.CreateOkAsync(ws, admin, snapshot.SnapshotId, ProductionHarness.Spec(prefix));
            var allocated = await h.AllocateAsync(ws, admin, draft.ProductionId);
            var finalized = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Unimaged(), Ct);
            finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
            return finalized.Production!;
        }

        var p1 = await ProduceAsync("WPA", docs[0], docs[1], docs[2]);
        await VolumeAsync(h, ws, admin, p1, "WPA_VOL");
        var p2 = await ProduceAsync("WPB", docs[3], docs[4]);
        (await service.VoidAsync(principal, ws, p2.ProductionId, p2.RowVersion, "Superseded by a corrected production.", Ct))
            .Status.Should().Be(ProductionOutcomeStatus.Ok);
        var draftSnapshot = await h.SnapshotAsync(ws, admin, [docs[0]]);
        var p3 = await h.CreateOkAsync(ws, admin, draftSnapshot.SnapshotId, ProductionHarness.Spec("WPC"));
        await h.AllocateAsync(ws, admin, p3.ProductionId);

        await using var factory = Factory(db.AppConnectionString);
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}";

        // AC 1: A's own numbers, its attachment's and its duplicate's, in a voided production too; never the draft, never C or E.
        using (var lookup = JsonDocument.Parse(await TextAsync(await GetAsync(client, $"{root}/documents/{docs[0]}/where-produced", admin))))
        {
            var r = lookup.RootElement;
            r.GetProperty("truncated").GetBoolean().Should().BeFalse();
            r.GetProperty("documents").EnumerateArray()
                .Select(d => (d.GetProperty("documentId").GetGuid(), Relations(d), d.GetProperty("productions").GetInt32()))
                .Should().Equal((docs[0], "self", 1), (docs[1], "family", 1), (docs[3], "duplicate", 1));
            var items = r.GetProperty("items").EnumerateArray().ToList();
            items.Select(i => (i.GetProperty("productionId").GetGuid(), i.GetProperty("controlNumber").GetString(), Relations(i),
                    i.GetProperty("prodBegBates").GetString(), i.GetProperty("prodEndAttach").GetString()))
                .Should().Equal(
                    (p1.ProductionId, "WPD0001", "self", "WPA0000001", "WPA0000002"),
                    (p1.ProductionId, "WPD0002", "family", "WPA0000002", "WPA0000002"),
                    (p2.ProductionId, "WPD0004", "duplicate", "WPB0000001", "WPB0000001"));
            items[0].GetProperty("productionStatus").GetString().Should().Be("finalized");
            items[0].GetProperty("volumes").EnumerateArray().Select(v => v.GetString()).Should().Equal("WPA_VOL001");
            items[0].GetProperty("output").GetString().Should().Be("image");
            items[2].GetProperty("productionStatus").GetString().Should().Be("voided");
            items[2].GetProperty("voidedAt").ValueKind.Should().Be(JsonValueKind.String);
            items[2].GetProperty("volumes").GetArrayLength().Should().Be(0);
        }

        // From the attachment: its family (in production order), not its parent's duplicate.
        using (var lookup = JsonDocument.Parse(await TextAsync(await GetAsync(client, $"{root}/documents/{docs[1]}/where-produced", admin))))
        {
            lookup.RootElement.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("documentId").GetGuid(), Relations(i)))
                .Should().Equal((docs[0], "family"), (docs[1], "self"));
        }

        // Q-52: the walled user gets the same answer without D, leaving no trace of it.
        var walledText = await TextAsync(await GetAsync(client, $"{root}/documents/{docs[0]}/where-produced", walled));
        walledText.Should().NotContain(docs[3].ToString()).And.NotContain("WPD0004").And.NotContain(p2.ProductionId.ToString());
        using (var lookup = JsonDocument.Parse(walledText))
        {
            lookup.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
            lookup.RootElement.GetProperty("documents").GetArrayLength().Should().Be(2);
        }

        (await StatusAsync(client, $"{root}/documents/{docs[3]}/where-produced", walled)).Should().Be(HttpStatusCode.NotFound);
        (await StatusAsync(client, $"{root}/documents/{Guid.CreateVersion7()}/where-produced", admin)).Should().Be(HttpStatusCode.NotFound);

        // AC 2: authorized roles only.
        (await StatusAsync(client, $"{root}/documents/{docs[0]}/where-produced", reviewer)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, $"{root}/productions/bates-cross-reference", reviewer)).Should().Be(HttpStatusCode.Forbidden);

        // The exportable cross-reference: one lookup, one production, the matter; formula-neutral CSV with a byte-order mark.
        var header = "Production,Production Version,Production Status,Produced At (UTC),Voided At (UTC),Volumes,Control Number,Document ID,Relation,"
            + "Output,ProdBegBates,ProdEndBates,ProdBegAttach,ProdEndAttach";
        var byDocument = await CsvAsync(client, $"{root}/productions/bates-cross-reference?documentId={docs[0]}", admin);
        byDocument[0].Should().Be(header);
        byDocument.Skip(1).Select(l => Cells(l, 6, 8, 10)).Should().Equal(
            "WPD0001|Self|WPA0000001", "WPD0002|Family|WPA0000002", "WPD0004|Duplicate|WPB0000001");
        byDocument[1].Should().Contain(",WPA_VOL001,").And.Contain(",Finalized,");
        (await CsvAsync(client, $"{root}/productions/bates-cross-reference?documentId={docs[0]}", walled)).Skip(1)
            .Select(l => Cells(l, 6)).Should().Equal("WPD0001", "WPD0002");
        (await CsvAsync(client, $"{root}/productions/bates-cross-reference", admin)).Skip(1).Select(l => Cells(l, 6, 10))
            .Should().Equal("WPD0001|WPA0000001", "WPD0002|WPA0000002", "WPD0003|WPA0000003", "WPD0004|WPB0000001", "WPD0005|WPB0000002");
        (await CsvAsync(client, $"{root}/productions/bates-cross-reference", walled)).Skip(1).Select(l => Cells(l, 6))
            .Should().Equal("WPD0001", "WPD0002", "WPD0003", "WPD0005");
        (await CsvAsync(client, $"{root}/productions/bates-cross-reference?productionId={p2.ProductionId}", admin)).Skip(1)
            .Select(l => Cells(l, 2, 6)).Should().Equal("Voided|WPD0004", "Voided|WPD0005");
        (await StatusAsync(client, $"{root}/productions/bates-cross-reference?productionId={p3.ProductionId}", admin))
            .Should().Be(HttpStatusCode.NotFound, "a draft issued no numbers");

        (await db.ColumnAsync(
                $"""
                SELECT (details->>'Scope') || ':' || (details->>'Rows') FROM audit.audit_event
                 WHERE workspace_id = '{ws}' AND category = 'Production' AND action = 'CrossReferenceExported' ORDER BY occurred_at
                """))
            .Should().Equal("Document:3", "Document:2", "Matter:5", "Matter:4", "Production:2");
    }

    private static string Relations(JsonElement element) =>
        string.Join('+', element.GetProperty("relations").EnumerateArray().Select(r => r.GetString()));

    private static string Cells(string line, params int[] indexes)
    {
        var cells = line.Split(',');
        return string.Join('|', indexes.Select(i => cells[i]));
    }

    private static async Task<string> TextAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, text);
            return text;
        }
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url, Guid user)
    {
        using var response = await GetAsync(client, url, user);
        return response.StatusCode;
    }

    private static async Task<List<string>> CsvAsync(HttpClient client, string url, Guid user)
    {
        using var response = await GetAsync(client, url, user);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        bytes.AsSpan(0, 3).ToArray().Should().Equal(Encoding.UTF8.GetPreamble());
        return [.. Encoding.UTF8.GetString(bytes.AsSpan(3)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>A completed volume run of the production, as the rendering worker records it (an export row with production_id).</summary>
    private static async Task VolumeAsync(ProductionHarness h, Guid ws, Guid user, ProductionRecord production, string volumePrefix)
    {
        var job = await h.Jobs.CreateAsync(new NewJob
        {
            WorkspaceId = ws,
            JobType = JobType.Production,
            InitiatedBy = user,
            TargetSnapshotId = production.SnapshotId,
        }, Ct);
        await h.Db.ExecuteAsync(
            """
            INSERT INTO opportunity.export (workspace_id, export_id, job_id, snapshot_id, name, settings, status, created_by, created_by_display,
                                            completed_at, documents_exported, documents_excluded, natives, texts, images, pages, file_count,
                                            total_bytes, manifest_sha256, production_id)
            VALUES (@ws, @id, @job, @snapshot, 'Volume', jsonb_build_object('volumePrefix', @prefix, 'volumeStart', 1, 'volumePadding', 3), 2,
                    @user, 'Production Tester', now(), 3, 0, 0, 0, 3, 3, 4, 100, decode(repeat('ab', 32), 'hex'), @production)
            """,
            ("ws", ws), ("id", Guid.CreateVersion7()), ("job", job.Job.JobId), ("snapshot", production.SnapshotId), ("prefix", volumePrefix),
            ("user", user), ("production", production.ProductionId));
    }
}
