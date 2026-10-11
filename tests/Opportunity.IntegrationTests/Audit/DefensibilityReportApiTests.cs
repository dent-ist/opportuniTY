using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Application.Jobs;
using Opportunity.Application.Storage;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Productions;
using Opportunity.Production.Productions;

using static Opportunity.IntegrationTests.Coding.CodingApiHarness;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T05 through the real API host, PDP, audit store and a filesystem object store: every defensibility report states
/// its data-as-of point and fingerprint and gives the same fingerprint for the same point after later changes (AC 1);
/// search term hit reports note the index lag at execution (AC 2); a document's chain of custody shows the hashes
/// recorded at import next to the hashes re-read from storage, and a tampered object is reported and audited (AC 3). The
/// pack bundles the reports with the production manifests, QC reports, compliance reports and audit trail, and its
/// manifest lists every file's SHA-256.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class DefensibilityReportApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reports_state_their_point_reproduce_for_it_note_index_lag_and_compare_import_and_storage_hashes()
    {
        await using var content = await ContentDatabase.CreateAsync(postgres);
        var core = content.Security.Core;
        var h = ProductionHarness.Over(core);
        var ws = await core.CreateWorkspaceAsync();
        await core.Fields.InitializeWorkspaceAsync(ws, Ct);
        var admin = await MemberAsync(core, ws, WorkspaceRole.WorkspaceAdmin, "Ada Admin");
        var rae = await MemberAsync(core, ws, WorkspaceRole.Reviewer, "Rae Reviewer");
        var sam = await MemberAsync(core, ws, WorkspaceRole.Reviewer, "Sam Second");
        var stored = await content.DocumentAsync(ws);
        var docs = await h.FamiliesAsync(ws, "DEF", [(1, "pdf")], [(1, "pdf")]);
        await core.ExecuteAsync(
            "UPDATE opportunity.document SET created_at = now() - interval '1 day', sha256 = @sha WHERE workspace_id = @ws",
            ("ws", ws), ("sha", SHA256.HashData(stored.Native)));

        // Import provenance of the stored document: its load file in object storage and its row.
        var loadFile = Encoding.UTF8.GetBytes("þControl Numberþ\r\nþ" + stored.ControlNumber + "þ\r\n");
        var importId = Guid.CreateVersion7();
        var loadKey = ObjectKeys.ImportSource(ws, importId, Sha256Digest.FromBytes(SHA256.HashData(loadFile)));
        using (var body = new MemoryStream(loadFile))
        {
            await content.Store.PutAsync(loadKey, body, cancellationToken: Ct);
        }

        var importJob = await h.Jobs.CreateAsync(new NewJob { WorkspaceId = ws, JobType = JobType.Import, InitiatedBy = admin }, Ct);
        await core.ExecuteAsync(
            """
            INSERT INTO opportunity.import_batch (workspace_id, import_batch_id, job_id, name, mode, source_file_name, source_object_key, source_sha256,
                                                  source_size, profile, created_by, created_at, completed_at)
            VALUES (@ws, @id, @job, 'Vendor volume 1', 1, 'VOL001.dat', @key, @sha, @size, '{}', @user, now() - interval '20 hours',
                    now() - interval '19 hours');
            INSERT INTO opportunity.import_batch_member (workspace_id, import_batch_id, row_no, document_id, action) VALUES (@ws, @id, 1, @doc, 1);
            """,
            ("ws", ws), ("id", importId), ("job", importJob.Job.JobId), ("key", loadKey.Value), ("sha", SHA256.HashData(loadFile)),
            ("size", (long)loadFile.Length), ("user", admin), ("doc", stored.DocumentId));

        // Coding history: Rae two hours ago, Sam half an hour ago.
        var field = await core.ScalarAsync<int>("SELECT min(field_id) FROM opportunity.field_definition WHERE workspace_id = @ws", ("ws", ws));
        var start = DateTimeOffset.UtcNow;
        await CodeAsync(core, ws, rae, docs[0], field, start.AddHours(-2));
        await CodeAsync(core, ws, rae, stored.DocumentId, field, start.AddHours(-2).AddMinutes(1));
        await CodeAsync(core, ws, sam, docs[1], field, start.AddMinutes(-30));
        var hourAgo = start.AddHours(-1);

        // A completed search term report that ran while three changes were not yet searchable.
        var reportJob = await h.Jobs.CreateAsync(new NewJob { WorkspaceId = ws, JobType = JobType.SearchTermReport, InitiatedBy = admin }, Ct);
        var reportId = Guid.CreateVersion7();
        await core.ExecuteAsync(
            """
            INSERT INTO opportunity.search_term_report (workspace_id, report_id, name, scope_kind, status, job_id, search_generation, index_current,
                index_pending_changes, index_lag_ms, documents_in_scope, documents_with_hits, documents_with_hits_family, documents_without_hits,
                excluded_no_access, created_by, created_by_display, executed_by, executed_by_display, executed_at, completed_at)
            VALUES (@ws, @id, 'Meet and confer terms', 'Workspace', 'Completed', @job, 41, false, 3, 4500, 3, 2, 2, 1, 0, @user, 'Ada Admin', @user,
                    'Ada Admin', now() - interval '3 hours', now() - interval '3 hours');
            INSERT INTO opportunity.search_term_report_term (workspace_id, report_id, term_no, term_id, name, expression, documents_with_hits,
                documents_with_hits_family, unique_hits, unique_hits_family)
            VALUES (@ws, @id, 1, gen_random_uuid(), 'Merger', 'merger', 2, 2, 2, 2);
            """,
            ("ws", ws), ("id", reportId), ("job", reportJob.Job.JobId), ("user", admin));

        // A finalized production with a completed volume.
        var snapshot = await h.SnapshotAsync(ws, admin, docs);
        var draft = await h.CreateOkAsync(ws, admin, snapshot.SnapshotId, ProductionHarness.Spec("DFP"));
        var allocated = await h.AllocateAsync(ws, admin, draft.ProductionId);
        var service = h.Service();
        var finalized = await service.FinalizeAsync(ProductionHarness.Principal(admin), ws, draft.ProductionId, allocated.RowVersion,
            ProductionHarness.Unimaged(), Ct);
        finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
        var production = finalized.Production!;
        await VolumeAsync(h, ws, admin, production.ProductionId, production.SnapshotId);
        var afterFinalization = DateTimeOffset.UtcNow;

        await using var factory = Factory(core.AppConnectionString,
            b => b.UseSetting("ObjectStorage:Provider", "FileSystem").UseSetting("ObjectStorage:FileSystem:RootPath", content.StoreRoot));
        using var client = factory.CreateClient();
        var root = $"/api/v1/workspaces/{ws}/defensibility";

        // Permission and validation.
        (await StatusAsync(client, root + "/review-summary", rae)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(client, root + "/review-summary", Guid.CreateVersion7())).Should().Be(HttpStatusCode.NotFound);
        await (await GetAsync(client, $"{root}/review-summary?asOf={Iso(DateTimeOffset.UtcNow.AddHours(1))}", admin))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await GetAsync(client, $"{root}/review-summary?snapshotId={Guid.CreateVersion7()}", admin))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // AC 1: the review summary as of an hour ago states its point and counts only what was coded by then.
        var review = await JsonAsync(client, $"{root}/review-summary?asOf={Iso(hourAgo)}", admin);
        var statement = review.GetProperty("statement");
        statement.GetProperty("asOf").GetDateTimeOffset().Should().BeCloseTo(hourAgo, TimeSpan.FromMilliseconds(1));
        statement.GetProperty("searchGeneration").ValueKind.Should().Be(JsonValueKind.Null, "the generation is known only for reports as of now");
        var fingerprint = statement.GetProperty("fingerprint").GetString();
        fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        review.GetProperty("population").GetProperty("documents").GetInt64().Should().Be(3);
        review.GetProperty("population").GetProperty("reviewed").GetInt64().Should().Be(2);
        review.GetProperty("reviewers").EnumerateArray().Select(r => (r.GetProperty("userId").GetGuid(), r.GetProperty("documents").GetInt64()))
            .Should().Equal((rae, 2L));
        review.GetProperty("days").EnumerateArray().First().GetProperty("date").GetString()
            .Should().Be(start.AddHours(-2).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        // Later coding does not change the report for that point; as of now it does.
        await CodeAsync(core, ws, sam, stored.DocumentId, field, DateTimeOffset.UtcNow);
        (await JsonAsync(client, $"{root}/review-summary?asOf={Iso(hourAgo)}", admin)).GetProperty("statement").GetProperty("fingerprint").GetString()
            .Should().Be(fingerprint);
        var now = await JsonAsync(client, root + "/review-summary", admin);
        now.GetProperty("statement").GetProperty("fingerprint").GetString().Should().NotBe(fingerprint);
        now.GetProperty("statement").GetProperty("searchGeneration").ValueKind.Should().Be(JsonValueKind.Number);
        now.GetProperty("reviewers").GetArrayLength().Should().Be(2);
        now.GetProperty("population").GetProperty("reviewed").GetInt64().Should().Be(3);

        // The scope of a snapshot: the production's frozen set.
        var scoped = await JsonAsync(client, $"{root}/review-summary?snapshotId={snapshot.SnapshotId}", admin);
        scoped.GetProperty("population").GetProperty("documents").GetInt64().Should().Be(2);
        scoped.GetProperty("statement").GetProperty("snapshotId").GetGuid().Should().Be(snapshot.SnapshotId);

        // Production history: nothing an hour ago; after finalization the production with its hashes, QC result and volume.
        (await JsonAsync(client, $"{root}/production-history?asOf={Iso(hourAgo)}", admin)).GetProperty("productions").GetArrayLength().Should().Be(0);
        var history = await JsonAsync(client, $"{root}/production-history?asOf={Iso(afterFinalization)}", admin);
        var entry = history.GetProperty("productions").EnumerateArray().Single();
        entry.GetProperty("status").GetString().Should().Be("Finalized");
        entry.GetProperty("batesBegin").GetString().Should().Be("DFP0000001");
        entry.GetProperty("batesEnd").GetString().Should().Be("DFP0000002");
        entry.GetProperty("documents").GetInt64().Should().Be(2);
        entry.GetProperty("manifestSha256").GetString().Should().Be(Convert.ToHexStringLower(production.ManifestSha256!));
        entry.GetProperty("qc").GetProperty("outcome").GetString().Should().Be("Passed");
        entry.GetProperty("qc").GetProperty("checks").EnumerateArray().Should().Contain(c => c.GetProperty("status").GetString() == "overridden");
        entry.GetProperty("volumes").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("DFP_VOL001");
        var historyFingerprint = history.GetProperty("statement").GetProperty("fingerprint").GetString();

        // A void after the point does not change the history as of the point.
        (await service.VoidAsync(ProductionHarness.Principal(admin), ws, production.ProductionId, production.RowVersion, "Clawback.", Ct))
            .Status.Should().Be(ProductionOutcomeStatus.Ok);
        var again = await JsonAsync(client, $"{root}/production-history?asOf={Iso(afterFinalization)}", admin);
        again.GetProperty("statement").GetProperty("fingerprint").GetString().Should().Be(historyFingerprint);
        again.GetProperty("productions")[0].GetProperty("status").GetString().Should().Be("Finalized");
        (await JsonAsync(client, root + "/production-history", admin)).GetProperty("productions")[0].GetProperty("status").GetString().Should().Be("Voided");
        var historyCsv = await CsvAsync(client, $"{root}/production-history/export?asOf={Iso(afterFinalization)}", admin);
        historyCsv.Should().Contain($"Fingerprint (SHA-256),{historyFingerprint}").And.Contain(",DFP0000001,DFP0000002,");

        // AC 2: hit reports note the index lag at execution and their snapshot and generation.
        var hits = await JsonAsync(client, root + "/search-term-hits", admin);
        var hit = hits.GetProperty("reports").EnumerateArray().Single();
        hit.GetProperty("reportId").GetGuid().Should().Be(reportId);
        hit.GetProperty("searchGeneration").GetInt64().Should().Be(41);
        hit.GetProperty("indexCurrent").GetBoolean().Should().BeFalse();
        hit.GetProperty("indexPendingChanges").GetInt64().Should().Be(3);
        hit.GetProperty("indexLagSeconds").GetDouble().Should().Be(4.5);
        hit.GetProperty("terms")[0].GetProperty("documentsWithHits").GetInt64().Should().Be(2);
        var hitsCsv = await CsvAsync(client, root + "/search-term-hits/export", admin);
        hitsCsv.Should().Contain("Changes not yet searchable at execution,Index lag at execution (s)").And.Contain(",41,No,3,4.5,");
        (await JsonAsync(client, $"{root}/search-term-hits?asOf={Iso(start.AddHours(-4))}", admin)).GetProperty("reports").GetArrayLength().Should().Be(0);

        // AC 3: the chain of custody shows the import hashes next to the hashes re-read from storage.
        var custodyUrl = $"/api/v1/workspaces/{ws}/documents/{stored.DocumentId}/chain-of-custody";
        var asOf = "?asOf=" + Iso(afterFinalization);
        var custody = await JsonAsync(client, custodyUrl + asOf, admin);
        var nativeSha = Convert.ToHexStringLower(SHA256.HashData(stored.Native));
        custody.GetProperty("importHashes").GetProperty("sha256").GetString().Should().Be(nativeSha);
        custody.GetProperty("mismatches").GetInt32().Should().Be(0);
        var native = custody.GetProperty("objects").EnumerateArray().First();
        native.GetProperty("objectId").GetGuid().Should().Be(stored.NativeObjectId);
        native.GetProperty("area").GetString().Should().Be("Native");
        native.GetProperty("recordedSha256").GetString().Should().Be(nativeSha);
        native.GetProperty("verification").GetProperty("sha256").GetString().Should().Be(nativeSha);
        native.GetProperty("verification").GetProperty("status").GetString().Should().Be("Match");
        custody.GetProperty("objects").EnumerateArray().Should().OnlyContain(o => o.GetProperty("verification").GetProperty("status").GetString() == "Match");
        var import = custody.GetProperty("imports").EnumerateArray().Single();
        import.GetProperty("loadFileName").GetString().Should().Be("VOL001.dat");
        import.GetProperty("loadFileVerification").GetProperty("status").GetString().Should().Be("Match");
        import.GetProperty("loadFileVerification").GetProperty("sha256").GetString().Should().Be(import.GetProperty("loadFileSha256").GetString());
        custody.GetProperty("pageSets").GetArrayLength().Should().Be(1);
        var custodyFingerprint = custody.GetProperty("statement").GetProperty("fingerprint").GetString();
        var produced = await JsonAsync(client, $"/api/v1/workspaces/{ws}/documents/{docs[0]}/chain-of-custody", admin);
        produced.GetProperty("productions").EnumerateArray().Single().GetProperty("begBates").GetString().Should().Be("DFP0000001");

        // Altered bytes in storage: reported as a mismatch next to the unchanged import hash, and audited.
        var nativeKey = await core.ScalarAsync<string>("SELECT logical_key FROM opportunity.stored_object WHERE object_id = @id", ("id", stored.NativeObjectId));
        await File.WriteAllBytesAsync(Path.Combine(content.StoreRoot, "objects", nativeKey.Replace('/', Path.DirectorySeparatorChar)),
            Encoding.UTF8.GetBytes("tampered"), Ct);
        var tampered = await JsonAsync(client, custodyUrl + asOf, admin);
        tampered.GetProperty("mismatches").GetInt32().Should().Be(1);
        var bad = tampered.GetProperty("objects")[0];
        bad.GetProperty("recordedSha256").GetString().Should().Be(nativeSha);
        bad.GetProperty("verification").GetProperty("status").GetString().Should().Be("Mismatch");
        bad.GetProperty("verification").GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData("tampered"u8)));
        tampered.GetProperty("statement").GetProperty("fingerprint").GetString().Should().Be(custodyFingerprint, "re-verification results are not as-of data");
        var custodyCsv = await CsvAsync(client, custodyUrl + "/export" + asOf, admin);
        custodyCsv.Should().Contain("SHA-256 recorded at import,SHA-256 re-verified from storage,Verification").And.Contain($",{nativeSha},")
            .And.Contain(",Mismatch,");
        (await core.ColumnAsync(
            $"SELECT details->>'objectId' FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Integrity' AND action = 'HashMismatch'"))
            .Should().Equal(Enumerable.Repeat(stored.NativeObjectId.ToString("D"), 2));

        // Documents the caller may not view answer like missing ones.
        (await StatusAsync(client, $"/api/v1/workspaces/{ws}/documents/{Guid.CreateVersion7()}/chain-of-custody", admin)).Should().Be(HttpStatusCode.NotFound);
        (await StatusAsync(client, custodyUrl, rae)).Should().Be(HttpStatusCode.Forbidden);

        // The pack: reports, production manifest and QC report, compliance reports, audit trail; manifest.json hashes every file.
        using (var response = await GetAsync(client, $"{root}/pack?asOf={Iso(afterFinalization)}&documentId={stored.DocumentId}", admin))
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
            response.Content.Headers.ContentDisposition!.FileName.Should().StartWith("defensibility-pack-asof-");
            using var zip = new ZipArchive(new MemoryStream(bytes));
            var names = zip.Entries.Select(e => e.FullName).ToList();
            names.Should().Contain(["review-summary.csv", "production-history.csv", "search-term-hits.csv", "compliance/ethical-wall-proof.csv",
                "compliance/access-review.csv", "audit/audit-trail.jsonl", "manifest.json"]);
            names.Should().Contain(n => n.StartsWith("chain-of-custody/", StringComparison.Ordinal));
            names.Should().Contain(n => n.StartsWith("productions/", StringComparison.Ordinal) && n.EndsWith("/manifest.json", StringComparison.Ordinal));
            names.Should().Contain(n => n.EndsWith("/qc-report.json", StringComparison.Ordinal));
            using var manifest = JsonDocument.Parse(Read(zip, "manifest.json"));
            var m = manifest.RootElement;
            m.GetProperty("kind").GetString().Should().Be("opportunity.defensibilityPack");
            m.GetProperty("reports").EnumerateArray().First(r => r.GetProperty("report").GetString() == "ProductionHistory")
                .GetProperty("fingerprint").GetString().Should().Be(historyFingerprint, "the pack's reports are the same reports for the same point");
            var files = m.GetProperty("files").EnumerateArray().ToList();
            files.Should().HaveCount(names.Count - 1);
            foreach (var file in files)
            {
                Convert.ToHexStringLower(SHA256.HashData(Read(zip, file.GetProperty("path").GetString()!)))
                    .Should().Be(file.GetProperty("sha256").GetString(), file.GetProperty("path").GetString());
            }

            Encoding.UTF8.GetString(Read(zip, names.First(n => n.EndsWith("/manifest.json", StringComparison.Ordinal) && n != "manifest.json")))
                .Should().Be(await core.ScalarAsync<string>("SELECT manifest FROM opportunity.production WHERE production_id = @id", ("id", production.ProductionId)));
        }

        (await StatusAsync(client, $"{root}/pack?documentId={Guid.CreateVersion7()}", admin)).Should().Be(HttpStatusCode.NotFound);

        (await core.ColumnAsync(
            $"""
            SELECT details->>'report' FROM audit.audit_event
             WHERE workspace_id = '{ws}' AND category = 'Audit' AND action = 'DefensibilityReportGenerated' ORDER BY occurred_at, event_id
            """)).Should().Equal(
                "ReviewSummary", "ReviewSummary", "ReviewSummary", "ReviewSummary", "ProductionHistory", "ProductionHistory", "ProductionHistory",
                "ProductionHistory", "ProductionHistory", "SearchTermHits", "SearchTermHits", "SearchTermHits", "ChainOfCustody", "ChainOfCustody",
                "ChainOfCustody", "ChainOfCustody", "Pack");
    }

    private static async Task CodeAsync(Documents.CoreSchemaDatabase core, Guid ws, Guid actor, Guid document, int field, DateTimeOffset at)
    {
        var write = Guid.CreateVersion7();
        var key = "defensibility-" + write.ToString("N");
        await core.ExecuteAsync(
            """
            INSERT INTO opportunity.coding_write (workspace_id, write_id, idempotency_key, request_hash, actor_id, actor_type, created_at)
            VALUES (@ws, @write, @key, decode(repeat('00', 32), 'hex'), @actor, 1, @at);
            INSERT INTO opportunity.coding_event (workspace_id, occurred_at, event_id, write_id, document_id, field_id, event_kind, prior_value, new_value,
                                                  document_version, actor_id, actor_type, idempotency_key)
            VALUES (@ws, @at, gen_random_uuid(), @write, @doc, @field, 1, NULL, '"coded"'::jsonb, 2, @actor, 1, @key);
            """,
            ("ws", ws), ("write", write), ("key", key), ("actor", actor), ("at", at), ("doc", document), ("field", field));
    }

    private static async Task VolumeAsync(ProductionHarness h, Guid ws, Guid user, Guid productionId, Guid snapshotId)
    {
        var job = await h.Jobs.CreateAsync(new NewJob { WorkspaceId = ws, JobType = JobType.Production, InitiatedBy = user, TargetSnapshotId = snapshotId }, Ct);
        await h.Db.ExecuteAsync(
            """
            INSERT INTO opportunity.export (workspace_id, export_id, job_id, snapshot_id, name, settings, status, created_by, created_by_display,
                                            completed_at, documents_exported, documents_excluded, natives, texts, images, pages, file_count,
                                            total_bytes, manifest_sha256, production_id)
            VALUES (@ws, @id, @job, @snapshot, 'Volume', jsonb_build_object('volumePrefix', 'DFP_VOL', 'volumeStart', 1, 'volumePadding', 3), 2,
                    @user, 'Production Tester', now(), 2, 0, 0, 0, 2, 2, 4, 100, decode(repeat('ab', 32), 'hex'), @production)
            """,
            ("ws", ws), ("id", Guid.CreateVersion7()), ("job", job.Job.JobId), ("snapshot", snapshotId), ("user", user), ("production", productionId));
    }

    private static byte[] Read(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string url, Guid user)
    {
        using var response = await GetAsync(client, url, user);
        return response.StatusCode;
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string url, Guid user)
    {
        using var response = await GetAsync(client, url, user);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task<string> CsvAsync(HttpClient client, string url, Guid user)
    {
        using var response = await GetAsync(client, url, user);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        bytes.AsSpan(0, 3).ToArray().Should().Equal(Encoding.UTF8.GetPreamble());
        return Encoding.UTF8.GetString(bytes.AsSpan(3));
    }
}
