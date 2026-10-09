using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Reindex;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Workspaces;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Correctness.ShadowLedger;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.SearchWork;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Reindex;
using Opportunity.Search.Workers;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T11 against real PostgreSQL and OpenSearch: the reindex coordinator, the chunk index worker (reindex tasks) and the
/// interactive index worker (dual-target writes) run in-process, driven step by step. Coding continues during the
/// rebuild and the shadow-ledger oracle (#145) judges the result; the coordinator is "crashed" and resumed by another
/// instance; a failed validation aborts without a switch; cancelling the job aborts too. Small corpora keep CI lean.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class ReindexTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Coding_during_a_reindex_reaches_the_new_index_and_the_switch_loses_no_update()
    {
        await using var r = await ReindexHarness.CreateAsync(openSearch, postgres);
        var w = await r.IndexedWorkspaceAsync(documents: 40);
        var alias = (await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct)).Read;

        // The ledger follows the stable alias across the switch: no document's indexed version may ever go down.
        await using var oracle = await LedgerOracle.StartAsync(r.H.Db.Core.DataSource, r.H.Http, new ShadowLedgerOptions
        {
            WorkspaceId = w.Id,
            Index = new LedgerIndexTarget(alias.Index, alias.Routing),
            Since = DateTimeOffset.UnixEpoch,
            Seed = 73,
            SampleInterval = TimeSpan.FromMilliseconds(50),
        }, Ct);
        oracle.StartSampling();

        var job = await r.StartAsync(w.Id);
        using var stop = new CancellationTokenSource();
        var edits = 0;
        var coding = Task.Run(async () =>
        {
            var random = new Random(73);
            while (!stop.IsCancellationRequested)
            {
                await InteractiveIndexWorkerTests.CodeAsync(r.H, w, w.Documents[random.Next(w.Documents.Count)], random.Next(2) == 0);
                Interlocked.Increment(ref edits);
                await Task.Delay(10, Ct);
            }
        }, Ct);

        // Keep editing until the switch is done and the old index is retired, then let the edits drain.
        var run = await r.DriveAsync(w.Id, job, run => run.Phase == ReindexPhase.Completed, minimumEdits: () => edits >= 40);
        await stop.CancelAsync();
        await coding;
        await r.DrainAsync(w.Id);

        run.Validation!.Passed.Should().BeTrue(run.Validation.ToString());
        run.Validation.FullCheck.Should().BeTrue();
        run.Validation.DocumentsCompared.Should().Be(40);
        run.Target.Should().Be(new IndexLocation(IndexPlacementKind.Dedicated, null, 2, 1), "a same-generation rebuild gets a new revision");
        run.Source.Should().Be(new IndexLocation(IndexPlacementKind.Dedicated, null, 2, 0));
        edits.Should().BeGreaterThanOrEqualTo(40);

        var placement = await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct);
        placement.State.Should().Be(IndexPlacementState.Active);
        placement.Location.Should().Be(run.Target);
        placement.Read.Index.Should().Be(alias.Index, "reads keep using the stable alias");
        (await r.AliasTargetsAsync(alias.Index)).Should().Equal(placement.WriteTargets.Single().Index);
        (await r.IndexExistsAsync(alias.Index + "-g2")).Should().BeFalse("the retired generation is deleted after its retention");

        var finished = await r.H.Db.Jobs.GetAsync(w.Id, job, Ct);
        finished!.Status.Should().Be(JobStatus.Completed);
        finished.Counters.ChunksCommitted.Should().Be(finished.Counters.ChunksTotal).And.BeGreaterThan(1);
        finished.Counters.IndexTasksApplied.Should().Be(finished.Counters.IndexTasksTotal);
        finished.StatusReason.Should().Contain("deleted");

        var verdict = await oracle.ReconcileAsync(Ct);
        verdict.Passed.Should().BeTrue(verdict.ToString());
        verdict.ShadowLedger.StaleOverwrites.Should().Be(0);
        verdict.ShadowLedger.MissingDocs.Should().Be(0);
        await InteractiveIndexWorkerTests.AssertIndexMatchesPostgresAsync(r.H, w.Id, w.Documents);
    }

    [Fact]
    public async Task A_legal_hold_keeps_the_retired_index_until_the_hold_is_released()
    {
        await using var r = await ReindexHarness.CreateAsync(openSearch, postgres, o => o.PreservationRecheckInterval = TimeSpan.FromMilliseconds(200));
        var w = await r.IndexedWorkspaceAsync(documents: 12);
        var alias = (await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct)).Read;
        await Workspaces.PreservationLockTestSql.PlaceAsync(r.H.Db.Core, w.Id);

        var job = await r.StartAsync(w.Id);
        await r.DriveAsync(w.Id, job, run => run.Phase == ReindexPhase.Retaining);
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(250, Ct);
            await r.Coordinator("coordinator").StepAsync(w.Id, job, Ct);
        }

        (await r.Store.GetAsync(w.Id, job, Ct))!.Phase.Should().Be(ReindexPhase.Retaining, "the retired index waits for the hold");
        (await r.IndexExistsAsync(alias.Index + "-g2")).Should().BeTrue();

        await Workspaces.PreservationLockTestSql.ReleaseAllAsync(r.H.Db.Core, w.Id);
        await r.DriveAsync(w.Id, job, run => run.Phase == ReindexPhase.Completed);
        (await r.IndexExistsAsync(alias.Index + "-g2")).Should().BeFalse("released, the retired generation is deleted");
    }

    [Fact]
    public async Task A_crashed_coordinator_is_resumed_by_another_and_search_results_are_identical()
    {
        await using var r = await ReindexHarness.CreateAsync(openSearch, postgres);
        var w = await r.IndexedWorkspaceAsync(documents: 40);
        var before = await r.ScanAsync(w.Id);
        before.Should().HaveCount(40);

        var job = await r.StartAsync(w.Id);

        // Crash right after the target was created and dual writes started, before the run recorded it: the next
        // coordinator adopts the rebuild instead of starting a second one.
        await r.H.Indexes.BeginRebuildAsync(w.Id, new IndexRebuildRequest(), Ct);
        var first = r.Coordinator("coordinator-a");
        await first.StepAsync(w.Id, job, Ct);
        (await r.Store.GetAsync(w.Id, job, Ct))!.Phase.Should().Be(ReindexPhase.Building);
        await Task.Delay(r.Options.WriterSettleDelay, Ct);
        await first.StepAsync(w.Id, job, Ct);
        var backfilling = await r.Store.GetAsync(w.Id, job, Ct);
        backfilling!.Phase.Should().Be(ReindexPhase.Backfilling);
        (await r.Store.GetTaskProgressAsync(w.Id, job, Ct)).Total.Should().Be(r.Options.TaskWindow, "the task window bounds the backfill");

        // coordinator-a dies here. Its lease keeps others out until it expires; then coordinator-b finishes the run.
        var second = r.Coordinator("coordinator-b");
        (await second.StepAsync(w.Id, job, Ct)).Should().BeFalse();
        (await r.Store.GetAsync(w.Id, job, Ct))!.Phase.Should().Be(ReindexPhase.Backfilling);
        await Task.Delay(r.Options.LeaseDuration + TimeSpan.FromMilliseconds(500), Ct);
        var run = await r.DriveAsync(w.Id, job, run => run.Phase == ReindexPhase.Completed, coordinator: second);

        run.Validation!.Passed.Should().BeTrue(run.Validation.ToString());
        run.DocumentsPlanned.Should().Be(40);
        (await r.ScanAsync(w.Id)).Should().BeEquivalentTo(before, "the rebuild from PostgreSQL reproduces every document exactly");
        (await r.H.Db.Jobs.GetAsync(w.Id, job, Ct))!.Status.Should().Be(JobStatus.Completed);
        (await r.H.Db.Jobs.GetChunksAsync(w.Id, job, cancellationToken: Ct)).Should().OnlyContain(c => c.Status == JobChunkStatus.Committed);
    }

    [Fact]
    public async Task A_failed_validation_aborts_without_a_switch_and_the_old_alias_keeps_serving()
    {
        await using var r = await ReindexHarness.CreateAsync(openSearch, postgres, o => o.TaskWindow = 20);
        var w = await r.IndexedWorkspaceAsync(documents: 20);
        var current = await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct);
        var job = await r.StartAsync(w.Id);
        var coordinator = r.Coordinator("coordinator");
        await coordinator.StepAsync(w.Id, job, Ct);
        await Task.Delay(r.Options.WriterSettleDelay, Ct);
        await coordinator.StepAsync(w.Id, job, Ct);
        await r.DeliverTasksAsync(w.Id, job);

        // Damage the new index behind the job's back: one document disappears from it.
        var target = (await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct)).RebuildTarget!;
        using (var deleted = await r.H.Http.DeleteAsync($"{target.Index}/_doc/{w.Documents[7]:D}?refresh=true", Ct))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await coordinator.StepAsync(w.Id, job, Ct);

        var run = await r.Store.GetAsync(w.Id, job, Ct);
        run!.Phase.Should().Be(ReindexPhase.Aborting);
        run.Validation!.Passed.Should().BeFalse();
        run.Validation.MissingDocuments.Should().Be(1);
        run.Validation.Examples.Should().Equal(w.Documents[7]);
        var failed = await r.H.Db.Jobs.GetAsync(w.Id, job, Ct);
        failed!.Status.Should().Be(JobStatus.Failed);
        failed.StatusReason.Should().Contain("missing");

        var placement = await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct);
        placement.Should().BeEquivalentTo(current, "the current placement keeps serving");
        (await r.AliasTargetsAsync(current.Read.Index)).Should().Equal(current.WriteTargets.Single().Index);
        (await r.ScanAsync(w.Id)).Should().HaveCount(20);

        await Task.Delay(r.Options.WriterSettleDelay, Ct);
        await coordinator.StepAsync(w.Id, job, Ct);
        (await r.Store.GetAsync(w.Id, job, Ct))!.Phase.Should().Be(ReindexPhase.Aborted);
        (await r.IndexExistsAsync(target.Index)).Should().BeFalse();
        (await r.Store.ListActiveAsync(Ct)).Should().NotContain((w.Id, job));
    }

    [Fact]
    public async Task Cancelling_the_job_aborts_the_rebuild_and_a_second_reindex_conflicts_while_one_runs()
    {
        await using var r = await ReindexHarness.CreateAsync(openSearch, postgres);
        var w = await r.IndexedWorkspaceAsync(documents: 5);
        var job = await r.StartAsync(w.Id);
        var second = await r.Service.StartAsync(w.Id, new ReindexRequest(), ReindexInitiator.User(SearchWorkDatabase.Reviewer, null), "second", Ct);
        second.Status.Should().Be(ReindexStartStatus.Conflict);
        second.Run!.JobId.Should().Be(job);

        var coordinator = r.Coordinator("coordinator");
        await coordinator.StepAsync(w.Id, job, Ct);
        var building = await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct);
        building.State.Should().Be(IndexPlacementState.Building);

        await r.H.Db.Jobs.CancelAsync(w.Id, job, SearchWorkDatabase.Reviewer, cancellationToken: Ct);
        await coordinator.StepAsync(w.Id, job, Ct);

        (await r.Store.GetAsync(w.Id, job, Ct))!.Phase.Should().Be(ReindexPhase.Aborting);
        (await r.H.Indexes.ResolveAsync(w.Id, IndexPurpose.Read, Ct)).State.Should().Be(IndexPlacementState.Active);
        (await r.H.Db.Jobs.GetAsync(w.Id, job, Ct))!.Status.Should().Be(JobStatus.Cancelled);
        (await r.IndexExistsAsync(building.RebuildTarget!.Index)).Should().BeFalse();
    }

    /// <summary>The index worker harness plus the chunk index worker, the reindex store and coordinators with short delays.</summary>
    private sealed class ReindexHarness : IAsyncDisposable
    {
        private ReindexHarness(IndexWorkerHarness h, ReindexOptions options)
        {
            H = h;
            Options = options;
        }

        public IndexWorkerHarness H { get; }

        public ReindexOptions Options { get; }

        public IReindexStore Store => H.Services.GetRequiredService<IReindexStore>();

        public ReindexService Service => new(Store, H.Db.Jobs, H.Services.GetRequiredService<IIndexManager>() as IWorkspaceSearchPlacement);

        public static async Task<ReindexHarness> CreateAsync(
            OpenSearchFixture openSearch, MigrationPostgresFixture postgres, Action<ReindexOptions>? configure = null)
        {
            var options = new ReindexOptions
            {
                WriterSettleDelay = TimeSpan.FromMilliseconds(200),
                LeaseDuration = TimeSpan.FromSeconds(5),
                TaskWindow = 2,
                DocumentsPerTask = 10,
                Retention = TimeSpan.Zero,
                RecheckTimeout = TimeSpan.FromSeconds(30),
                ValidationPageSize = 25,
            };
            configure?.Invoke(options);
            var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres, configure: s =>
            {
                s.AddSingleton<IAuditEventWriter>(new InMemoryAuditEventWriter());
                s.AddChunkIndexWorker(new ChunkIndexWorkerOptions
                {
                    WorkerId = "reindex-test",
                    RetryBaseDelay = TimeSpan.FromMilliseconds(50),
                    RetryMaxDelay = TimeSpan.FromMilliseconds(200),
                    ThrottleDelay = TimeSpan.FromMilliseconds(100),
                });
                s.AddPostgresReindexStore();
                s.AddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<NpgsqlDataSource>()));
                s.AddPostgresJobChunkStore();
                s.AddPostgresSearchWatermarkStore();
                s.AddPostgresPreservationLocks();
                s.AddReindexCoordinatorCore(options);
            });
            return new ReindexHarness(h, options);
        }

        /// <summary>A dedicated workspace whose documents are coded once and indexed.</summary>
        public async Task<TestWorkspace> IndexedWorkspaceAsync(int documents)
        {
            var w = await H.Db.WorkspaceAsync(documents);
            await H.Indexes.PlaceAsync(w.Id, new WorkspacePlacementRequest(DedicatedIndex: true), Ct);
            await H.Db.Core.Coding.ApplyAsync(new CodingWriteRequest
            {
                WorkspaceId = w.Id,
                IdempotencyKey = "load-" + Guid.CreateVersion7().ToString("N"),
                Actor = new CodingActor(SearchWorkDatabase.Reviewer, CodingActorType.Human),
                Documents = [.. w.Documents.Select(d => new CodingTarget(d))],
                Operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))],
            }, Ct);
            await DrainAsync(w.Id);
            return w;
        }

        public async Task<Guid> StartAsync(Guid workspaceId)
        {
            var outcome = await Service.StartAsync(workspaceId, new ReindexRequest(), ReindexInitiator.User(SearchWorkDatabase.Reviewer, null),
                "reindex-" + Guid.NewGuid().ToString("N"), Ct);
            outcome.Status.Should().Be(ReindexStartStatus.Ok);
            outcome.Job!.Status.Should().Be(JobStatus.Preparing);
            return outcome.Job.JobId;
        }

        /// <summary>A coordinator with its own identity, as another worker process would have.</summary>
        internal ReindexCoordinator Coordinator(string owner) => new(
            Store,
            H.Services.GetRequiredService<IJobRepository>(),
            H.Services.GetRequiredService<IJobChunkRepository>(),
            H.Indexes,
            H.Projections,
            H.Services.GetRequiredService<ReindexValidator>(),
            H.Services.GetRequiredService<IPreservationLockGuard>(),
            Options,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReindexCoordinator>.Instance)
        {
            Owner = owner,
        };

        /// <summary>
        /// Runs the pipeline until <paramref name="done"/>: in the background, as the index worker hosts would, interactive
        /// outbox rows are dispatched and indexed and the job's reindex tasks delivered to the chunk index worker; in the
        /// foreground the coordinator is stepped (its validation waits for those workers to catch up).
        /// </summary>
        public async Task<ReindexRun> DriveAsync(
            Guid workspaceId, Guid jobId, Func<ReindexRun, bool> done, Func<bool>? minimumEdits = null, ReindexCoordinator? coordinator = null)
        {
            coordinator ??= Coordinator("coordinator");
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var workers = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await PumpOutboxAsync(workspaceId);
                    await DeliverTasksAsync(workspaceId, jobId);
                    await Task.Delay(20, CancellationToken.None);
                }
            }, CancellationToken.None);
            try
            {
                while (true)
                {
                    if (minimumEdits is null || minimumEdits())
                    {
                        await coordinator.StepAsync(workspaceId, jobId, Ct);
                    }

                    var run = await Store.GetAsync(workspaceId, jobId, Ct);
                    if (done(run!))
                    {
                        return run!;
                    }

                    DateTime.UtcNow.Should().BeBefore(deadline, $"the reindex is stuck in {run!.Phase}: {run.Error}");
                    await Task.Delay(50, Ct);
                }
            }
            finally
            {
                await stop.CancelAsync();
                await workers;
            }
        }

        public async Task DeliverTasksAsync(Guid workspaceId, Guid jobId)
        {
            foreach (var task in (await H.Db.Tasks.GetByJobAsync(workspaceId, jobId, Ct)).Where(t => t.Status != IndexChunkTaskStatus.Applied))
            {
                await using var scope = H.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ChunkIndexTaskConsumer>()
                    .HandleAsync(new() { TaskId = task.TaskId }, ChunkIndexHarness.Received(task), Ct);
            }
        }

        public async Task PumpOutboxAsync(Guid workspaceId)
        {
            await Parallel.ForEachAsync(await H.DispatchAsync(workspaceId), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct },
                async (message, _) => await H.HandleAsync(message));
        }

        /// <summary>Until every interactive outbox row is applied.</summary>
        public async Task DrainAsync(Guid workspaceId)
        {
            for (var i = 0; i < 200; i++)
            {
                await PumpOutboxAsync(workspaceId);
                if ((await H.Db.OutboxStatusesAsync(workspaceId)).Where(s => s.Key != SearchOutboxStatus.Applied).Sum(s => s.Value) == 0)
                {
                    return;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException("The search outbox did not drain.");
        }

        /// <summary>Every document the read alias returns, with its version and source, after a refresh.</summary>
        public async Task<Dictionary<string, string>> ScanAsync(Guid workspaceId)
        {
            var read = (await H.Indexes.ResolveAsync(workspaceId, IndexPurpose.Read, Ct)).Read;
            (await H.Http.PostAsync($"{read.Index}/_refresh", null, Ct)).Dispose();
            var query = new JsonObject
            {
                ["size"] = 1_000,
                ["version"] = true,
                ["sort"] = new JsonArray(new JsonObject { ["controlNumberSort"] = "asc" }),
                ["query"] = new JsonObject { ["term"] = new JsonObject { ["workspaceId"] = workspaceId.ToString("D") } },
            };
            using var response = await H.Http.PostAsync($"{read.Index}/_search",
                new StringContent(query.ToJsonString(), Encoding.UTF8, "application/json"), Ct);
            response.EnsureSuccessStatusCode();
            var hits = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["hits"]!["hits"]!.AsArray();
            return hits.ToDictionary(h => h!["_id"]!.GetValue<string>(), h => $"{h!["_version"]}|{h["_source"]!.ToJsonString()}");
        }

        public async Task<IReadOnlyList<string>> AliasTargetsAsync(string alias)
        {
            using var response = await H.Http.GetAsync($"_alias/{alias}", Ct);
            response.EnsureSuccessStatusCode();
            return [.. JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject().Select(kv => kv.Key)];
        }

        public async Task<bool> IndexExistsAsync(string index)
        {
            using var response = await H.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, index), Ct);
            return response.StatusCode == HttpStatusCode.OK;
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }
}
