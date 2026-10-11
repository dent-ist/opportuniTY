using System.Globalization;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Audit;
using Opportunity.Application.Backup;
using Opportunity.Application.Keys;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Backup;
using Opportunity.Data.Search;
using Opportunity.Hosting.Operations;
using Opportunity.IntegrationTests.Audit;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.IntegrationTests.SearchWork;
using Opportunity.Search;
using Opportunity.Security.Keys;
using Opportunity.Storage;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Backup;

/// <summary>
/// E19-T08 against real PostgreSQL and a single OpenSearch node (ADR-016 §3, design §3–§7): the snapshot job commits the
/// per-workspace anchors and takes a snapshot; work continues; OpenSearch is restored from the older snapshot (now behind
/// PostgreSQL), and the restore tool — run through its CLI, as an operator would — re-pends the work above the anchor,
/// lowers the watermark, redrives in-flight work, ends break-glass, drops search sessions, writes Integrity.Restored with
/// an AfterRestore checkpoint and a signed record, and passes its pre-open gates. After "reopening" (the real dispatcher
/// relay and interactive index worker) the convergence check reports 0 ahead / 0 stale / 0 missing. A database rolled
/// back behind the index is caught by the ahead-check.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class RestoreConsistencyTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_older_snapshot_is_caught_up_from_postgres_and_converges_with_no_stale_version()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        await using var tool = await RestoreTool.CreateAsync(h);

        // Two workspaces, one dedicated and one in the shared pool, coded once, indexed and visible.
        var dedicated = await h.Db.WorkspaceAsync(12);
        await h.Indexes.PlaceAsync(dedicated.Id, new WorkspacePlacementRequest(DedicatedIndex: true), Ct);
        var shared = await h.Db.WorkspaceAsync(8);
        foreach (var w in new[] { dedicated, shared })
        {
            foreach (var doc in w.Documents)
            {
                await InteractiveIndexWorkerTests.CodeAsync(h, w, doc, true);
            }

            await DrainAsync(h, w.Id);
            await WatermarkTestSupport.TickAsync(h.Services, h.Db.Core.AppDataSource, w.Id);
        }

        var anchorDedicated = await WatermarkAsync(h, dedicated.Id);
        anchorDedicated.Should().BeGreaterThan(0);

        // Snapshot job: anchors first, then the snapshot.
        var snapshotOutput = await tool.RunAsync(RestoreOperationsCli.ExitSuccess, "backup", "search-snapshot");
        var snapshotName = snapshotOutput.Split('\t')[1];
        snapshotOutput.Should().Contain("Success").And.Contain("2 anchor(s)");
        var record = await tool.Services.GetRequiredService<ISearchSnapshotStore>().FindAsync(tool.Repository, snapshotName, Ct);
        record!.State.Should().Be(SearchSnapshotState.Success);
        var anchors = await tool.Services.GetRequiredService<ISearchSnapshotStore>().GetAnchorsAsync(record.SnapshotId, Ct);
        anchors[dedicated.Id].IndexedThroughGeneration.Should().Be(anchorDedicated);
        anchors[dedicated.Id].PlacementKind.Should().Be(2);

        // Work after the snapshot: recoding, a deletion, and one edit the old timeline had dispatched but not applied.
        foreach (var doc in dedicated.Documents.Take(6))
        {
            await InteractiveIndexWorkerTests.CodeAsync(h, dedicated, doc, false);
        }

        await h.SoftDeleteAsync(dedicated.Id, dedicated.Documents[^1]);
        await InteractiveIndexWorkerTests.CodeAsync(h, shared, shared.Documents[0], false);
        await DrainAsync(h, dedicated.Id);
        await DrainAsync(h, shared.Id);
        await WatermarkTestSupport.TickAsync(h.Services, h.Db.Core.AppDataSource, dedicated.Id);
        await InteractiveIndexWorkerTests.CodeAsync(h, shared, shared.Documents[1], false);
        (await h.DispatchAsync(shared.Id)).Should().ContainSingle("the lost timeline published it; no worker applied it");
        var latest = await WatermarkAsync(h, dedicated.Id);
        latest.Should().BeGreaterThan(anchorDedicated);

        // Security state of the past that a restore must end.
        await h.Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.break_glass_activation (workspace_id, activation_id, user_id, reason, activated_at, expires_at)
            VALUES (@ws, gen_random_uuid(), gen_random_uuid(), 'investigation', now(), now() + interval '1 hour');
            INSERT INTO opportunity.search_session (workspace_id, search_id, user_id, query_text, sort_keys, page_size, pit_id, expires_at)
            VALUES (@ws, gen_random_uuid(), gen_random_uuid(), 'privileged', '[]', 50, 'pit-of-the-old-cluster', now() + interval '1 hour');
            """, ("ws", shared.Id));

        // R1: the plan picks the snapshot for a target after it and refuses one that ends too late.
        var target = record.EndedAt!.Value.AddSeconds(90);
        var info = tool.PgBackRestInfo(record.RequestedAt.AddHours(-1));
        var plan = await tool.RunAsync(RestoreOperationsCli.ExitSuccess, "restore", "plan", "--pgbackrest-info", info,
            "--target-time", Iso(target), "--decided-at", Iso(target.AddMinutes(5)));
        plan.Should().Contain("verdict\tACCEPTED").And.Contain("path\tsnapshot").And.Contain($"snapshot\t{snapshotName}");
        (await tool.RunAsync(RestoreOperationsCli.ExitRefused, "restore", "plan", "--pgbackrest-info", info,
            "--target-time", Iso(record.EndedAt.Value.AddSeconds(10)), "--snapshot", snapshotName)).Should().Contain("ahead of PostgreSQL");

        // R4: OpenSearch back to the snapshot: behind PostgreSQL, never ahead.
        (await tool.RunAsync(RestoreOperationsCli.ExitSuccess, "restore", "search-snapshot", "--snapshot", snapshotName))
            .Should().Contain($"from {snapshotName}");
        var behind = await tool.Search.CompareAsync(dedicated.Id, null, [], Ct);
        behind.Ahead.Should().Be(0);
        behind.Stale.Should().Be(7, "six recodings and a deletion came after the snapshot: lag, which catch-up removes");
        behind.Missing.Should().Be(0);
        behind.Compared.Should().Be(dedicated.Documents.Count);
        var sharedBehind = await tool.Search.CompareAsync(shared.Id, null, [], Ct);
        (sharedBehind.Ahead, sharedBehind.Stale).Should().Be((0, 2), "one recoding applied after the snapshot and one only dispatched");

        // R6: reconcile.
        var restoreId = Guid.CreateVersion7();
        var reconcile = await tool.RunAsync(RestoreOperationsCli.ExitSuccess, "restore", "reconcile", "--restore-id", restoreId.ToString(),
            "--path", "snapshot", "--snapshot", snapshotName, "--target-time", Iso(target), "--backup-label", "20261011-000000F", "--operator", "dr-drill");
        reconcile.Should().Contain($"{dedicated.Id:D}\t{anchorDedicated}\t7\t0\t{latest}->{anchorDedicated}");
        (await WatermarkAsync(h, dedicated.Id)).Should().Be(anchorDedicated, "freshness must read Updating until the replay applied");
        (await h.Db.OutboxStatusesAsync(shared.Id))[SearchOutboxStatus.Dispatched].Should().Be(0, "in-flight work is redriven");
        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.break_glass_activation WHERE workspace_id = @ws AND ended_reason = 'Revoked'", ("ws", shared.Id)))
            .Should().Be(1);
        (await h.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.search_session WHERE workspace_id = @ws", ("ws", shared.Id))).Should().Be(0);

        // R6d: the restore record, then the pre-open gates.
        var recorded = await tool.RunAsync(RestoreOperationsCli.ExitSuccess, "restore", "record", "--restore-id", restoreId.ToString());
        recorded.Should().Contain("chains\t3").And.Contain("AfterRestore").And.Contain("sys/restores/");
        var restored = await h.Db.Core.ColumnAsync(
            $"SELECT coalesce(workspace_id::text, 'system') || ' ' || (details ->> 'RestoredSequence') FROM audit.audit_event WHERE action = 'Restored' AND details ->> 'RestoreId' = '{restoreId:D}'");
        restored.Should().HaveCount(3);
        await tool.AssertSignedRecordAsync(restoreId);
        (await tool.RunAsync(RestoreOperationsCli.ExitFailed, "restore", "reconcile", "--restore-id", restoreId.ToString(), "--path", "snapshot",
            "--snapshot", snapshotName, "--operator", "dr-drill")).Should().BeEmpty("reconcile after the record is refused (writers may run)");

        var gates = await tool.RunAsync(RestoreOperationsCli.ExitSuccess, "restore", "verify", "--restore-id", restoreId.ToString(), "--full");
        gates.Should().Contain("all gates passed");
        foreach (var gate in new[] { "restore-record", "audit-chains", "keys", "work-state", "watermark", "security-reset", "lost-window", "search-placements", "search-not-ahead", "objects" })
        {
            gates.Should().Contain($"{gate}\tPASS", gate);
        }

        // R7–R8: reopen (the dispatcher relay and the index worker) and converge.
        await DrainAsync(h, dedicated.Id);
        await DrainAsync(h, shared.Id);
        await WatermarkTestSupport.TickAsync(h.Services, h.Db.Core.AppDataSource, dedicated.Id);
        await WatermarkTestSupport.TickAsync(h.Services, h.Db.Core.AppDataSource, shared.Id);
        (await WatermarkAsync(h, dedicated.Id)).Should().Be(latest);
        var converged = await tool.RunAsync(RestoreOperationsCli.ExitSuccess, "restore", "verify", "--restore-id", restoreId.ToString(), "--converge");
        converged.Should().Contain("search-converged\tPASS").And.Contain("0 ahead, 0 stale, 0 missing");
        await InteractiveIndexWorkerTests.AssertIndexMatchesPostgresAsync(h, dedicated.Id, dedicated.Documents);

        // A database behind the index (what a wrong snapshot choice would cause) fails the ahead-check.
        await h.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET document_version = document_version - 1 WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", dedicated.Id), ("doc", dedicated.Documents[0]));
        var ahead = await tool.RunAsync(RestoreOperationsCli.ExitRefused, "restore", "verify", "--restore-id", restoreId.ToString(), "--full");
        ahead.Should().Contain("search-not-ahead\tFAIL").And.Contain($"{dedicated.Id:D}/{dedicated.Documents[0]:D}");
    }

    private static async Task DrainAsync(IndexWorkerHarness h, Guid workspaceId)
    {
        for (var i = 0; i < 200; i++)
        {
            foreach (var message in await h.DispatchAsync(workspaceId))
            {
                await h.HandleAsync(message);
            }

            if ((await h.Db.OutboxStatusesAsync(workspaceId)).Where(s => s.Key != SearchOutboxStatus.Applied).Sum(s => s.Value) == 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The search outbox did not drain.");
    }

    private static Task<long> WatermarkAsync(IndexWorkerHarness h, Guid workspaceId) => h.Db.Core.ScalarAsync<long>(
        "SELECT indexed_through_generation FROM opportunity.workspace_search_watermark WHERE workspace_id = @ws", ("ws", workspaceId));

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The operations CLI over the harness's database and index prefix, with a filesystem snapshot repository.</summary>
    private sealed class RestoreTool : IAsyncDisposable
    {
        private readonly IndexWorkerHarness _h;
        private readonly string _temp;

        private RestoreTool(IndexWorkerHarness h, IConfiguration configuration, string temp, ServiceProvider services)
        {
            _h = h;
            Configuration = configuration;
            _temp = temp;
            Services = services;
        }

        public IConfiguration Configuration { get; }

        public ServiceProvider Services { get; }

        public string Repository => "r-" + _h.Scope.Prefix;

        public ISearchRestoreChecks Search => Services.GetRequiredService<ISearchRestoreChecks>();

        private string ObjectsRoot => Path.Combine(_temp, "objects");

        public static async Task<RestoreTool> CreateAsync(IndexWorkerHarness h)
        {
            var temp = Directory.CreateTempSubdirectory("opportunity-restore-").FullName;
            var sealer = await CoreSchemaDatabase.CreateLoginAsync(h.Db.Core.ConnectionString, "opportunity_audit_sealer");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = h.Db.Core.AppConnectionString,
                ["ConnectionStrings:AuditSealer"] = sealer,
                ["ConnectionStrings:OpenSearch"] = h.Http.BaseAddress!.ToString(),
                ["OpenSearch:IndexPrefix"] = h.Scope.Prefix,
                ["OpenSearch:Placement:CacheTtl"] = "00:00:00",
                ["KeyManagement:Local:KeyDirectory"] = Path.Combine(temp, "keys"),
                ["Backup:SearchSnapshots:Repository"] = "r-" + h.Scope.Prefix,
                ["Backup:SearchSnapshots:RepositoryType"] = "fs",
                ["Backup:SearchSnapshots:RepositorySettings:location"] = $"{OpenSearchFixture.SnapshotRoot}/{h.Scope.Prefix}",
                ["ObjectStorage:Provider"] = "FileSystem",
                ["ObjectStorage:FileSystem:RootPath"] = Path.Combine(temp, "objects"),
            }).Build();

            // The stores and search checks the CLI composes, for direct assertions.
            var openSearchOptions = h.Services.GetRequiredService<OpenSearchOptions>();
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(_ => Npgsql.NpgsqlDataSource.Create(h.Db.Core.AppConnectionString));
            services.AddPostgresIndexPlacementStore();
            services.AddPostgresReindexStore();
            services.AddPostgresBackupStores();
            services.AddOpenSearchBackup(openSearchOptions, RestoreOperationsCli.BindSnapshotOptions(configuration));
            return new RestoreTool(h, configuration, temp, services.BuildServiceProvider());
        }

        /// <summary>Runs one CLI command, asserts its exit code and returns its output.</summary>
        public async Task<string> RunAsync(int expectedExit, params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await RestoreOperationsCli.RunAsync(args, output, error, Configuration, AddObjects, Ct);
            exit.Should().Be(expectedExit, "{0}\n{1}\n{2}", string.Join(' ', args), output, error);
            return output.ToString();
        }

        /// <summary>A pgBackRest info file with one full backup that stopped at <paramref name="stop"/>.</summary>
        public string PgBackRestInfo(DateTimeOffset stop)
        {
            var file = Path.Combine(_temp, "info.json");
            var seconds = stop.ToUnixTimeSeconds();
            File.WriteAllText(file, new JsonArray(new JsonObject
            {
                ["name"] = "opportunity",
                ["status"] = new JsonObject { ["code"] = 0, ["message"] = "ok" },
                ["archive"] = new JsonArray(new JsonObject { ["min"] = "000000010000000000000001", ["max"] = "000000010000000000000009" }),
                ["backup"] = new JsonArray(new JsonObject
                {
                    ["label"] = "20261011-000000F",
                    ["type"] = "full",
                    ["error"] = false,
                    ["timestamp"] = new JsonObject { ["start"] = seconds - 60, ["stop"] = seconds },
                }),
            }).ToJsonString());
            return file;
        }

        /// <summary>The record under sys/restores/ is canonical JSON signed with the audit checkpoint key.</summary>
        public async Task AssertSignedRecordAsync(Guid restoreId)
        {
            await using var objects = new ServiceCollection().AddObjectStorage(new ObjectStorageOptions
            {
                Provider = ObjectStorageProvider.FileSystem,
                FileSystem = { RootPath = ObjectsRoot },
            }).BuildServiceProvider();
            var store = objects.GetRequiredService<IObjectStore>();
            var listing = new List<ObjectListing>();
            await foreach (var item in store.ListPrefixAsync(ObjectPrefix.Parse("sys/restores/"), Ct))
            {
                listing.Add(item);
            }

            var key = listing.Should().ContainSingle().Subject.Key;
            key.Value.Should().StartWith($"sys/restores/{restoreId:N}/");
            await using var stream = await store.OpenReadAsync(key, cancellationToken: Ct);
            var signed = (await JsonNode.ParseAsync(stream, cancellationToken: Ct))!.AsObject();
            signed["record"]!["restoreId"]!.GetValue<string>().Should().Be(restoreId.ToString("D"));
            signed["record"]!["restoredHeads"]!.AsArray().Should().HaveCount(3);
            var canonical = System.Text.Encoding.UTF8.GetBytes(Application.Audit.Chain.CanonicalJson.Canonicalize(signed["record"]!.ToJsonString()));
            var signer = new LocalSigningKeyProvider(
                new LocalKeyStoreOptions { KeyDirectory = Path.Combine(_temp, "keys") }, new EnvironmentSecretProvider(_ => null));
            var keyId = signed["keyId"]!.GetValue<string>();
            (await signer.VerifyAsync(new KeySignature(SigningKeyPurposes.AuditCheckpoint, int.Parse(keyId[(keyId.LastIndexOf('v') + 1)..], CultureInfo.InvariantCulture),
                KeySignature.Es256, Convert.FromBase64String(signed["signature"]!.GetValue<string>())), canonical, Ct)).Should().BeTrue();
        }

        private static void AddObjects(IServiceCollection services, IConfiguration configuration)
        {
            var options = new ObjectStorageOptions();
            configuration.GetSection(ObjectStorageOptions.SectionName).Bind(options);
            services.AddObjectStorage(options);
        }

        public async ValueTask DisposeAsync()
        {
            // Snapshots are blobs inside the container's path.repo: delete them with the repository.
            foreach (var name in await RepositorySnapshotsAsync())
            {
                using var _ = await _h.Http.DeleteAsync($"_snapshot/{Repository}/{name}", CancellationToken.None);
            }

            using (await _h.Http.DeleteAsync($"_snapshot/{Repository}", CancellationToken.None))
            {
            }

            await Services.DisposeAsync();
            Directory.Delete(_temp, recursive: true);
        }

        private async Task<IReadOnlyList<string>> RepositorySnapshotsAsync()
        {
            using var response = await _h.Http.GetAsync($"_snapshot/{Repository}/_all", CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
            return [.. (body?["snapshots"] as JsonArray ?? []).Select(s => s!["snapshot"]!.GetValue<string>())];
        }
    }
}
