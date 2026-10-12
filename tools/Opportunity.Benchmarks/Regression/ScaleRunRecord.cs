namespace Opportunity.Benchmarks.Regression;

/// <summary>
/// The nightly scale-regression sidecar (E18-T06), bundled next to bundle.json as <see cref="FileName"/>: what the result
/// bundle has no slot for — the scenario version, the effective configuration, every pipeline stage's duration and
/// throughput, and the run's correctness counters (each must be 0).
/// </summary>
public sealed record ScaleRunRecord
{
    public const string FileName = "scale-run.json";

    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Bumped whenever the workload changes in a way that makes runs incomparable; a new version starts a new baseline series.</summary>
    public required string ScenarioVersion { get; init; }

    public required long Documents { get; init; }

    public required ulong Seed { get; init; }

    /// <summary>Effective settings (corpus profile overrides, chunk sizes, query iterations, worker configuration).</summary>
    public required IReadOnlyDictionary<string, string> Config { get; init; }

    /// <summary>import → index → search → bulk tag → export, in run order.</summary>
    public required IReadOnlyList<StageResult> Stages { get; init; }

    /// <summary>Correctness counters; any value above 0 is a correctness failure.</summary>
    public required IReadOnlyDictionary<string, long> Counters { get; init; }

    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>One pipeline stage: wall-clock seconds and documents processed.</summary>
public sealed record StageResult
{
    public required string Name { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required double Seconds { get; init; }

    public required long Documents { get; init; }

    public double DocsPerSecond => Seconds <= 0 ? 0 : Math.Round(Documents / Seconds, 3);
}
