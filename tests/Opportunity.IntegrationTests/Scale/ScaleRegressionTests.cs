extern alias bench;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using bench::Opportunity.Benchmarks.Bundles;
using bench::Opportunity.Benchmarks.Capture;
using bench::Opportunity.Benchmarks.Gates;
using bench::Opportunity.Benchmarks.Regression;
using bench::Opportunity.Benchmarks.Workloads;
using Opportunity.Application.Coding;
using Opportunity.Application.Exports;
using Opportunity.Application.Jobs;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Indexing;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Import;
using Opportunity.Contracts.Search;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Correctness.ShadowLedger;
using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Indexing;
using Opportunity.Testing.OpenSearch;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.Scale;

/// <summary>
/// The nightly scale-regression run (E18-T06, test-strategy §3 T2, workflow <c>scale-regression.yml</c>): a seeded
/// synthetic corpus goes import → index → search → bulk tag → export through <see cref="ScaleWorld"/>, measuring stage
/// throughput, search p50/p95/p99 per query bucket idle and during the bulk tag, the coding → searchable probe and the
/// search-index lag, then judging correctness (pipeline counters, an outsider's searches, the shadow-ledger oracle).
/// The result is a schema-valid E17-T03 result bundle plus <c>scale-run.json</c>; <c>opportunity-bench regress</c> compares
/// it with the rolling baseline. Nothing absolute is asserted about latency (Q-44, Q-70) — only correctness, which fails
/// the test. Opt-in: <c>OPPORTUNITY_SCALE_REGRESSION=1</c>; see docs/testing/scale-regression.md for the variables.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class ScaleRegressionTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres, RabbitMqFixture rabbit)
{
    /// <summary>Bump when the workload changes so that runs stop being comparable (a new baseline series starts).</summary>
    public const string ScenarioVersion = "1";

    private static readonly TimeSpan ProbePoll = TimeSpan.FromMilliseconds(50);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "ScaleRegression")]
    public async Task Nightly_scale_regression_run()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OPPORTUNITY_SCALE_REGRESSION") == "1",
            "Opt-in nightly scale-regression run: set OPPORTUNITY_SCALE_REGRESSION=1 (E18-T06, docs/testing/scale-regression.md).");
        var settings = ScaleSettings.FromEnvironment();
        var started = DateTime.UtcNow;
        var root = Directory.CreateTempSubdirectory("opp-scale-").FullName;
        try
        {
            await RunAsync(settings, root, started);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task RunAsync(ScaleSettings settings, string root, DateTime started)
    {
        var output = TestContext.Current.TestOutputHelper;
        var stages = new List<StageResult>();
        var counters = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var cpu = HostCpuSampler.Start();

        // ---- generate ------------------------------------------------------------------------------------------
        var corpusRoot = Path.Combine(root, "share");
        var profile = ProfileSerializer.WithDocumentCount(ScaleSettings.Profile(), settings.Documents);
        var clock = Stopwatch.StartNew();
        var generateStarted = DateTime.UtcNow;
        CorpusRunner.Run(profile, settings.Seed, corpusRoot, new CorpusRunOptions
        {
            Threads = Math.Max(1, Environment.ProcessorCount / 2),
            WriteGroundTruth = false,
            SinkFactories = [context => new VolumeWriter(context, corpusRoot, new VolumeOptions { IncludeNatives = false, IncludeImages = false, IncludeText = true })],
        });
        stages.Add(Stage("generate", generateStarted, clock.Elapsed, settings.Documents));
        var dat = await File.ReadAllBytesAsync(Path.Combine(corpusRoot, "VOL001", "DATA", "VOL001.dat"), Ct);

        await using var world = await ScaleWorld.CreateAsync(openSearch, postgres, rabbit, corpusRoot);
        var ws = await world.Search.WorkspaceAsync();
        var fields = await world.Bulk.WorkspaceAsync(ws);
        var admin = await world.Search.MemberAsync(ws, WorkspaceRole.WorkspaceAdmin);
        var reviewer = await world.Search.MemberAsync(ws, WorkspaceRole.Reviewer);
        var outsider = await world.Search.Db.CreateUserAsync();
        var lag = new LagMonitor(world, ws);

        // ---- import → index -------------------------------------------------------------------------------------
        var importLag = new LagSeries();
        var importStarted = DateTime.UtcNow;
        clock.Restart();
        using (var sampling = lag.Sample(importLag))
        {
            var batch = await world.Import.StartAsync(ws, dat, new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL001" } }, name: "VOL001.dat");
            var job = await WaitAsync("import job", settings.Timeout, async () =>
                await world.Import.Jobs.GetAsync(ws, batch.JobId, Ct) is { Status: JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled } j ? j : null);
            stages.Add(Stage("import", importStarted, clock.Elapsed, settings.Documents));
            var report = await world.Import.BatchAsync(batch);
            counters["import.job-not-completed"] = job.Status == JobStatus.Completed ? 0 : 1;
            counters["import.row-errors"] = report.RowsErrored;
            counters["import.rows-missing"] = Math.Max(0, settings.Documents - report.RowsImported);

            await lag.WaitCaughtUpAsync(settings.Timeout);
            stages.Add(Stage("import-searchable", importStarted, clock.Elapsed, settings.Documents));
        }

        var importEnded = DateTime.UtcNow;
        var all = await CountAsync(world, ws, admin, "");
        counters["index.documents-missing"] = Math.Abs(settings.Documents - all);
        var documents = await DocumentIdsAsync(world, ws);
        output?.WriteLine($"imported and indexed {documents.Count} documents in {(importEnded - importStarted).TotalSeconds:F1} s");

        // ---- search (idle) --------------------------------------------------------------------------------------
        var queries = await ScaleQueries.ResolveAsync(world, ws, profile, dat);
        var idle = new LatencyRecorder();
        var idleStarted = DateTime.UtcNow;
        var searchErrors = new SearchErrors();
        await RunQueriesAsync(world, ws, reviewer, queries, idle, rounds: settings.WarmupRounds, record: false, searchErrors);
        for (var round = 0; round < settings.SearchRounds; round++)
        {
            await RunQueriesAsync(world, ws, reviewer, queries, idle, rounds: 1, record: true, searchErrors);
        }

        var idleProbe = new LatencyRecorder();
        var probeDocuments = documents.Skip(documents.Count / 2).ToList();
        var probeIndex = 0;
        for (var i = 0; i < settings.IdleProbes; i++)
        {
            await ProbeAsync(world, ws, reviewer, fields.Responsive, probeDocuments[probeIndex++ % probeDocuments.Count], idleProbe, settings, counters);
        }

        var idleEnded = DateTime.UtcNow;

        // ---- bulk tag, with searches and the coding → searchable probe running alongside -----------------------------
        var bulkDocuments = documents.Take(documents.Count / 2).ToList();
        var load = new LatencyRecorder();
        var loadProbe = new LatencyRecorder();
        var bulkLag = new LagSeries();
        var bulkStarted = DateTime.UtcNow;
        clock.Restart();
        JobInfo bulk;
        using (var sampling = lag.Sample(bulkLag))
        {
            var snapshot = await world.Bulk.SnapshotAsync(ws, admin, bulkDocuments);
            var submitted = await world.Bulk.StartAsync(ws, admin, snapshot.SnapshotId,
                new CodingChange(fields.Responsive, CodingOperationKind.Set, true),
                new CodingChange(fields.Issues, CodingOperationKind.AddChoices, new JsonArray(fields.IssueA)));
            using var done = new CancellationTokenSource();
            var searching = Task.Run(async () =>
            {
                while (!done.IsCancellationRequested || load.Count("simple") + load.Count("complex") < queries.Count)
                {
                    await RunQueriesAsync(world, ws, reviewer, queries, load, rounds: 1, record: true, searchErrors);
                }
            }, Ct);
            var probing = Task.Run(async () =>
            {
                while (!done.IsCancellationRequested || loadProbe.Count("coding-to-searchable") < settings.MinimumLoadProbes)
                {
                    await ProbeAsync(world, ws, reviewer, fields.Responsive, probeDocuments[probeIndex++ % probeDocuments.Count], loadProbe, settings, counters);
                }
            }, Ct);
            bulk = await WaitAsync("bulk-coding job", settings.Timeout, async () =>
                await world.Bulk.JobAsync(ws, submitted.JobId) is { } j && (j.Status is JobStatus.Failed or JobStatus.Cancelled
                    || (j.Status == JobStatus.Completed && j.Counters.IndexTasksApplied == j.Counters.IndexTasksTotal)) ? j : null);
            await lag.WaitCaughtUpAsync(settings.Timeout);
            stages.Add(Stage("bulk-tag", bulkStarted, clock.Elapsed, bulkDocuments.Count));
            await done.CancelAsync();
            await Task.WhenAll(searching, probing);
        }

        var bulkEnded = DateTime.UtcNow;
        counters["bulk.job-not-completed"] = bulk.Status == JobStatus.Completed ? 0 : 1;
        counters["bulk.items-failed"] = bulk.Counters.ItemsFailed;
        counters["bulk.items-unaccounted"] = Math.Abs(bulkDocuments.Count
            - (bulk.Counters.ItemsApplied + bulk.Counters.ItemsUnchanged + bulk.Counters.ItemsSkippedConcurrentEdit + bulk.Counters.ItemsExcludedNoAccess));
        counters["bulk.searchable-mismatch"] = Math.Abs(bulkDocuments.Count - await CountAsync(world, ws, admin, "issues:\"Supply contract\""));
        counters["search.errors"] = searchErrors.Count;

        // ---- export ---------------------------------------------------------------------------------------------
        var exportStarted = DateTime.UtcNow;
        clock.Restart();
        var exportSnapshot = await FreezeAsync(world, ws, admin, documents);
        var creation = await world.Exports.CreateAsync(ws, admin, new CreateExportRequest(exportSnapshot.SnapshotId,
            [new(FieldId: SystemFields.ControlNumber), new(FieldId: SystemFields.BegAttach), new(FieldId: SystemFields.EndAttach),
             new(Column: ExportColumnResource.FamilyId), new(Column: ExportColumnResource.ParentId)],
            IncludeNatives: false, IncludeImages: false));
        var export = await WaitAsync("export", settings.Timeout, async () =>
            await world.Exports.Exports.GetAsync(ws, creation.Export.ExportId, Ct) is { Status: ExportStatus.Completed or ExportStatus.Failed or ExportStatus.Cancelled } e ? e : null);
        stages.Add(Stage("export", exportStarted, clock.Elapsed, export.Report?.DocumentsExported ?? 0));
        counters["export.not-completed"] = export.Status == ExportStatus.Completed ? 0 : 1;
        counters["export.documents-missing"] = Math.Abs(documents.Count - (export.Report?.DocumentsExported ?? 0));
        var exportEnded = DateTime.UtcNow;

        // ---- correctness oracles --------------------------------------------------------------------------------
        var security = await OutsiderAsync(world, ws, outsider, queries);
        var target = await world.Search.Indexes.ResolveAsync(ws, IndexPurpose.Read, Ct);
        await using var ledger = await LedgerOracle.StartAsync(world.Core.DataSource, world.Search.OpenSearchHttp, new ShadowLedgerOptions
        {
            WorkspaceId = ws,
            Index = new LedgerIndexTarget(target.Read.Index, target.Read.Routing),
            Scope = LedgerScope.Workspace,
            Seed = (long)settings.Seed,
            BatchSize = 2_000,
        }, Ct);
        var verdict = await ledger.ReconcileAsync(Ct);
        output?.WriteLine(verdict.ToString());

        // ---- environment and bundle -----------------------------------------------------------------------------
        var ended = DateTime.UtcNow;
        var environment = await CaptureAsync(world);
        var workloadFile = Path.Combine(root, "queries.json");
        await File.WriteAllTextAsync(workloadFile, ScaleQueries.ToJson(queries), Ct);
        var workload = new Dictionary<string, string> { [workloadFile] = "queries.json" };
        if (ScenarioSource() is { } source && File.Exists(source))
        {
            workload[source] = Path.GetFileName(source);
        }

        var record = new ScaleRunRecord
        {
            ScenarioVersion = ScenarioVersion,
            Documents = settings.Documents,
            Seed = settings.Seed,
            Config = settings.Describe().Concat(world.WorkerSettings).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal),
            Stages = stages,
            Counters = counters,
            Notes = ["In-process worker host and search service on Testcontainers; relative trends only (Q-70)."],
        };
        var scenarios = new List<bench::Opportunity.Benchmarks.Bundles.Scenario>
        {
            Scenario("import", ScenarioRole.Other, importStarted, importEnded, cpu, [], new Throughput { ImportDocsPerSecond = stages.Single(s => s.Name == "import").DocsPerSecond },
                importLag.Summarize("metrics/index-lag-import.csv"), null),
            Scenario("idle", ScenarioRole.IdleBaseline, idleStarted, idleEnded, cpu, idle.Results(), new Throughput(), null,
                Probe(idleProbe)),
            Scenario("bulk", ScenarioRole.BulkLoad, bulkStarted, bulkEnded, cpu, load.Results(), new Throughput { BulkDocsPerSecond = stages.Single(s => s.Name == "bulk-tag").DocsPerSecond },
                bulkLag.Summarize("metrics/index-lag-bulk.csv"), Probe(loadProbe)),
            Scenario("export", ScenarioRole.Other, exportStarted, exportEnded, cpu, [], new Throughput(), null, null),
        };
        var oracles = new Oracles
        {
            ShadowLedger = new ShadowLedgerOracle
            {
                Status = verdict.Passed ? OracleStatus.Passed : OracleStatus.Failed,
                TouchedDocs = verdict.ShadowLedger.TouchedDocs,
                StaleOverwrites = verdict.ShadowLedger.StaleOverwrites,
                VersionRegressions = verdict.ShadowLedger.VersionRegressions,
                MissingDocs = verdict.ShadowLedger.MissingDocs,
                ValueMismatches = verdict.ShadowLedger.ValueMismatches,
                VersionConflictRejections = verdict.ShadowLedger.VersionConflictRejections,
            },
            Security = new SecurityOracle
            {
                Status = security.Unauthorized == 0 ? OracleStatus.Passed : OracleStatus.Failed,
                Checks = security.Checks,
                UnauthorizedRetrievals = security.Unauthorized,
            },
            Idempotency = new IdempotencyOracle { Status = OracleStatus.NotRun },
        };

        var outputDirectory = settings.OutputDirectory ?? Path.Combine(root, "bundle");
        var git = await GitAsync();
        var bundlePath = ScaleRunBundle.Write(new ScaleRunBundleInput
        {
            OutputDirectory = outputDirectory,
            Git = git,
            Operator = Environment.GetEnvironmentVariable("OPPORTUNITY_SCALE_OPERATOR") ?? Environment.UserName,
            StartedUtc = started,
            EndedUtc = ended,
            Environment = environment,
            CorpusManifestPath = Path.Combine(corpusRoot, CorpusRunner.ManifestFile),
            GatesPath = GatesFile.Locate() ?? throw new FileNotFoundException("gates.yaml not found."),
            WorkloadFiles = workload,
            Texts = new Dictionary<string, string>
            {
                ["metrics/index-lag-import.csv"] = importLag.ToCsv(),
                ["metrics/index-lag-bulk.csv"] = bulkLag.ToCsv(),
                ["oracles/shadow-ledger-verdict.json"] = verdict.ToJson() + "\n",
            },
            Scenarios = scenarios,
            Oracles = oracles,
            Record = record,
            Notes = $"E18-T06 nightly scale regression, {settings.Documents} documents, seed {settings.Seed}.",
        });
        output?.WriteLine("result bundle: " + bundlePath);
        foreach (var stage in stages)
        {
            output?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {stage.Name,-18} {stage.Seconds,8:F1} s  {stage.DocsPerSecond,10:F0} docs/s"));
        }

        foreach (var c in idle.Results().Concat(load.Results()))
        {
            output?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {c.Name,-18} n={c.Requests,-5} p50 {c.Latency.P50 / 1000.0,8:F1} ms  p95 {c.Latency.P95 / 1000.0,8:F1} ms"));
        }

        // Correctness fails the test (and so the nightly job) on the first night; performance is judged by `regress`.
        counters.Where(c => c.Value > 0).Should().BeEmpty("every correctness counter must be 0");
        security.Unauthorized.Should().Be(0, "an outsider must never retrieve a document");
        verdict.Passed.Should().BeTrue(verdict.ToString());
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static StageResult Stage(string name, DateTime started, TimeSpan elapsed, long documents) =>
        new() { Name = name, StartedUtc = started, Seconds = Math.Round(elapsed.TotalSeconds, 3), Documents = documents };

    private static bench::Opportunity.Benchmarks.Bundles.Scenario Scenario(
        string id, ScenarioRole role, DateTime started, DateTime ended, HostCpuSampler cpu, IReadOnlyList<QueryClassResult> classes,
        Throughput throughput, IndexLag? lag, CodingToSearchable? probe)
    {
        // The policy's validity limits: the in-process load generator shares the host with the system under test, so a
        // scenario is valid for the §26 gates only while the whole host stays below the CPU limit. The nightly guard
        // compares trends regardless; validity is recorded for honesty.
        var cpuMax = cpu.MaxPercent(started, ended);
        var reasons = new List<string>();
        if (!cpu.Sampled(started, ended))
        {
            reasons.Add("host CPU was not sampled in this window");
        }
        else if (cpuMax >= 70)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"host CPU (load generator in-process) reached {cpuMax:0.#}% (limit < 70%)"));
        }

        return new bench::Opportunity.Benchmarks.Bundles.Scenario
        {
            Id = id,
            Role = role,
            StartedUtc = started,
            EndedUtc = ended,
            WarmupSeconds = 0,
            DrainSeconds = 0,
            Validity = new Validity { Valid = reasons.Count == 0, DroppedIterationsRatio = 0, LoadGeneratorCpuMaxPercent = Math.Round(cpuMax, 2), Reasons = reasons },
            QueryClasses = classes,
            Throughput = throughput,
            IndexLag = lag,
            CodingToSearchable = probe,
            MetricsFiles = lag?.SeriesFile is { } series ? [series] : null,
        };
    }

    private static CodingToSearchable? Probe(LatencyRecorder probe) =>
        probe.Results().SingleOrDefault(r => r.Name == "coding-to-searchable") is { Latency.Count: > 0 } result
            ? new CodingToSearchable { Samples = result.Latency.Count, PollIntervalMs = ProbePoll.TotalMilliseconds, Latency = result.Latency }
            : null;

    /// <summary>
    /// Coding → searchable (test-strategy §5.1): t0 before the save, t1 when the search service first returns the
    /// document with the new value (polled every 50 ms). Alternates the value so every save is a real change.
    /// </summary>
    private static async Task ProbeAsync(
        ScaleWorld world, Guid ws, Guid user, int field, Guid document, LatencyRecorder recorder, ScaleSettings settings, SortedDictionary<string, long> counters)
    {
        var current = await world.Bulk.ValuesAsync(ws, [document], field);
        var value = !(current.GetValueOrDefault(document)?.GetValue<bool>() ?? false);
        var controlNumber = await world.Core.ScalarAsync<string>(
            "SELECT control_number FROM opportunity.document WHERE workspace_id = @ws AND document_id = @id", ("ws", ws), ("id", document));
        var query = $"controlnumber:\"{controlNumber}\" AND responsive:{(value ? "true" : "false")}";
        var clock = Stopwatch.StartNew();
        await world.Bulk.InteractiveAsync(ws, user, document, CodingFieldOperation.Set(field, value));
        while (await CountAsync(world, ws, user, query) == 0)
        {
            if (clock.Elapsed > settings.ProbeTimeout)
            {
                lock (counters)
                {
                    counters["probe.timeouts"] = counters.TryGetValue("probe.timeouts", out var n) ? n + 1 : 1;
                }

                recorder.Record("coding-to-searchable", null, clock.Elapsed, succeeded: false);
                return;
            }

            await Task.Delay(ProbePoll, Ct);
        }

        recorder.Record("coding-to-searchable", null, clock.Elapsed);
    }

    private static async Task RunQueriesAsync(
        ScaleWorld world, Guid ws, Guid user, IReadOnlyList<ScaleQuery> queries, LatencyRecorder recorder, int rounds, bool record, SearchErrors errors)
    {
        for (var round = 0; round < rounds; round++)
        {
            foreach (var query in queries)
            {
                var clock = Stopwatch.StartNew();
                var outcome = await world.Search.InScopeSearchAsync(ws, user, query.Request);
                var elapsed = clock.Elapsed;
                var ok = outcome.Status == SearchStatus.Ok;
                if (!ok)
                {
                    errors.Add($"{query.Bucket} '{query.Request.Query}': {outcome.Status} {string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message))} {string.Join("; ", outcome.RequestErrors.Select(e => e.Key + ": " + string.Join(", ", e.Value)))}");
                }

                if (record)
                {
                    recorder.Record(query.Bucket, query.Gate, elapsed, ok);
                }
            }
        }
    }

    /// <summary>A signed-in user with no role in the workspace runs every query: each must answer like a missing workspace.</summary>
    private static async Task<(long Checks, long Unauthorized)> OutsiderAsync(ScaleWorld world, Guid ws, Guid outsider, IReadOnlyList<ScaleQuery> queries)
    {
        long unauthorized = 0;
        foreach (var query in queries)
        {
            var outcome = await world.Search.InScopeSearchAsync(ws, outsider, query.Request);
            unauthorized += outcome.Page?.Items.Count ?? 0;
        }

        return (queries.Count, unauthorized);
    }

    private static async Task<long> CountAsync(ScaleWorld world, Guid ws, Guid user, string query)
    {
        var outcome = await world.Search.SearchAsync(ws, user, query, pageSize: 1, countExact: true);
        return outcome.Status == SearchStatus.Ok ? outcome.Page!.Total.Value : -1;
    }

    private static async Task<List<Guid>> DocumentIdsAsync(ScaleWorld world, Guid ws) =>
        [.. (await world.Core.ColumnAsync($"SELECT document_id::text FROM opportunity.document WHERE workspace_id = '{ws}' ORDER BY control_number")).Select(Guid.Parse)];

    /// <summary>An Export frozen set of every document (explicit IDs are capped at 10,000 per API request, so staged directly).</summary>
    private static async Task<Application.Snapshots.SnapshotRecord> FreezeAsync(ScaleWorld world, Guid ws, Guid user, List<Guid> ids)
    {
        const string Owner = "scale-regression";
        var snapshots = world.Exports.Snapshots;
        var created = await snapshots.CreateAsync(new Application.Snapshots.NewSnapshot
        {
            WorkspaceId = ws,
            Name = "All documents",
            Purpose = SnapshotPurpose.Export,
            SourceKind = SnapshotSourceKind.DocumentIds,
            RequestedCount = ids.Count,
            CreatedBy = user,
            CreatedByDisplay = "Scale regression",
            ClaimOwner = Owner,
            ClaimLease = TimeSpan.FromMinutes(30),
        }, Ct);
        foreach (var batch in ids.Chunk(10_000))
        {
            await snapshots.StageAsync(ws, created.Snapshot.SnapshotId, batch, SnapshotInclusionReason.Explicit, Ct);
        }

        var frozen = await snapshots.FreezeAsync(
            new Application.Snapshots.SnapshotFreezeRequest { WorkspaceId = ws, SnapshotId = created.Snapshot.SnapshotId, ClaimOwner = Owner },
            (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()), Ct);
        return frozen.Snapshot ?? throw new InvalidOperationException($"Export snapshot not frozen: {frozen.Outcome}.");
    }

    private async Task<EnvironmentManifest> CaptureAsync(ScaleWorld world)
    {
        var management = new UriBuilder(rabbit.ManagementAddress) { UserName = RabbitMqFixture.Username, Password = RabbitMqFixture.Password }.Uri;
        return await new EnvironmentCapturer().CaptureAsync(new CaptureOptions
        {
            Profile = BenchmarkProfile.DeveloperRegression,
            Host = new HostOverrides { Role = "all-in-one", IncludeHostname = false },
            Containers = [postgres.ContainerId, openSearch.ContainerId, rabbit.ContainerId],
            Postgres = [new PostgresTarget("primary", world.Core.ConnectionString)],
            OpenSearch = openSearch.BaseAddress,
            RabbitMqManagement = management,
            ObjectStore = new ObjectStoreInfo { Provider = "FileSystem", Implementation = "runner temp directory" },
            Workers = [new WorkerPool { Type = ScaleWorld.WorkerTypesEnabled, Instances = 1 }],
            RelaxedDurabilityJustification =
                "Nightly developer-regression run (tier T2, Q-05/Q-70) on Testcontainers defaults: relative trends only, never a §26 decision.",
            Notes = ["In-process worker host (Worker.All composition) and search service; Testcontainers PostgreSQL, OpenSearch and RabbitMQ."],
        }, Ct);
    }

    private static async Task<References.GitState> GitAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: > 0 } sha)
        {
            return new References.GitState(sha, false, Environment.GetEnvironmentVariable("GITHUB_REF_NAME"));
        }

        var repository = Path.GetDirectoryName(VersionsEnvFile.Locate() ?? throw new FileNotFoundException("versions.env not found."))!;
        return await References.GitAsync(repository, cancellationToken: Ct);
    }

    private static string? ScenarioSource([CallerFilePath] string path = "") => path.Length == 0 ? null : path;

    private static async Task<T> WaitAsync<T>(string what, TimeSpan timeout, Func<Task<T?>> poll)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await poll() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{what} did not finish within {timeout}.");
            }

            await Task.Delay(200, Ct);
        }
    }

    private sealed class SearchErrors
    {
        private readonly List<string> _errors = [];

        public long Count
        {
            get
            {
                lock (_errors)
                {
                    return _errors.Count;
                }
            }
        }

        public void Add(string error)
        {
            lock (_errors)
            {
                if (_errors.Count < 20)
                {
                    TestContext.Current.TestOutputHelper?.WriteLine("search error: " + error);
                }

                _errors.Add(error);
            }
        }
    }
}

/// <summary>Search-index lag of one workspace (test-strategy §5.1), sampled once a second while a phase runs.</summary>
internal sealed class LagMonitor(ScaleWorld world, Guid workspaceId)
{
    // Committed search work (outbox rows and index chunk tasks) whose generation the visible watermark has not reached.
    private const string Pending =
        """
        WITH w AS (SELECT COALESCE((SELECT indexed_through_generation FROM opportunity.workspace_search_watermark WHERE workspace_id = @ws), 0) AS g)
        SELECT count(*)::bigint, COALESCE(EXTRACT(EPOCH FROM now() - min(committed_at)), 0)::float8 FROM (
            SELECT committed_at FROM opportunity.search_outbox, w WHERE workspace_id = @ws AND search_generation > w.g
            UNION ALL
            SELECT committed_at FROM opportunity.index_chunk_task, w WHERE workspace_id = @ws AND search_generation > w.g) pending
        """;

    public IDisposable Sample(LagSeries series)
    {
        var stop = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(series.ResolutionSeconds));
            do
            {
                var (_, seconds) = await ReadAsync();
                series.Add(DateTime.UtcNow, seconds);
            }
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
        });
        return new Stopper(stop, loop);
    }

    /// <summary>Waits until no committed search work is above the visible watermark.</summary>
    public async Task WaitCaughtUpAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((await ReadAsync()).Pending > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The search watermark did not catch up within {timeout}.");
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    private async Task<(long Pending, double Seconds)> ReadAsync()
    {
        await using var command = world.Core.DataSource.CreateCommand(Pending);
        command.Parameters.AddWithValue("ws", workspaceId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetDouble(1));
    }

    private sealed class Stopper(CancellationTokenSource stop, Task loop) : IDisposable
    {
        public void Dispose()
        {
            stop.Cancel();
            try
            {
                loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            stop.Dispose();
        }
    }
}

/// <summary>The run's knobs (environment variables), recorded in the bundle's configuration.</summary>
internal sealed record ScaleSettings
{
    public long Documents { get; init; } = 2_000;

    public ulong Seed { get; init; } = 20261012;

    public int SearchRounds { get; init; } = 40;

    public int WarmupRounds { get; init; } = 2;

    public int IdleProbes { get; init; } = 20;

    public int MinimumLoadProbes { get; init; } = 20;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public string? OutputDirectory { get; init; }

    public static ScaleSettings FromEnvironment()
    {
        static T Read<T>(string name, T fallback, Func<string, T> parse) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? parse(value) : fallback;
        var defaults = new ScaleSettings();
        return new ScaleSettings
        {
            Documents = Read("OPPORTUNITY_SCALE_DOCS", defaults.Documents, v => long.Parse(v, CultureInfo.InvariantCulture)),
            Seed = Read("OPPORTUNITY_SCALE_SEED", defaults.Seed, v => ulong.Parse(v, CultureInfo.InvariantCulture)),
            SearchRounds = Read("OPPORTUNITY_SCALE_SEARCH_ROUNDS", defaults.SearchRounds, v => int.Parse(v, CultureInfo.InvariantCulture)),
            Timeout = Read("OPPORTUNITY_SCALE_TIMEOUT", defaults.Timeout, v => TimeSpan.Parse(v, CultureInfo.InvariantCulture)),
            OutputDirectory = Environment.GetEnvironmentVariable("OPPORTUNITY_SCALE_OUT") is { Length: > 0 } output ? Path.GetFullPath(output) : null,
        };
    }

    /// <summary>The nightly profile: the default generator profile with extracted text capped for CI-sized runs.</summary>
    public static CorpusProfile Profile() => new()
    {
        Text = new TextProfile { MedianBytes = 2 * 1024, P99Bytes = 16 * 1024, MaxBytes = 64 * 1024 },
    };

    public IEnumerable<KeyValuePair<string, string>> Describe() =>
    [
        new("documents", Documents.ToString(CultureInfo.InvariantCulture)),
        new("seed", Seed.ToString(CultureInfo.InvariantCulture)),
        new("searchRounds", SearchRounds.ToString(CultureInfo.InvariantCulture)),
        new("warmupRounds", WarmupRounds.ToString(CultureInfo.InvariantCulture)),
        new("idleProbes", IdleProbes.ToString(CultureInfo.InvariantCulture)),
        new("minimumLoadProbes", MinimumLoadProbes.ToString(CultureInfo.InvariantCulture)),
        new("probePollMs", "50"),
        new("corpus.text", "median 2 KiB, p99 16 KiB, max 64 KiB"),
        new("corpus.volume", "DAT + extracted text, no natives or images"),
        new("bulkTag", "first half of the documents: Responsive = true, Issues += Supply contract"),
        new("export", "every document, metadata DAT (no natives, text or images)"),
    ];
}

/// <summary>One benchmark search: its taxonomy bucket, gate class and request.</summary>
internal sealed record ScaleQuery(string Bucket, GateClass Gate, SearchRequest Request);

/// <summary>The fixed query mix (taxonomy buckets, docs/benchmarks/query-taxonomy.md) resolved against the imported corpus.</summary>
internal static class ScaleQueries
{
    public static async Task<IReadOnlyList<ScaleQuery>> ResolveAsync(ScaleWorld world, Guid ws, CorpusProfile profile, byte[] dat)
    {
        var catalog = await world.Core.Fields.GetCatalogAsync(ws, cancellationToken: TestContext.Current.CancellationToken);
        var names = FieldQueryNames.Assign(catalog.Fields);
        foreach (var field in catalog.Fields.OrderBy(f => f.FieldId))
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"field {field.FieldId} {field.Name} ({field.Type}) -> {names.GetValueOrDefault(field.FieldId)}");
        }

        var vocabulary = new Vocabulary(profile.Text, profile.Needles.Select(n => n.Term).Concat(profile.ProximityPairs.SelectMany(p => new[] { p.First, p.Second })));
        string Word(int rank) => vocabulary[Math.Min(rank, vocabulary.Count - 1)];
        var needle = profile.Needles[0].Term;
        var pair = profile.ProximityPairs[0];
        var custodians = Custodians(dat);
        var custodian = custodians.Count > 0 ? custodians[0] : "none";
        var other = custodians.Count > 1 ? custodians[1] : custodian;
        static SearchRequest R(string query, IReadOnlyList<SearchSortKey>? sort = null, IReadOnlyList<string>? facets = null, SearchExpand? expand = null) =>
            new(query, Sort: sort, PageSize: QueryTaxonomy.PageSize, Highlight: QueryTaxonomy.Highlight, Facets: facets, Expand: expand);
        return
        [
            new("term", GateClass.Simple, R(Word(80))),
            new("term", GateClass.Simple, R($"{Word(300)} {Word(1_200)}")),
            new("phrase", GateClass.Simple, R($"\"{Word(90)} {Word(95)}\"")),
            new("field-filter", GateClass.Simple, R($"{Word(150)} AND custodian:\"{custodian}\"")),
            new("date-range", GateClass.Simple, R($"{Word(200)} AND date:[2018-01-01 TO 2019-12-31]")),
            new("boolean", GateClass.Complex, R($"({Word(100)} OR {Word(400)}) AND NOT {Word(2_000)} AND (custodian:\"{custodian}\" OR custodian:\"{other}\")")),
            new("content-coding", GateClass.Complex, R($"{Word(120)} AND responsive:true")),
            new("expansion", GateClass.Complex, R(needle, expand: new SearchExpand(Family: true))),
            new("grid-coding", GateClass.Complex, R(Word(110), sort: [new SearchSortKey("date", SearchSortDirection.Desc)], facets: ["fileType"])),
            new("proximity", GateClass.Complex, R($"{pair.First} W/{pair.NearDistance} {pair.Second}")),
            new("wildcard", GateClass.Complex, R(Word(250)[..Math.Min(4, Word(250).Length)] + "*")),
        ];
    }

    /// <summary>The two most frequent custodians of the load file (Concordance: þ qualifier, DC4 separator).</summary>
    private static List<string> Custodians(byte[] dat)
    {
        var lines = System.Text.Encoding.UTF8.GetString(dat).TrimStart('\uFEFF').Split("\r\n");
        static string[] Cells(string line) => [.. line.Split(ImportHarness.Dc4).Select(c => c.Trim('þ'))];
        var column = Array.IndexOf(Cells(lines[0]), "Custodian");
        return column < 0
            ? []
            : [.. lines.Skip(1).Select(Cells).Where(c => c.Length > column && c[column].Length > 0)
                .GroupBy(c => c[column], StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(2).Select(g => g.Key)];
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ToJson(IReadOnlyList<ScaleQuery> queries) =>
        JsonSerializer.Serialize(queries.Select(q => new { bucket = q.Bucket, gate = q.Gate.Name(), request = q.Request }), Json) + "\n";
}
