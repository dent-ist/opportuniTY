using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Search.Indexing;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Content;
using Opportunity.IntegrationTests.Exports;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
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
    Guid PreflightId)
{
    /// <summary>Fresh identifiers that exist nowhere: the reference every foreign identifier must be indistinguishable from.</summary>
    public static WorkspaceResources Unknown(CodingWorkspace fields) => new(
        "unknown", Guid.CreateVersion7(), Guid.CreateVersion7(), fields, Guid.CreateVersion7(), "ZZ-0000001", Guid.CreateVersion7(),
        Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7());

    /// <summary>Every identifier of the set, in the spellings a response could carry them (D and N formats).</summary>
    public IEnumerable<string> IdentifierSpellings()
    {
        Guid[] ids =
        [
            WorkspaceId, DocumentId, SearchId, BulkSnapshotId, ExportSnapshotId, ExportId, ExportFileId, ImportId, ImportJobId, ImportProfileId,
            SpareProfileId, BulkCodingJobId, LayoutId, PreflightId,
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
            builder.UseSetting("Jobs:Events:MaxStreamDuration", "00:00:01");
            builder.ConfigureTestServices(services =>
            {
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
            preflightId);
    }

    /// <summary>Sends a request as <paramref name="user"/> (null: anonymous) with a fresh Idempotency-Key on writes.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Guid? user, HttpContent? content = null, string? ifMatch = null, string? groups = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative)) { Content = content };
        if (user is { } id)
        {
            request.Headers.Add(TestAuthentication.UserHeader, id.ToString());
            if (groups is not null)
            {
                request.Headers.Add(TestAuthentication.GroupsHeader, groups);
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

        return await Client.SendAsync(request, Ct);
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
