using HdrHistogram;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Gates;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.Benchmarks.Tests;

/// <summary>
/// Builds a complete, schema-valid run directory the way the harness will: a real (tiny) generated corpus manifest,
/// the repository gates.yaml, a workload script, HDR histograms and a developer-profile environment.
/// </summary>
internal static class SampleBundle
{
    public const ulong Seed = 20261003;
    public static readonly DateTime Start = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    public static string RepositoryGates => GatesFile.Locate() ?? throw new FileNotFoundException("gates.yaml");

    private static readonly Lazy<string> CorpusDirectory = new(() =>
    {
        string dir = NewDirectory();
        CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), 300), Seed, dir, new CorpusRunOptions { WriteGroundTruth = false });
        return dir;
    });

    public static string CorpusManifest => Path.Combine(CorpusDirectory.Value, CorpusRunner.ManifestFile);

    /// <summary>Writes a valid bundle into a new temp directory; <paramref name="customize"/> may alter it before writing.</summary>
    public static string Write(Func<ResultBundle, ResultBundle>? customize = null, BundleValidationOptions? options = null)
    {
        string dir = NewDirectory();
        var writer = new BundleWriter(dir);
        ResultBundle bundle = Build(writer);
        writer.Write(customize is null ? bundle : customize(bundle), options);
        return dir;
    }

    /// <summary>Copies inputs into <paramref name="writer"/> and returns the bundle (not yet written).</summary>
    public static ResultBundle Build(BundleWriter writer)
    {
        writer.AddFile(CorpusManifest, References.CorpusManifestFile, description: "corpus manifest (E17-T01)");
        GatesFile gates = GatesFile.Load(RepositoryGates);
        writer.AddFile(gates.Path, GatesFile.FileName, description: "gates the run was evaluated against");
        BundleFile script = writer.AddText("workload/search-mix.js", "// k6 placeholder workload for tests\nexport default function () {}\n");
        BundleFile lagSeries = writer.AddText("metrics/index-lag.jsonl", "{\"t\":0,\"lagSeconds\":0.4}\n{\"t\":1,\"lagSeconds\":0.9}\n");

        WorkloadScript[] scripts = [new(script.Path, script.Sha256)];
        return new ResultBundle
        {
            RunId = References.RunId(Start, "adr004/degradation", "candidate-a", 1),
            Run = new RunInfo
            {
                GitSha = new string('a', 40),
                GitDirty = false,
                GitRef = "main",
                StartedUtc = Start,
                EndedUtc = Start.AddMinutes(30),
                Operator = "ci:test",
                Tier = "T2",
                Suite = "adr004/degradation",
                Candidate = "candidate-a",
                Repetition = new Repetition(1, 3),
                CacheState = CacheState.Warm,
            },
            Environment = Environment(),
            Corpus = References.Corpus(CorpusManifest, CorpusLoadPath.FastPath),
            Workload = new WorkloadReference
            {
                Name = "search-mix",
                Version = "0.1.0",
                TaxonomyVersion = "1",
                QuerySeed = 42,
                Model = WorkloadModel.Open,
                Scripts = scripts,
                WorkloadSha256 = References.WorkloadSha256(scripts),
                Mix = new Dictionary<string, double> { ["simple"] = 0.6, ["boolean"] = 0.3, ["proximity-wildcard"] = 0.1 },
                OfferedRate = new OfferedRate { QueriesPerSecond = 50, BulkDocsPerSecond = 2000 },
            },
            Gates = gates.ToReference(),
            Scenarios =
            [
                Scenario("idle", ScenarioRole.IdleBaseline, 0, simpleMedianUs: 40_000, complexMedianUs: 120_000, lag: null),
                Scenario("bulk", ScenarioRole.BulkLoad, 10, simpleMedianUs: 46_000, complexMedianUs: 150_000, lag: lagSeries.Path),
                Scenario("ceiling", ScenarioRole.BulkCeiling, 20, simpleMedianUs: 0, complexMedianUs: 0, lag: null) with
                {
                    QueryClasses = [],
                    Throughput = new Throughput { BulkDocsPerSecond = 8500 },
                },
            ],
            Oracles = new Oracles
            {
                ShadowLedger = new ShadowLedgerOracle { Status = OracleStatus.Passed, TouchedDocs = 50_000, StaleOverwrites = 0, VersionRegressions = 0, MissingDocs = 0, ValueMismatches = 0, VersionConflictRejections = 12 },
                Security = new SecurityOracle { Status = OracleStatus.Passed, Checks = 4_000, UnauthorizedRetrievals = 0 },
                Idempotency = new IdempotencyOracle { Status = OracleStatus.Passed, Trials = 40, IdempotentTrials = 40, IdempotentRatio = 1.0 },
            },
        };
    }

    public static EnvironmentManifest Environment(BenchmarkProfile profile = BenchmarkProfile.DeveloperRegression)
    {
        VersionsEnvFile versions = VersionsEnvFile.Load(VersionsEnvFile.Locate()!);
        PinnedImage pg = versions.PinnedImages().Single(p => p.Key == "POSTGRES");
        PinnedImage os = versions.PinnedImages().Single(p => p.Key == "OPENSEARCH");
        return new EnvironmentManifest
        {
            CapturedUtc = Start.AddMinutes(-1),
            Profile = profile,
            Hosts =
            [
                new HostInfo
                {
                    Role = "all-in-one",
                    Cpu = new CpuInfo { Model = "Example CPU", Architecture = "x86_64", LogicalCores = 16, PhysicalCores = 8, Sockets = 1 },
                    Memory = new MemoryInfo { TotalBytes = 64L << 30 },
                    Disks = [new DiskInfo { Name = "nvme0n1", Type = DiskType.Nvme, Rotational = false, SizeBytes = 1L << 40, Backs = ["/var/lib/docker"] }],
                    Os = new OsInfo { Kernel = "6.8.0" },
                    ContainerRuntime = new ContainerRuntimeInfo { Name = "docker", Version = "29.0.0" },
                    Platform = new PlatformInfo { Kind = PlatformKind.BareMetal },
                },
            ],
            Software = new SoftwareInfo { VersionsEnv = versions.ToInfo(VersionsEnvFile.FileName), Images = versions.PinnedImages() },
            Containers =
            [
                new ContainerInfo
                {
                    Name = "opportunity-dev-postgres-1",
                    Service = "postgres",
                    ComposeProject = "opportunity-dev",
                    Image = new ContainerImage { Reference = pg.Reference, Id = "sha256:" + new string('1', 64) },
                    Pin = new ImagePin("POSTGRES", pg.Digest!, true),
                },
                new ContainerInfo
                {
                    Name = "opportunity-dev-opensearch-1",
                    Service = "opensearch",
                    ComposeProject = "opportunity-dev",
                    Image = new ContainerImage { Reference = os.Reference, Id = "sha256:" + new string('2', 64) },
                    Pin = new ImagePin("OPENSEARCH", os.Digest!, true),
                    Environment = new Dictionary<string, string> { ["OPENSEARCH_JAVA_OPTS"] = "-Xms1g -Xmx1g" },
                },
            ],
            Postgres =
            [
                new PostgresInstance
                {
                    Name = "primary",
                    ServerVersion = "17.6",
                    Role = PostgresRole.Primary,
                    Settings = new Dictionary<string, PgSetting> { ["fsync"] = new() { Value = "on" }, ["shared_buffers"] = new() { Value = "16384", Unit = "8kB" } },
                    Durability = new PostgresDurability { Fsync = "on", SynchronousCommit = "on", FullPageWrites = "on", WalLevel = "replica", WalSyncMethod = "fdatasync", SynchronousStandbyNames = string.Empty },
                },
            ],
            Opensearch = new OpenSearchCluster
            {
                ClusterName = "opportunity-dev",
                Version = "3.2.0",
                Nodes = [new OpenSearchNode { Name = "node-1", Roles = ["data", "ingest", "cluster_manager"], Jvm = new JvmInfo { Version = "24", HeapMaxBytes = 1L << 30, InputArguments = ["-Xms1g", "-Xmx1g"] } }],
                ClusterSettings = new OpenSearchClusterSettings { Persistent = new Dictionary<string, string>(), Transient = new Dictionary<string, string>() },
                Indices = [new OpenSearchIndex { Name = "docs-shared-000001", PrimaryShards = 1, Replicas = 0, RefreshInterval = "1s", TranslogDurability = "request" }],
            },
            Rabbitmq = new RabbitMqInfo { Version = "4.1.4", Queues = [new RabbitMqQueueInfo("index.chunks", "/", "quorum", true)] },
            ObjectStore = new ObjectStoreInfo { Provider = "FileSystem", Implementation = "filesystem (named volume)" },
            Workers = [new WorkerPool { Type = "all", Instances = 1 }],
            Durability = new DurabilityInfo { Mode = DurabilityMode.Production, Deviations = [] },
        };
    }

    public static LatencySummary Histogram(long medianUs, int samples = 2_000, int seed = 1)
    {
        LongHistogram histogram = Latency.NewHistogram();
        var random = new Random(seed);
        for (int i = 0; i < samples; i++)
        {
            // Log-normal-ish spread around the median.
            double factor = Math.Exp(random.NextDouble() * 1.2 - 0.6);
            histogram.RecordValue(Math.Max(1, (long)(medianUs * factor)));
        }

        return Latency.Summarize(histogram);
    }

    public static string NewDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opportunity-bench-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Scenario Scenario(string id, ScenarioRole role, int offsetMinutes, long simpleMedianUs, long complexMedianUs, string? lag) => new()
    {
        Id = id,
        Role = role,
        StartedUtc = Start.AddMinutes(offsetMinutes),
        EndedUtc = Start.AddMinutes(offsetMinutes + 9),
        WarmupSeconds = 60,
        DrainSeconds = 30,
        Validity = new Validity { Valid = true, DroppedIterationsRatio = 0.001, LoadGeneratorCpuMaxPercent = 41 },
        QueryClasses =
        [
            new QueryClassResult { Name = "simple", Gated = true, Requests = 2_000, Errors = 0, Latency = Histogram(simpleMedianUs, seed: offsetMinutes + 1) },
            new QueryClassResult { Name = "complex", Gated = true, Requests = 2_000, Errors = 0, Latency = Histogram(complexMedianUs, seed: offsetMinutes + 2) },
        ],
        Throughput = new Throughput { QueriesPerSecond = 50, BulkDocsPerSecond = role == ScenarioRole.BulkLoad ? 2_000 : null },
        OfferedBulkDocsPerSecond = role == ScenarioRole.BulkLoad ? 2_000 : null,
        IndexLag = lag is null ? null : new IndexLag { ResolutionSeconds = 1, Samples = 540, MaxSeconds = 4.2, P95Seconds = 1.9, SeriesFile = lag },
        CodingToSearchable = role == ScenarioRole.BulkLoad
            ? new CodingToSearchable { Samples = 500, PollIntervalMs = 50, ProbeTrafficRatio = 0.004, Latency = Histogram(450_000, samples: 500, seed: 99) }
            : null,
    };
}
