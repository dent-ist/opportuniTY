using Opportunity.Benchmarks.Capture;

namespace Opportunity.Benchmarks.Bundles;

// Mirrors schema/result-bundle.v1.schema.json.

public enum CacheState
{
    Cold,
    Warm,
}

public enum CorpusLoadPath
{
    Import,
    FastPath,
    CacheRestore,
}

public enum WorkloadModel
{
    Open,
    Closed,
    Mixed,
}

public enum GatesStatus
{
    Draft,
    Frozen,
}

public enum ScenarioRole
{
    IdleBaseline,
    BulkLoad,
    BulkCeiling,
    FaultWindow,
    Calibration,
    Smoke,
    Other,
}

public enum OracleStatus
{
    Passed,
    Failed,
    NotRun,
}

public sealed record ResultBundle
{
    public const string FileName = "bundle.json";

    public string SchemaVersion { get; init; } = HarnessInfo.SchemaVersion;

    public required string RunId { get; init; }

    public required RunInfo Run { get; init; }

    public required EnvironmentManifest Environment { get; init; }

    public required CorpusReference Corpus { get; init; }

    public required WorkloadReference Workload { get; init; }

    public required GatesReference Gates { get; init; }

    public required IReadOnlyList<Scenario> Scenarios { get; init; }

    public required Oracles Oracles { get; init; }

    public IReadOnlyList<BundleFile> Files { get; init; } = [];
}

public sealed record RunInfo
{
    public required string GitSha { get; init; }

    public required bool GitDirty { get; init; }

    public string? GitRef { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime EndedUtc { get; init; }

    public required string Operator { get; init; }

    /// <summary>test-strategy §3 tier: T1, T2, T3 or T4.</summary>
    public required string Tier { get; init; }

    public required string Suite { get; init; }

    public string? Candidate { get; init; }

    public required Repetition Repetition { get; init; }

    public required CacheState CacheState { get; init; }

    public ToolIdentity Tool { get; init; } = new(HarnessInfo.Name, HarnessInfo.Version);

    public string? Notes { get; init; }
}

public sealed record Repetition(int Index, int Of);

public sealed record GeneratorIdentity(string Name, string Version);

public sealed record CorpusReference
{
    public string? ManifestFile { get; init; }

    public required string ManifestSha256 { get; init; }

    public required GeneratorIdentity Generator { get; init; }

    public required ulong Seed { get; init; }

    public required string ProfileName { get; init; }

    public required string ProfileHash { get; init; }

    public required long DocumentCount { get; init; }

    public required CorpusLoadPath LoadPath { get; init; }

    public string? CacheKey { get; init; }

    public long? TextCapChars { get; init; }
}

public sealed record WorkloadScript(string Path, string Sha256);

public sealed record WorkloadReference
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string TaxonomyVersion { get; init; }

    public required ulong QuerySeed { get; init; }

    public required WorkloadModel Model { get; init; }

    public required IReadOnlyList<WorkloadScript> Scripts { get; init; }

    public required string WorkloadSha256 { get; init; }

    public IReadOnlyDictionary<string, double>? Mix { get; init; }

    public OfferedRate? OfferedRate { get; init; }

    public Reviewers? Reviewers { get; init; }
}

public sealed record OfferedRate
{
    public double? QueriesPerSecond { get; init; }

    public double? BulkDocsPerSecond { get; init; }
}

public sealed record Reviewers
{
    public required int Count { get; init; }

    public double? ThinkTimeMedianSeconds { get; init; }

    public double? ThinkTimeP90Seconds { get; init; }
}

public sealed record GatesReference
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required string GatesVersion { get; init; }

    public required GatesStatus Status { get; init; }
}

public sealed record HdrPayload
{
    public const string V2CompressedBase64 = "hdrhistogram-v2-compressed-base64";

    public string Encoding { get; init; } = V2CompressedBase64;

    public required long LowestTrackableValue { get; init; }

    public required long HighestTrackableValue { get; init; }

    public required int SignificantDigits { get; init; }

    public required string Payload { get; init; }
}

public sealed record LatencySummary
{
    public string Unit { get; init; } = "us";

    public required long Count { get; init; }

    public required long Min { get; init; }

    public required long P50 { get; init; }

    public long? P90 { get; init; }

    public required long P95 { get; init; }

    public required long P99 { get; init; }

    public long? P999 { get; init; }

    public required long Max { get; init; }

    public required double Mean { get; init; }

    public required HdrPayload Hdr { get; init; }
}

public sealed record QueryClassResult
{
    public required string Name { get; init; }

    public bool? Gated { get; init; }

    public required long Requests { get; init; }

    public required long Errors { get; init; }

    public required LatencySummary Latency { get; init; }

    public LatencySummary? PostFilterLatency { get; init; }
}

public sealed record Validity
{
    public required bool Valid { get; init; }

    public required double DroppedIterationsRatio { get; init; }

    public required double LoadGeneratorCpuMaxPercent { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];
}

public sealed record Throughput
{
    public double? QueriesPerSecond { get; init; }

    public double? BulkDocsPerSecond { get; init; }

    public double? CodingOpsPerSecond { get; init; }

    public double? ImportDocsPerSecond { get; init; }
}

public sealed record IndexLag
{
    public required double ResolutionSeconds { get; init; }

    public required long Samples { get; init; }

    public required double MaxSeconds { get; init; }

    public required double P95Seconds { get; init; }

    public string? SeriesFile { get; init; }
}

public sealed record CodingToSearchable
{
    public required long Samples { get; init; }

    public required double PollIntervalMs { get; init; }

    public double? ProbeTrafficRatio { get; init; }

    public required LatencySummary Latency { get; init; }
}

public sealed record Scenario
{
    public required string Id { get; init; }

    public required ScenarioRole Role { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime EndedUtc { get; init; }

    public required double WarmupSeconds { get; init; }

    public required double DrainSeconds { get; init; }

    public required Validity Validity { get; init; }

    public required IReadOnlyList<QueryClassResult> QueryClasses { get; init; }

    public required Throughput Throughput { get; init; }

    public double? OfferedBulkDocsPerSecond { get; init; }

    public IndexLag? IndexLag { get; init; }

    public CodingToSearchable? CodingToSearchable { get; init; }

    public IReadOnlyList<string>? MetricsFiles { get; init; }
}

public sealed record ShadowLedgerOracle
{
    public required OracleStatus Status { get; init; }

    public long? TouchedDocs { get; init; }

    public long? StaleOverwrites { get; init; }

    public long? VersionRegressions { get; init; }

    public long? MissingDocs { get; init; }

    public long? ValueMismatches { get; init; }

    public long? VersionConflictRejections { get; init; }
}

public sealed record SecurityOracle
{
    public required OracleStatus Status { get; init; }

    public long? Checks { get; init; }

    public long? UnauthorizedRetrievals { get; init; }
}

public sealed record IdempotencyOracle
{
    public required OracleStatus Status { get; init; }

    public long? Trials { get; init; }

    public long? IdempotentTrials { get; init; }

    public double? IdempotentRatio { get; init; }
}

public sealed record Oracles
{
    public required ShadowLedgerOracle ShadowLedger { get; init; }

    public required SecurityOracle Security { get; init; }

    public required IdempotencyOracle Idempotency { get; init; }

    public static Oracles NotRun { get; } = new()
    {
        ShadowLedger = new ShadowLedgerOracle { Status = OracleStatus.NotRun },
        Security = new SecurityOracle { Status = OracleStatus.NotRun },
        Idempotency = new IdempotencyOracle { Status = OracleStatus.NotRun },
    };
}

public sealed record BundleFile
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required long Bytes { get; init; }

    public string? MediaType { get; init; }

    public string? Description { get; init; }
}
