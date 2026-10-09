using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Keys;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;
using Opportunity.Application.Workspaces.Deletion;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
using Opportunity.Hosting.Workers;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Messaging;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.Jobs;
using Opportunity.Jobs.Dispatch;
using Opportunity.Messaging;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Search.Workers;
using Opportunity.Search.Writing;
using Opportunity.Testing.OpenSearch;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.Workspaces;

/// <summary>
/// E20-T02 acceptance: deleting a workspace while a bulk coding job, its index tasks and interactive codings are in flight
/// leaves no OpenSearch document, PostgreSQL row or stored object of it, and nothing comes back afterwards. Everything
/// runs as the hosts compose it: the dispatcher host (outbox, task and chunk relays, lease sweeper), the worker host with
/// the bulk-coding and both indexing modules (where the deletion coordinator runs) on RabbitMQ, OpenSearch and envelope-
/// encrypted object storage. Bulk index requests built while the work was in flight are held back until the deletion's
/// first search purge is done, as a writer that passed its fence check just before the fence would be: they land after it
/// and resurrect documents, and the second pass after the guard delay removes them. The wait for resurrection is <c>OPPORTUNITY_DELETION_RESURRECTION_WINDOW</c> (default 5 s; the
/// ticket's 5 minutes on real hardware).
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class WorkspaceDeletionInFlightTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeSpan ResurrectionWindow =>
        TimeSpan.TryParse(Environment.GetEnvironmentVariable("OPPORTUNITY_DELETION_RESURRECTION_WINDOW"), CultureInfo.InvariantCulture, out var window)
            ? window
            : TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Deleting_with_in_flight_bulk_and_index_work_leaves_no_documents_rows_or_objects_and_nothing_is_resurrected()
    {
        await using var world = await World.StartAsync(openSearch, postgres, rabbit);
        var bulk = BulkCodingHarness.Over(world.Db);
        await using var _ = bulk;
        var w = await bulk.WorkspaceAsync();
        var n = await bulk.WorkspaceAsync();
        var placement = world.Api.GetRequiredService<IWorkspaceSearchPlacement>();
        await placement.PlaceAsync(w.Id, new WorkspacePlacementRequest(false), Ct);
        await placement.PlaceAsync(n.Id, new WorkspacePlacementRequest(false), Ct);
        var qc = await bulk.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var neighborQc = await bulk.MemberAsync(n.Id, WorkspaceRole.QcReviewer);
        var docs = await bulk.DocumentsAsync(w.Id, 120, "GONE");
        var neighborDocs = await bulk.DocumentsAsync(n.Id, 20, "STAY");
        foreach (var doc in docs.Take(10).Concat(neighborDocs.Take(5)))
        {
            var ws = docs.Contains(doc) ? w.Id : n.Id;
            await world.PutAsync(ws, doc);
        }

        // Index both workspaces (a bulk coding job each), then start a second job and interactive codings on the doomed one.
        await SubmitAsync(bulk, w, qc, docs, chunkSize: 20, value: true);
        await SubmitAsync(bulk, n, neighborQc, neighborDocs, chunkSize: 10, value: true);
        await world.WaitForAsync(async () => await world.CountAsync(w.Id) == docs.Count && await world.CountAsync(n.Id) == neighborDocs.Count,
            "both workspaces are indexed");

        world.Writes.Armed = true;
        var inFlight = await SubmitAsync(bulk, w, qc, docs, chunkSize: 5, value: false);

        // A few documents edited several times: their held writes carry versions newer than the first purge's deletes.
        foreach (var value in new[] { true, false, true })
        {
            foreach (var doc in docs.Take(10))
            {
                await world.Db.Core.Coding.ApplyAsync(Edit(w, qc, doc, value), Ct);
            }
        }

        using var stopEdits = new CancellationTokenSource();
        var edits = Task.Run(() => EditUntilFencedAsync(world, w, qc, docs, stopEdits.Token), CancellationToken.None);
        await world.WaitForAsync(async () => await world.Db.Core.ScalarAsync<long>(
                "SELECT count(*) FROM opportunity.job_chunk WHERE workspace_id = @ws AND job_id = @job AND status IN (3, 5)",
                ("ws", w.Id), ("job", inFlight)) > 0, "the second job is running");
        await world.WaitForAsync(() => Task.FromResult(world.Writes.Held >= 3), "index writes are in flight");

        var deletion = await RequestAndApproveAsync(world, w.Id);
        await world.WaitForAsync(async () => (await world.Store.GetStepsAsync(deletion, Ct)).Any(s => s.Step == DeletionStep.SearchPurge && s.FinishedAt is not null),
            "the first search purge is done");
        world.Writes.Release();
        var done = await world.WaitForDeletionAsync(deletion);
        await stopEdits.CancelAsync();
        var rejectedEdits = await edits;

        done.Status.Should().Be(WorkspaceDeletionStatus.Completed, done.Error);
        var steps = await world.Store.GetStepsAsync(deletion, Ct);
        var verification = steps.Single(s => s.Step == DeletionStep.Verification).Counts!;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"in flight at the fence: job {inFlight}; interactive codings refused by the fence: {rejectedEdits}; "
            + $"jobs cancelled: {steps.Single(s => s.Step == DeletionStep.Drain).Counts!["jobsCancelled"]}; "
            + $"bulk requests held past the first purge: {world.Writes.Held}; "
            + $"second search pass removed {verification["openSearch"]!["secondPassDocumentsDeleted"]} document(s)");
        world.Writes.Held.Should().BeGreaterThan(0, "index writes were in flight when the workspace was fenced");
        long.Parse(verification["openSearch"]!["secondPassDocumentsDeleted"]!.ToJsonString(), CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(0, "the held writes resurrected documents after the first purge, and the second pass removed them");

        await AssertGoneAsync(world, w.Id, "right after the deletion");
        (await world.CountAsync(n.Id)).Should().Be(neighborDocs.Count, "the neighbour sharing the index keeps every document");
        (await world.ObjectKeysAsync(n.Id)).Should().HaveCount(5);

        // The workers keep running; held writes and redeliveries land in this window. Nothing may come back.
        await Task.Delay(ResurrectionWindow, Ct);
        await AssertGoneAsync(world, w.Id, $"{ResurrectionWindow.TotalSeconds:0} s later");
        (await world.CountAsync(n.Id)).Should().Be(neighborDocs.Count);
        (await world.Db.Core.ScalarAsync<string>("SELECT status FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", w.Id))).Should().Be("Purged");
    }

    [Fact]
    public async Task The_search_purge_deletes_a_dedicated_index_with_every_revision_and_leaves_other_workspaces_alone()
    {
        await using var world = await World.StartAsync(openSearch, postgres, rabbit, workers: false);
        var dedicated = await world.Db.Core.CreateWorkspaceAsync();
        var other = await world.Db.Core.CreateWorkspaceAsync();
        var placement = world.Api.GetRequiredService<IWorkspaceSearchPlacement>();
        await placement.PlaceAsync(dedicated, new WorkspacePlacementRequest(true), Ct);
        await placement.PlaceAsync(other, new WorkspacePlacementRequest(true), Ct);
        var indexes = world.Api.GetRequiredService<IIndexManager>();
        var target = (await indexes.ResolveAsync(dedicated, IndexPurpose.Write, Ct)).WriteTargets[0].Index;
        var otherTarget = (await indexes.ResolveAsync(other, IndexPurpose.Write, Ct)).WriteTargets[0].Index;
        foreach (var index in new[] { target, otherTarget, target.Replace("-g", "-r7-g", StringComparison.Ordinal) })
        {
            using var put = await world.OpenSearchHttp.PutAsJsonAsync($"{index}/_doc/{Guid.NewGuid()}?refresh=true",
                new { workspaceId = index == otherTarget ? other : dedicated }, Ct);
            put.IsSuccessStatusCode.Should().BeTrue(await put.Content.ReadAsStringAsync(Ct));
        }

        var purge = world.Api.GetRequiredService<IWorkspaceSearchPurge>();
        (await purge.CountAsync(dedicated, Ct)).Should().Be(new WorkspaceSearchInventory(2, 2));
        var result = await purge.PurgeAsync(dedicated, Ct);
        result.Should().Be(new WorkspaceSearchPurgeResult(2, 2));
        (await purge.CountAsync(dedicated, Ct)).IsEmpty.Should().BeTrue();
        (await purge.CountAsync(other, Ct)).Should().Be(new WorkspaceSearchInventory(1, 1));
        (await purge.PurgeAsync(dedicated, Ct)).Should().Be(new WorkspaceSearchPurgeResult(0, 0), "a purge is idempotent");
    }

    private static async Task AssertGoneAsync(World world, Guid ws, string when)
    {
        (await world.CountAsync(ws)).Should().Be(0, "no OpenSearch document of the workspace is left {0}", when);
        (await world.DedicatedIndexesAsync(ws)).Should().BeEmpty();
        var rows = new Dictionary<string, long>();
        foreach (var table in await world.Db.Core.ColumnAsync("SELECT t FROM opportunity.workspace_purge_tables() t"))
        {
            var count = await world.Db.Core.ScalarAsync<long>($"SELECT count(*) FROM opportunity.{table} WHERE workspace_id = @ws", ("ws", ws));
            if (count > 0)
            {
                rows[table] = count;
            }
        }

        rows.Should().BeEmpty("no PostgreSQL row of the workspace is left {0}", when);
        (await world.ObjectKeysAsync(ws)).Should().BeEmpty("no stored object of the workspace is left {0}", when);
    }

    /// <summary>Interactive codings every few milliseconds until the write fence refuses one (or the test stops them).</summary>
    private static async Task<int> EditUntilFencedAsync(World world, CodingWorkspace w, Guid user, IReadOnlyList<Guid> docs, CancellationToken stop)
    {
        var refused = 0;
        var random = new Random(167);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await world.Db.Core.Coding.ApplyAsync(Edit(w, user, docs[random.Next(docs.Count)], random.Next(2) == 0), CancellationToken.None);
            }
            catch (PostgresException ex) when (ex.SqlState is WorkspaceFenceViolation.SqlState or PostgresErrorCodes.ForeignKeyViolation)
            {
                refused++;
            }

            await Task.Delay(30, CancellationToken.None);
        }

        return refused;
    }

    private static CodingWriteRequest Edit(CodingWorkspace w, Guid user, Guid document, bool value) => new()
    {
        WorkspaceId = w.Id,
        IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
        Actor = new CodingActor(user, CodingActorType.Human),
        Documents = [new CodingTarget(document)],
        Operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(value))],
    };

    private static async Task<Guid> SubmitAsync(BulkCodingHarness bulk, CodingWorkspace w, Guid user, IReadOnlyList<Guid> docs, int chunkSize, bool value)
    {
        var snapshot = await bulk.SnapshotAsync(w.Id, user, docs);
        var plans = ChunkPlanner.SplitByCount(1, snapshot.DocumentCount!.Value, chunkSize)
            .Select(r => new ChunkPlan(ChunkMembership.SnapshotRange(snapshot.SnapshotId, r.From, r.To), checked((int)r.Count)))
            .ToList();
        var created = await bulk.Jobs.CreateAsync(new NewJob
        {
            WorkspaceId = w.Id,
            JobType = JobType.BulkCoding,
            InitiatedBy = user,
            TargetSnapshotId = snapshot.SnapshotId,
            Parameters = BulkCodingParameters.ToJson([CodingFieldOperation.Set(w.Responsive, JsonValue.Create(value))], securityAffecting: false),
        }, Ct);
        await bulk.Jobs.BeginPreparingAsync(w.Id, created.Job.JobId, Ct);
        await bulk.Jobs.StartAsync(new JobStartRequest(w.Id, created.Job.JobId, ChunkOperationKind.BulkCodingChunk, plans), Ct);
        return created.Job.JobId;
    }

    private static async Task<Guid> RequestAndApproveAsync(World world, Guid ws)
    {
        var requester = await world.Db.CreateUserAsync();
        await world.Db.AssignAsync(ws, WorkspaceRole.WorkspaceAdmin, requester);
        var approver = await world.Db.CreateUserAsync();
        var name = await world.Db.Core.ScalarAsync<string>("SELECT name FROM opportunity.workspace WHERE workspace_id = @ws", ("ws", ws));
        var service = new WorkspaceDeletionService(world.Store, new WorkspaceReader(world.Db.Core.AppDataSource), World.DeletionOptions, TimeProvider.System);
        var requested = await service.RequestAsync(DeletionHarness.Principal(requester), ws,
            new WorkspaceDeletionRequest(DeletionRetentionProfile.PurgeAll, "Return-or-destroy deadline under the protective order", null, name), Ct);
        requested.Status.Should().Be(WorkspaceDeletionResultStatus.Ok);
        var approved = await service.ApproveAsync(DeletionHarness.Caller(approver, canApprove: true), requested.Deletion!.DeletionId,
            requested.Deletion.Version, null, Ct);
        approved.Status.Should().Be(WorkspaceDeletionResultStatus.Ok);
        return approved.Deletion!.DeletionId;
    }

    /// <summary>
    /// The stack under test: a migrated database, an OpenSearch index prefix, a RabbitMQ virtual host, envelope-encrypted
    /// filesystem storage with local keys, the dispatcher host and the worker host (bulk coding, interactive and chunk
    /// indexing, the deletion coordinator), with test timings.
    /// </summary>
    private sealed class World : IAsyncDisposable
    {
        public static readonly WorkspaceDeletionOptions DeletionOptions = new()
        {
            WaitingPeriod = TimeSpan.Zero,
            AllowShortWaitingPeriod = true,
        };

        private readonly string _keys = Directory.CreateTempSubdirectory("opportunity-inflight-keys-").FullName;
        private readonly string _objects = Directory.CreateTempSubdirectory("opportunity-inflight-objects-").FullName;
        private readonly List<(ServiceProvider Provider, List<IHostedService> Hosted)> _hosts = [];
        private OpenSearchIndexScope _scope = null!;
        private RabbitMqVirtualHost _vhost = null!;
        private MeterCapture _meters = null!;

        public AuthorizationDatabase Db { get; private set; } = null!;

        public ServiceProvider Api { get; private set; } = null!;

        public WorkspaceDeletionStore Store { get; private set; } = null!;

        public HttpClient OpenSearchHttp { get; private set; } = null!;

        public HeldWrites Writes { get; } = new();

        private Opportunity.Storage.Encryption.EnvelopeObjectStore Objects { get; set; } = null!;

        public static async Task<World> StartAsync(OpenSearchFixture openSearch, MigrationPostgresFixture postgres, RabbitMqFixture rabbit, bool workers = true)
        {
            var world = new World();
            await world.InitializeAsync(openSearch, postgres, rabbit, workers);
            return world;
        }

        private async Task InitializeAsync(OpenSearchFixture openSearch, MigrationPostgresFixture postgres, RabbitMqFixture rabbit, bool workers)
        {
            Db = await AuthorizationDatabase.CreateAsync(postgres);
            Store = new WorkspaceDeletionStore(Db.Core.AppDataSource);
            _scope = openSearch.CreateIndexScope();
            _meters = new MeterCapture();
            OpenSearchHttp = new HttpClient { BaseAddress = openSearch.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
            var keys = new Opportunity.Security.Keys.LocalKeyStoreOptions { KeyDirectory = _keys };
            var ring = new WorkspaceKeyService(new Data.Keys.WorkspaceDataKeyStore(Db.Core.AppDataSource),
                new Opportunity.Security.Keys.LocalKeyEncryptionKeyProvider(keys, new Opportunity.Security.Keys.EnvironmentSecretProvider(_ => null)),
                new WorkspaceKeyOptions(), TimeProvider.System);
            Objects = new Opportunity.Storage.Encryption.EnvelopeObjectStore(
                new Opportunity.Storage.FileSystem.FileSystemObjectStore(new Opportunity.Storage.FileSystemObjectStoreOptions { RootPath = _objects }), ring);

            var options = new OpenSearchOptions { Endpoint = openSearch.BaseAddress, IndexPrefix = _scope.Prefix, Placement = { CacheTtl = TimeSpan.Zero } };
            var api = new ServiceCollection().AddLogging();
            api.AddSingleton(options);
            api.AddSingleton<IIndexPlacementStore>(new IndexPlacementStore(Db.Core.AppDataSource));
            api.AddOpenSearchIndexTemplateBootstrap(options);
            api.AddOpenSearchIndexManagement(options);
            Api = api.BuildServiceProvider();
            await Api.GetServices<IInfrastructureBootstrapStep>().Single().RunAsync(Ct);
            if (!workers)
            {
                return;
            }

            _vhost = await rabbit.CreateVirtualHostAsync(Ct);
            await using (var _ = await MessagingHarness.StartAsync(_vhost.AmqpUri, _vhost.AmqpUri))
            {
            }

            await StartAsync(Dispatcher());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings("inflight-worker")).Build();
            var worker = Common(configuration);
            worker.AddSingleton<IObjectStore>(Objects);
            worker.AddSingleton<IWorkspaceCryptoShredder>(ring);
            worker.AddSingleton<ISigningKeyProvider>(new Opportunity.Security.Keys.LocalSigningKeyProvider(keys, new Opportunity.Security.Keys.EnvironmentSecretProvider(_ => null)));
            worker.AddSingleton(LeaseOptions());
            worker.AddSingleton(new JobChunkConsumerOptions { WorkerId = "inflight-worker" });
            worker.AddIndexingWorker(configuration);
            worker.AddChunkIndexWorkerModule(configuration);
            worker.AddBulkCodingWorker(configuration);
            worker.AddSingleton(sp => new OpenSearchConnection(sp.GetRequiredService<OpenSearchOptions>(), new HoldingHandler(Writes)));
            await StartAsync(worker);
        }

        public async Task PutAsync(Guid ws, Guid document)
        {
            var bytes = Encoding.UTF8.GetBytes("synthetic native " + document);
            await Objects.PutAsync(ObjectKeys.Native(ws, document, Sha256Digest.Compute(bytes)), new MemoryStream(bytes), cancellationToken: Ct);
        }

        public async Task<List<string>> ObjectKeysAsync(Guid ws)
        {
            var keys = new List<string>();
            await foreach (var listing in Objects.ListPrefixAsync(ObjectPrefix.Workspace(ws), Ct))
            {
                keys.Add(listing.Key.Value);
            }

            return keys;
        }

        /// <summary>Documents of the workspace in any index under the test prefix (shared or dedicated), after a refresh.</summary>
        public async Task<long> CountAsync(Guid ws)
        {
            using (var _ = await OpenSearchHttp.PostAsync($"{_scope.Prefix}-*/_refresh?allow_no_indices=true", null, Ct))
            {
            }

            using var response = await OpenSearchHttp.PostAsJsonAsync($"{_scope.Prefix}-*/_count?allow_no_indices=true",
                new { query = new { term = new { workspaceId = ws.ToString("D") } } }, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            response.IsSuccessStatusCode.Should().BeTrue(body);
            return JsonDocument.Parse(body).RootElement.GetProperty("count").GetInt64();
        }

        public async Task<List<string>> DedicatedIndexesAsync(Guid ws)
        {
            using var response = await OpenSearchHttp.GetAsync($"_cat/indices/{_scope.Prefix}-ws-{ws:N}*?format=json&expand_wildcards=all", Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            return [.. JsonDocument.Parse(body).RootElement.EnumerateArray().Select(i => i.GetProperty("index").GetString()!)];
        }

        public async Task WaitForAsync(Func<Task<bool>> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!await condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    DateTime.UtcNow.Should().BeBefore(deadline, "waiting until {0}; state: {1}", what, await StateAsync());
                }

                await Task.Delay(200, Ct);
            }
        }

        /// <summary>Diagnostics on timeout: job, chunk, index task and outbox states per workspace.</summary>
        private Task<string> StateAsync() => Db.Core.ScalarAsync<string>(
            """
            SELECT concat_ws(' | ',
              (SELECT string_agg(workspace_id || ' job ' || status || coalesce(' ' || status_reason, ''), '; ') FROM opportunity.job),
              (SELECT string_agg(s, '; ') FROM (SELECT 'chunks ' || status || ':' || count(*) AS s FROM opportunity.job_chunk GROUP BY status) c),
              (SELECT string_agg(s, '; ') FROM (SELECT 'tasks ' || status || ':' || count(*) AS s FROM opportunity.index_chunk_task GROUP BY status) t),
              (SELECT string_agg(s, '; ') FROM (SELECT 'outbox ' || status || ':' || count(*) AS s FROM opportunity.search_outbox GROUP BY status) o),
              (SELECT string_agg(coalesce(error_code, '') || ' ' || coalesce(last_error, ''), '; ') FROM opportunity.job_chunk WHERE status = 6))
            """);

        public async Task<WorkspaceDeletion> WaitForDeletionAsync(Guid deletionId)
        {
            WorkspaceDeletion? current = null;
            await WaitForAsync(async () =>
            {
                current = await Store.GetAsync(deletionId, Ct);
                return current!.Status is WorkspaceDeletionStatus.Completed or WorkspaceDeletionStatus.CompletedWithResiduals;
            }, "the deletion finishes (the coordinator runs in the worker host)");
            return current!;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (provider, hosted) in Enumerable.Reverse(_hosts))
            {
                foreach (var service in Enumerable.Reverse(hosted))
                {
                    await service.StopAsync(CancellationToken.None);
                }

                await provider.DisposeAsync();
            }

            await Api.DisposeAsync();
            using (var _ = await OpenSearchHttp.DeleteAsync($"_index_template/{_scope.Prefix}-projection-g2", CancellationToken.None))
            {
            }

            await _scope.DisposeAsync();
            OpenSearchHttp.Dispose();
            if (_vhost is not null)
            {
                await _vhost.DisposeAsync();
            }

            _meters.Dispose();
            await Db.DisposeAsync();
            Directory.Delete(_keys, recursive: true);
            Directory.Delete(_objects, recursive: true);
        }

        private Dictionary<string, string?> Settings(string client) => new()
        {
            ["ConnectionStrings:App"] = Db.Core.AppConnectionString,
            ["ConnectionStrings:OpenSearch"] = _scope is null ? null : OpenSearchHttp.BaseAddress!.ToString(),
            ["ConnectionStrings:RabbitMq"] = _vhost.AmqpUri.ToString(),
            ["OpenSearch:IndexPrefix"] = _scope!.Prefix,
            ["OpenSearch:Placement:CacheTtl"] = "00:00:00",
            ["Messaging:RabbitMq:ClientName"] = client,
            ["Messaging:RabbitMq:RetryBaseDelay"] = "00:00:00.300",
            ["Messaging:RabbitMq:MaxTransportRetries"] = "3",
            ["Messaging:RabbitMq:DeliveryLimit"] = "3",
            ["Messaging:RabbitMq:NetworkRecoveryInterval"] = "00:00:00.500",
            ["Messaging:RabbitMq:MaxConnectRetryDelay"] = "00:00:01",
            ["ObjectStorage:Provider"] = "FileSystem",
            [$"{ChunkIndexWorkerOptions.SectionName}:WorkerId"] = client,
            [$"{ChunkIndexWorkerOptions.SectionName}:LeaseDuration"] = Lease.ToString("c", CultureInfo.InvariantCulture),
            [$"{ChunkIndexWorkerOptions.SectionName}:HeartbeatInterval"] = "00:00:01",
            [$"{ChunkIndexWorkerOptions.SectionName}:RetryBaseDelay"] = "00:00:00.200",
            [$"{ChunkIndexWorkerOptions.SectionName}:RetryMaxDelay"] = "00:00:01",
            ["Search:Bulk:MaxRequestActions"] = "8",
            ["WorkspaceDeletion:WaitingPeriod"] = "00:00:00",
            ["WorkspaceDeletion:AllowShortWaitingPeriod"] = "true",
            ["WorkspaceDeletion:PollInterval"] = "00:00:00.250",
            ["WorkspaceDeletion:LeaseDuration"] = "00:00:10",
            ["WorkspaceDeletion:DrainSettleDelay"] = "00:00:01",
            ["WorkspaceDeletion:ResurrectionGuardDelay"] = "00:00:04",
            ["WorkspaceDeletion:BatchSize"] = "50",
        };

        private ServiceCollection Common(IConfiguration configuration)
        {
            var services = new ServiceCollection();
            services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(configuration);
            services.AddSingleton(_ => NpgsqlDataSource.Create(Db.Core.AppConnectionString));
            services.AddSingleton(_meters.Metrics);
            return services;
        }

        private ServiceCollection Dispatcher()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings("inflight-dispatcher")).Build();
            var services = Common(configuration);
            services.AddPostgresSearchWorkStore();
            services.AddPostgresJobChunkStore();
            services.AddJobLeaseSweeper(LeaseOptions());
            services.AddSearchWorkHousekeeping(new SearchWorkHousekeepingOptions
            {
                Interval = TimeSpan.FromSeconds(1),
                DispatchedTimeout = TimeSpan.FromSeconds(5),
                LeaseGrace = TimeSpan.FromMilliseconds(500),
            });
            services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
            services.AddOutboxDispatcher(new OutboxDispatcherOptions
            {
                Owner = "inflight-dispatcher",
                ClaimDuration = TimeSpan.FromSeconds(3),
                OutboxPollInterval = TimeSpan.FromMilliseconds(200),
                OutboxPollIntervalWithoutListener = TimeSpan.FromMilliseconds(200),
                IndexTaskPollInterval = TimeSpan.FromMilliseconds(200),
                JobChunkPollInterval = TimeSpan.FromMilliseconds(200),
                WorkspaceRefreshInterval = TimeSpan.FromMilliseconds(300),
                BacklogSampleInterval = TimeSpan.FromSeconds(1),
                ShutdownGrace = TimeSpan.FromSeconds(1),
                RetryDelay = TimeSpan.FromMilliseconds(200),
                JobChunkLimits = new JobChunkDispatchLimits { Operations = JobChunkRelay.DispatchedOperations, RedispatchAfter = TimeSpan.FromSeconds(5) },
            });
            return services;
        }

        private static JobLeaseOptions LeaseOptions() => new()
        {
            LeaseDuration = Lease,
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            SweepInterval = TimeSpan.FromSeconds(1),
            SweepGrace = TimeSpan.FromMilliseconds(500),
        };

        private async Task StartAsync(ServiceCollection services)
        {
            var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().ToList();
            _hosts.Add((provider, hosted));
            foreach (var service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>While armed, every OpenSearch <c>_bulk</c> request of the worker waits for <see cref="Release"/>.</summary>
    private sealed class HeldWrites
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public bool Armed { get; set; }

        public int Held => Volatile.Read(ref _held);

        public void Release() => _release.TrySetResult();

        public Task WaitAsync()
        {
            Interlocked.Increment(ref _held);
            return _release.Task;
        }
    }

    /// <summary>
    /// A writer that passed its fence check and built its bulk request, then is held up (GC pause, slow network) until the
    /// deletion's first search purge is done: the request cannot be stopped any more, so it lands afterwards and would
    /// resurrect its documents unless the second pass removes them.
    /// </summary>
    private sealed class HoldingHandler(HeldWrites holds) : DelegatingHandler(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (holds.Armed && request.RequestUri?.AbsolutePath.EndsWith("/_bulk", StringComparison.Ordinal) == true)
            {
                await holds.WaitAsync();
                return await base.SendAsync(request, CancellationToken.None);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
