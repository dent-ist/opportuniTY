using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Opportunity.Application.Authorization;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.TermReports;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Exports;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Productions;
using Opportunity.IntegrationTests.Search;
using Opportunity.Search;
using Opportunity.Search.Projection;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Security;

/// <summary>
/// Every identifier a workspace exposes over the API, as the attack suite substitutes them (E05-T05).
/// </summary>
internal sealed record WorkspaceResources(
    string Name,
    Guid WorkspaceId,
    Guid Owner,
    CodingWorkspace Fields,
    Guid DocumentId,
    string ControlNumber,
    Guid SearchId,
    string SearchCursor,
    Guid BulkSnapshotId,
    Guid ExportSnapshotId,
    Guid ExportId,
    Guid ExportFileId,
    Guid ImportId,
    Guid ImportJobId,
    Guid ImportProfileId,
    Guid SpareProfileId,
    Guid BulkCodingJobId,
    Guid LayoutId,
    Guid PreflightId,
    Guid SavedSearchFolderId,
    Guid SpareFolderId,
    Guid SavedSearchId,
    Guid SpareSavedSearchId,
    Guid TermReportId,
    Guid TermId,
    Guid SpareTermReportId,
    Guid GridViewId,
    Guid SpareGridViewId,
    Guid HighlightSetId,
    Guid SpareHighlightSetId,
    Guid ProductionSnapshotId,
    Guid FinalizedProductionId,
    Guid DraftProductionId,
    Guid SpareProductionId,
    string BatesLabel,
    Guid PropagationPreviewId,
    Guid WallId,
    Guid SpareWallId,
    Guid BreakGlassActivationId,
    Guid RedactionSetId,
    Guid PreservationLockId,
    Guid ReviewBatchSnapshotId,
    Guid FirstPassBatchSetId,
    Guid QcBatchSetId,
    Guid ReviewBatchId,
    Guid ProductionVolumeId,
    Guid ProductionVolumeFileId,
    Guid PrivilegeLogTemplateId,
    Guid PrivilegeLogId)
{
    /// <summary>Fresh identifiers that exist nowhere: the reference every foreign identifier must be indistinguishable from.</summary>
    public static WorkspaceResources Unknown(CodingWorkspace fields) => new(
        "unknown", Guid.CreateVersion7(), Guid.CreateVersion7(), fields, Guid.CreateVersion7(), "ZZ-0000001", Guid.CreateVersion7(),
        Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        "ZZ0000001", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

    /// <summary>Every identifier of the set, in the spellings a response could carry them (D and N formats).</summary>
    public IEnumerable<string> IdentifierSpellings()
    {
        Guid[] ids =
        [
            WorkspaceId, DocumentId, SearchId, BulkSnapshotId, ExportSnapshotId, ExportId, ExportFileId, ImportId, ImportJobId, ImportProfileId,
            SpareProfileId, BulkCodingJobId, LayoutId, PreflightId, SavedSearchFolderId, SpareFolderId, SavedSearchId, SpareSavedSearchId,
            TermReportId, TermId, SpareTermReportId, GridViewId, SpareGridViewId, HighlightSetId, SpareHighlightSetId,
            ProductionSnapshotId, FinalizedProductionId, DraftProductionId, SpareProductionId, PropagationPreviewId,
            WallId, SpareWallId, BreakGlassActivationId, RedactionSetId, PreservationLockId, ReviewBatchSnapshotId, FirstPassBatchSetId,
            QcBatchSetId, ReviewBatchId, ProductionVolumeId, ProductionVolumeFileId, PrivilegeLogTemplateId, PrivilegeLogId,
        ];
        return ids.SelectMany(id => new[] { id.ToString("D"), id.ToString("N") }).Append(SearchCursor);
    }
}

/// <summary>
/// The attack world (E05-T05): three workspaces on one PostgreSQL, one object store and one OpenSearch prefix: A and B
/// share an index (routing), C has a dedicated index. Each holds a reviewable document (native, text, page image,
/// thumbnail), an imported and indexed load file, a search handle with a cursor, frozen sets for bulk coding and export, a
/// bulk coding job, a completed export with files and import profiles. The attacker is Workspace Admin of A and B (and
/// created B's resources, so a missing workspace scope would let them through); C belongs to a victim the attacker has
/// no role with. Everything runs through the real API host, the PostgreSQL PDP and the real audit store.
/// </summary>
internal sealed class AttackWorld : IAsyncDisposable
{
    private readonly ChunkIndexHarness _index;
    private readonly OpenSearchIndexScope _scope;
    private readonly HttpClient _openSearch;

    private AttackWorld(
        AuthorizationDatabase db, ImportHarness import, ExportHarness exports, ContentDatabase content, ChunkIndexHarness index,
        OpenSearchIndexScope scope, HttpClient openSearch, WebApplicationFactory<Program> factory)
    {
        Db = db;
        Import = import;
        Exports = exports;
        Content = content;
        Productions = ProductionHarness.Over(db.Core);
        _index = index;
        _scope = scope;
        _openSearch = openSearch;
        Factory = factory;
        Client = factory.CreateClient();
    }

    public AuthorizationDatabase Db { get; }

    public ImportHarness Import { get; }

    public ExportHarness Exports { get; }

    public ContentDatabase Content { get; }

    public ProductionHarness Productions { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public Guid Attacker { get; private set; }

    public Guid Victim { get; private set; }

    public WorkspaceResources A { get; private set; } = null!;

    public WorkspaceResources B { get; private set; } = null!;

    public WorkspaceResources C { get; private set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<AttackWorld> CreateAsync(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
    {
        var db = await AuthorizationDatabase.CreateAsync(postgres);
        var import = ImportHarness.Over(db.Core, rowsPerChunk: 500);
        var exports = ExportHarness.Over(import, documentsPerChunk: 5);
        var content = ContentDatabase.Over(db, import.StoreRoot, import.Store);
        var scope = openSearch.CreateIndexScope();
        var options = new OpenSearchOptions { Endpoint = openSearch.BaseAddress, IndexPrefix = scope.Prefix, Placement = { CacheTtl = TimeSpan.Zero } };
        var index = await ChunkIndexHarness.OverAsync(openSearch, import, options, new ProjectionOptions());
        var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:App", db.Core.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OpenSearch", openSearch.BaseAddress.ToString());
            builder.UseSetting("OpenSearch:IndexPrefix", scope.Prefix);
            builder.UseSetting("OpenSearch:Placement:CacheTtl", "00:00:00");
            builder.UseSetting("OpenSearch:Search:FieldCatalogCacheTtl", "00:00:00");
            builder.UseSetting("ObjectStorage:Provider", "FileSystem");
            builder.UseSetting("ObjectStorage:FileSystem:RootPath", import.StoreRoot);
            builder.UseSetting("Snapshots:BackgroundEnabled", "false");
            builder.UseSetting("SearchTermReports:BackgroundEnabled", "false");
            builder.UseSetting("Jobs:Events:MaxStreamDuration", "00:00:01");
            // Audit envelopes (E14-T02): a session hash key (obviously fake) and a client address, as behind a real listener.
            builder.UseSetting("Authentication:Session:AuditHashKey", Convert.ToBase64String(new byte[32]));
            // Installation administration and step-up for the audit coverage scenario (workspace creation, holds, deletion).
            builder.UseSetting("Authentication:Mfa:AmrValues:0", "mfa");
            builder.UseSetting("Authorization:InstallationAdminGroups:0", InstallationAdminGroup);
            builder.UseSetting("Authorization:RetentionApproverGroups:0", RetentionApproverGroup);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>();
                services.RemoveAll<ISecurityStateReader>();
                services.AddSingleton<ISecurityStateReader, Data.Security.PostgresSecurityStateReader>();
            });
        });
        var world = new AttackWorld(db, import, exports, content, index, scope, new HttpClient { BaseAddress = openSearch.BaseAddress }, factory);
        try
        {
            world.Attacker = await db.CreateUserAsync();
            world.Victim = await db.CreateUserAsync();
            world.A = await world.WorkspaceAsync("A", world.Attacker, dedicated: false);
            world.B = await world.WorkspaceAsync("B", world.Attacker, dedicated: false);
            world.C = await world.WorkspaceAsync("C", world.Victim, dedicated: true);
            return world;
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>All three workspaces' identifier sets.</summary>
    public IReadOnlyList<WorkspaceResources> All => [A, B, C];

    private async Task<WorkspaceResources> WorkspaceAsync(string name, Guid owner, bool dedicated)
    {
        var ws = await Db.Core.CreateWorkspaceAsync();
        var fields = await CodingApiHarness.WorkspaceAsync(Db.Core, ws);
        await Db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, owner);
        await Factory.Services.GetRequiredService<IWorkspaceSearchPlacement>().PlaceAsync(ws, new WorkspacePlacementRequest(dedicated), Ct);

        // A reviewable document with every rendition, stored as import and rendering register them.
        var document = await Content.DocumentAsync(ws);

        // An imported, indexed load file (control numbers unique to the workspace) for the search handle and the import routes.
        var dat = ImportHarness.Dat(
            ["ControlNumber", "FileName"],
            [$"{name}-0000001", $"{name} memo one.txt"],
            [$"{name}-0000002", $"{name} memo two.txt"],
            [$"{name}-0000003", $"{name} memo three.txt"]);
        var batch = await Import.StartAsync(ws, ImportHarness.Utf8Bom(dat), name: $"{name}.dat");
        (await Import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        await _index.DeliverAllAsync(ws, batch.JobId);
        (await _index.CountAsync(ws)).Should().Be(3, "the import is indexed (and the index refreshed)");

        var search = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/searches", owner, HttpStatusCode.OK,
            new JsonObject { ["query"] = "", ["pageSize"] = 1 });
        search.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.String, "the first page has a next page: {0}", search.GetRawText());
        var bulkSnapshot = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", owner, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "BulkCoding", ["documentIds"] = new JsonArray(document.DocumentId.ToString()) });
        var exportSnapshot = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", owner, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "Export", ["documentIds"] = new JsonArray(document.DocumentId.ToString()) });
        var bulkJob = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/bulk-coding", owner, HttpStatusCode.Accepted, new JsonObject
        {
            ["snapshotId"] = bulkSnapshot.GetProperty("snapshotId").GetString(),
            ["changes"] = new JsonArray(new JsonObject { ["fieldId"] = fields.Responsive, ["operation"] = "set", ["value"] = true }),
        });
        var export = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/exports", owner, HttpStatusCode.Accepted, new JsonObject
        {
            ["snapshotId"] = exportSnapshot.GetProperty("snapshotId").GetString(),
            ["fields"] = new JsonArray(new JsonObject { ["fieldId"] = Core.Fields.SystemFields.ControlNumber }),
        });
        var exportId = export.GetProperty("exportId").GetGuid();
        var exportJob = export.GetProperty("job").GetProperty("jobId").GetGuid();
        await Exports.CoordinateAsync(ws);
        await Exports.DeliverOpenChunksAsync(ws, exportJob);
        await Exports.CoordinateAsync(ws);
        var files = await JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/exports/{exportId}/files?limit=500", owner, HttpStatusCode.OK);
        var profile = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/import-profiles", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Profile {name}", ["definition"] = new JsonObject() });
        var spare = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/import-profiles", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Spare profile {name}", ["definition"] = new JsonObject() });
        Guid preflightId;
        using (var preflight = await SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/imports/preflight", owner,
            ImportForm(new JsonObject { ["profileId"] = profile.GetProperty("profileId").GetString() }, $"{name}-PREFLIGHT")))
        {
            var text = await preflight.Content.ReadAsStringAsync(Ct);
            preflight.StatusCode.Should().Be(HttpStatusCode.OK, "pre-flight: {0}", text);
            using var body = JsonDocument.Parse(text);
            preflightId = body.RootElement.GetProperty("preflightId").GetGuid();
        }

        // Saved searches (E07-T09): a folder holding a saved search over every document, and spares for the delete probes.
        var folder = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-search-folders", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Folder {name}" });
        var spareFolder = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-search-folders", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Spare folder {name}" });
        var savedSearch = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Everything {name}", ["query"] = "", ["folderId"] = folder.GetProperty("folderId").GetString() });
        var spareSearch = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/saved-searches", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Spare search {name}", ["query"] = "memo" });

        // Search term reports (E07-T10): a completed one with a term, and a spare for the delete probe.
        var termReport = await TermReportAsync(ws, owner, $"Terms {name}");
        var spareTermReport = await TermReportAsync(ws, owner, $"Spare terms {name}");
        var termId = termReport.GetProperty("terms")[0].GetProperty("termId").GetGuid();

        // Document-list views (E16-T09): a shared view, and a personal spare for the delete probe.
        var gridView = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/grid-views", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"View {name}", ["visibility"] = "shared", ["columns"] = new JsonArray(new JsonObject { ["field"] = "filename" }) });
        var spareView = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/grid-views", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Spare view {name}" });

        // Highlight Sets (E16-T12): one applied by the term-hit probes, a spare for the delete probe.
        var highlightSet = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/highlight-sets", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Key terms {name}", ["color"] = "amber", ["terms"] = new JsonArray(new JsonObject { ["expression"] = "memo" }) });
        var spareHighlightSet = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/highlight-sets", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Spare set {name}", ["color"] = "green", ["terms"] = new JsonArray(new JsonObject { ["expression"] = "\"price increase\"" }) });

        // Productions (E12-T02/T03): one finalized (Bates <name>0000001, allocated by the production worker), a draft and a spare draft.
        var productionSnapshot = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", owner, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "Production", ["documentIds"] = new JsonArray(document.DocumentId.ToString()) });
        var productionSnapshotId = productionSnapshot.GetProperty("snapshotId").GetGuid();
        var finalized = await ProductionAsync(ws, owner, productionSnapshotId, name);
        var allocation = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/productions/{finalized}/bates-allocation", owner, HttpStatusCode.Accepted);
        await Productions.RunAllocationAsync(ws, allocation.GetProperty("jobId").GetGuid());
        using (var current = await SendAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/productions/{finalized}", owner))
        using (var finalize = await SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/productions/{finalized}/finalize", owner,
            JsonContent.Create(new JsonObject
            {
                // E12-T07: the attack document has no stored page images (a Technical Issue page) and the QC warnings are accepted.
                ["qcOverrides"] = new JsonArray(new JsonObject { ["check"] = "renderFailure", ["reason"] = "Attack-suite document without page images." }),
                ["acknowledgeWarnings"] = true,
            }),
            ifMatch: current.Headers.ETag!.ToString()))
        {
            finalize.StatusCode.Should().Be(HttpStatusCode.OK, await finalize.Content.ReadAsStringAsync(Ct));
        }

        // Production volumes (E12-T05): a completed run of the finalized production, written by the rendering worker's volume writer.
        var volume = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/productions/{finalized}/volumes", owner, HttpStatusCode.Accepted);
        var volumeId = volume.GetProperty("volumeId").GetGuid();
        var volumes = ProductionVolumeHarness.Over(Exports);
        await volumes.CoordinateAsync(ws);
        await volumes.DeliverOpenChunksAsync(ws, volume.GetProperty("job").GetProperty("jobId").GetGuid());
        await volumes.CoordinateAsync(ws);
        var volumeFiles = await JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/productions/{finalized}/volumes/{volumeId}/files?limit=500", owner,
            HttpStatusCode.OK);

        var draftProduction = await ProductionAsync(ws, owner, productionSnapshotId, name + "D");
        var spareProduction = await ProductionAsync(ws, owner, productionSnapshotId, name + "S");

        // Coding propagation (E09-T05): a preview of the reviewable document's family (a family of one: nothing to change),
        // over a field no other probe codes, so the preview stays current while the suite runs.
        var propagation = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/coding-propagations/preview", owner, HttpStatusCode.OK,
            new JsonObject
            {
                ["sourceDocumentId"] = document.DocumentId.ToString(),
                ["scope"] = "familyAndDuplicates",
                ["fields"] = new JsonArray(fields.Notes),
            });

        // Document security (E05-T06): an ethical wall and a spare (members and scope never touch the owner or the probes'
        // documents), the owner's BreakGlass assignment (activation is MFA-gated) and another user's open activation.
        var wallMember = await Db.CreateUserAsync();
        JsonObject Wall(string wallName) => new()
        {
            ["name"] = wallName,
            ["members"] = new JsonObject { ["userIds"] = new JsonArray(wallMember.ToString()), ["groups"] = new JsonArray() },
            ["scope"] = new JsonObject { ["documentIds"] = new JsonArray(), ["custodians"] = new JsonArray("nobody " + name), ["choices"] = new JsonArray() },
        };
        var wall = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/security/walls", owner, HttpStatusCode.Created, Wall($"Wall {name}"));
        var spareWall = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/security/walls", owner, HttpStatusCode.Created, Wall($"Spare wall {name}"));
        await Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @id, 'BreakGlass', @user)",
            ("ws", ws), ("id", Guid.CreateVersion7()), ("user", owner));
        var glassUser = await Db.CreateUserAsync();
        await Db.AssignAsync(ws, WorkspaceRole.BreakGlass, glassUser);
        var activationId = Guid.CreateVersion7();
        await Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.break_glass_activation (workspace_id, activation_id, user_id, reason, activated_at, expires_at)
            VALUES (@ws, @id, @user, 'Attack suite probe', now(), now() + interval '4 hours')
            """,
            ("ws", ws), ("id", activationId), ("user", glassUser));

        // Redactions (E11-T04): the workspace's default Redaction Set with one redaction on the reviewable document.
        var redactionSets = await JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/redaction-sets", owner, HttpStatusCode.OK);
        var redactionSetId = redactionSets.GetProperty("items")[0].GetProperty("redactionSetId").GetGuid();
        using (var redaction = await SendAsync(HttpMethod.Post,
            $"/api/v1/workspaces/{ws}/documents/{document.DocumentId}/redaction-sets/{redactionSetId}/revisions", owner,
            Json(new JsonObject
            {
                ["changes"] = new JsonArray(new JsonObject
                {
                    ["operation"] = "add",
                    ["pageNumber"] = 1,
                    ["type"] = "black",
                    ["reasonCode"] = "PII",
                    ["rect"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = 500_000, ["h"] = 100_000 },
                }),
            }), ifMatch: "\"0\""))
        {
            redaction.StatusCode.Should().Be(HttpStatusCode.OK, await redaction.Content.ReadAsStringAsync(Ct));
        }

        // Legal hold (E20-T01): a released lock, so the probes can address it while nothing in the workspace is held (a held
        // workspace would answer 423 to the suite's own delete probes).
        var hold = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/preservation-locks", owner, HttpStatusCode.Created,
            new JsonObject { ["reason"] = "Attack suite probe", ["releaseRequiresApproval"] = false });
        var holdId = hold.GetProperty("lockId").GetGuid();
        using (var released = await SendAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/preservation-locks/{holdId}/release", owner,
            Json(new JsonObject { ["reason"] = "Attack suite probe" }), ifMatch: "\"1\""))
        {
            released.StatusCode.Should().Be(HttpStatusCode.OK, await released.Content.ReadAsStringAsync(Ct));
        }
        // Review batches (E10-T05): a first-pass Batch Set over the reviewable document and a QC set checking it.
        var reviewSnapshot = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/snapshots", owner, HttpStatusCode.Created,
            new JsonObject { ["purpose"] = "ReviewBatch", ["documentIds"] = new JsonArray(document.DocumentId.ToString()) });
        var reviewSnapshotId = reviewSnapshot.GetProperty("snapshotId").GetGuid();
        var firstPass = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/review-batch-sets", owner, HttpStatusCode.Created, new JsonObject
        {
            ["name"] = $"First pass {name}",
            ["snapshotId"] = reviewSnapshotId.ToString(),
            ["batchPrefix"] = $"FP{name}",
            ["maxBatchSize"] = 10,
        });
        var firstPassId = firstPass.GetProperty("batchSetId").GetGuid();
        var qcSet = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/review-batch-sets", owner, HttpStatusCode.Created, new JsonObject
        {
            ["name"] = $"QC {name}",
            ["snapshotId"] = reviewSnapshotId.ToString(),
            ["batchPrefix"] = $"QC{name}",
            ["maxBatchSize"] = 10,
            ["reviewPass"] = "qc",
            ["qcOfBatchSetId"] = firstPassId.ToString(),
        });
        var batches = await JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/review-batches?batchSetId={firstPassId}", owner, HttpStatusCode.OK);

        // Privilege logs (E13-T03): a template and a log of the finalized production.
        var logTemplate = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/privilege-log-templates", owner, HttpStatusCode.Created,
            new JsonObject { ["name"] = $"Log template {name}" });
        var privilegeLog = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/privilege-logs", owner, HttpStatusCode.Created, new JsonObject
        {
            ["productionId"] = finalized.ToString(),
            ["templateId"] = logTemplate.GetProperty("templateId").GetString(),
        });

        var layout = await Db.Core.ScalarAsync<Guid>(
            "SELECT layout_id FROM opportunity.coding_layout WHERE workspace_id = @ws AND is_default", ("ws", ws));

        return new WorkspaceResources(
            name,
            ws,
            owner,
            fields,
            document.DocumentId,
            $"{name}-0000001",
            Guid.ParseExact(search.GetProperty("searchId").GetString()!, "N"),
            search.GetProperty("nextCursor").GetString()!,
            bulkSnapshot.GetProperty("snapshotId").GetGuid(),
            exportSnapshot.GetProperty("snapshotId").GetGuid(),
            exportId,
            files.GetProperty("items")[0].GetProperty("fileId").GetGuid(),
            batch.ImportBatchId,
            batch.JobId,
            profile.GetProperty("profileId").GetGuid(),
            spare.GetProperty("profileId").GetGuid(),
            bulkJob.GetProperty("jobId").GetGuid(),
            layout,
            preflightId,
            folder.GetProperty("folderId").GetGuid(),
            spareFolder.GetProperty("folderId").GetGuid(),
            savedSearch.GetProperty("savedSearchId").GetGuid(),
            spareSearch.GetProperty("savedSearchId").GetGuid(),
            termReport.GetProperty("reportId").GetGuid(),
            termId,
            spareTermReport.GetProperty("reportId").GetGuid(),
            gridView.GetProperty("viewId").GetGuid(),
            spareView.GetProperty("viewId").GetGuid(),
            highlightSet.GetProperty("highlightSetId").GetGuid(),
            spareHighlightSet.GetProperty("highlightSetId").GetGuid(),
            productionSnapshotId,
            finalized,
            draftProduction,
            spareProduction,
            $"{name}0000001",
            propagation.GetProperty("previewId").GetGuid(),
            wall.GetProperty("wallId").GetGuid(),
            spareWall.GetProperty("wallId").GetGuid(),
            activationId,
            redactionSetId,
            holdId,
            reviewSnapshotId,
            firstPassId,
            qcSet.GetProperty("batchSetId").GetGuid(),
            batches.GetProperty("items")[0].GetProperty("batchId").GetGuid(),
            volumeId,
            volumeFiles.GetProperty("items")[0].GetProperty("fileId").GetGuid(),
            logTemplate.GetProperty("templateId").GetGuid(),
            privilegeLog.GetProperty("log").GetProperty("logId").GetGuid());
    }

    /// <summary>A draft production of the frozen set with Bates prefix <paramref name="prefix"/>.</summary>
    private async Task<Guid> ProductionAsync(Guid ws, Guid owner, Guid snapshotId, string prefix) =>
        (await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/productions", owner, HttpStatusCode.Created, new JsonObject
        {
            ["snapshotId"] = snapshotId.ToString(),
            ["name"] = "Production " + prefix,
            ["specification"] = new JsonObject { ["bates"] = new JsonObject { ["prefix"] = prefix } },
        })).GetProperty("productionId").GetGuid();

    /// <summary>A search term report over the workspace with one term, run to completion by the API host's runner.</summary>
    private async Task<JsonElement> TermReportAsync(Guid ws, Guid owner, string name)
    {
        var created = await JsonAsync(HttpMethod.Post, $"/api/v1/workspaces/{ws}/search-term-reports", owner, HttpStatusCode.Accepted, new JsonObject
        {
            ["name"] = name,
            ["terms"] = new JsonArray(new JsonObject { ["name"] = "Memo", ["expression"] = "memo" }),
            ["scope"] = new JsonObject { ["kind"] = "workspace" },
        });
        var reportId = created.GetProperty("reportId").GetGuid();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<SearchTermReportRunner>().RunToEndAsync(ws, reportId, cancellationToken: Ct))
                .Should().Be(Core.SearchTermReports.SearchTermReportStatus.Completed);
        }

        return await JsonAsync(HttpMethod.Get, $"/api/v1/workspaces/{ws}/search-term-reports/{reportId}", owner, HttpStatusCode.OK);
    }

    /// <summary>Sends a request as <paramref name="user"/> (null: anonymous) with a fresh Idempotency-Key on writes.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Guid? user, HttpContent? content = null, string? ifMatch = null, string? groups = null,
        string? amr = null, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative)) { Content = content };
        if (user is { } id)
        {
            request.Headers.Add(TestAuthentication.UserHeader, id.ToString());
            request.Headers.Add(TestAuthentication.SessionHeader, SessionOf(id).ToString());
            if (groups is not null)
            {
                request.Headers.Add(TestAuthentication.GroupsHeader, groups);
            }

            if (amr is not null)
            {
                request.Headers.Add(TestAuthentication.AmrHeader, amr);
            }
        }
        else
        {
            request.Headers.Add(TestAuthentication.AnonymousHeader, "1");
        }

        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        return await Client.SendAsync(request, Ct);
    }

    public const string InstallationAdminGroup = "cn=attack-world-installation-admins";

    public const string RetentionApproverGroup = "cn=attack-world-retention-approvers";

    /// <summary>The client address every request of the world comes from (TEST-NET-1, RFC 5737).</summary>
    public static IPAddress ClientAddress { get; } = IPAddress.Parse("192.0.2.10");

    /// <summary>The (stable) server-side session of <paramref name="user"/> in this world.</summary>
    public static Guid SessionOf(Guid user)
    {
        var bytes = user.ToByteArray();
        bytes[0] ^= 0x5a;
        return new Guid(bytes);
    }

    public async Task<JsonElement> JsonAsync(HttpMethod method, string url, Guid user, HttpStatusCode expected, JsonNode? body = null)
    {
        using var response = await SendAsync(method, url, user, body is null ? null : Json(body));
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, "{0} {1}: {2}", method, url, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>A multipart import request: a one-row DAT as <c>file</c> and the JSON <c>request</c> part.</summary>
    public static MultipartFormDataContent ImportForm(JsonNode request, string controlNumber)
    {
        var file = new ByteArrayContent(ImportHarness.Utf8Bom(ImportHarness.Dat(["ControlNumber"], [controlNumber])));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { file, "file", "probe.dat" },
            { new StringContent(request.ToJsonString()), "request" },
        };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _index.DisposeAsync();
        foreach (var generation in Enumerable.Range(1, 4))
        {
            using var _ = await _openSearch.DeleteAsync($"_index_template/{_scope.Prefix}-projection-g{generation}", CancellationToken.None);
        }

        await _scope.DisposeAsync();
        _openSearch.Dispose();
        await Db.DisposeAsync();
    }
}

/// <summary>TestServer leaves the remote address empty; the world's requests come from <see cref="AttackWorld.ClientAddress"/>.</summary>
internal sealed class ClientAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress ??= AttackWorld.ClientAddress;
            return nextMiddleware(context);
        });
        next(app);
    };
}
