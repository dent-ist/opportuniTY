using System.Globalization;
using System.Text;
using System.Text.Json;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Regression;

public enum RunStatus
{
    /// <summary>Every gated metric within tolerance of the baseline.</summary>
    Pass,

    /// <summary>The series has fewer accepted runs than the baseline needs; nothing is judged yet.</summary>
    CollectingBaseline,

    /// <summary>At least one gated metric is worse than tolerance (this run is kept out of the baseline).</summary>
    Regressed,

    /// <summary>At least one correctness counter is above 0 (this run is kept out of the baseline).</summary>
    CorrectnessFailed,
}

/// <summary>The judgement recorded with a history entry.</summary>
public sealed record EntryVerdict
{
    public required RunStatus Status { get; init; }

    public IReadOnlyList<string> Regressed { get; init; } = [];

    public IReadOnlyList<string> CorrectnessFailures { get; init; } = [];

    public bool Alert { get; init; }

    /// <summary>Whether this run may join a baseline.</summary>
    public bool Accepted => Status is RunStatus.Pass or RunStatus.CollectingBaseline;
}

/// <summary>One line of <c>history.jsonl</c>: a run reduced to its series, metrics and counters (E18-T06 trends).</summary>
public sealed record HistoryEntry
{
    public required string RunId { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required string GitSha { get; init; }

    public string? GitRef { get; init; }

    /// <summary>Link to the CI run that produced it, when known.</summary>
    public string? Url { get; init; }

    /// <summary>Opaque series key; runs are compared only within a series.</summary>
    public required string Series { get; init; }

    public required string SeriesLabel { get; init; }

    /// <summary>Starts a new baseline within the series (a maintainer accepted a deliberate change).</summary>
    public bool Rebaseline { get; init; }

    public required IReadOnlyDictionary<string, double> Metrics { get; init; }

    public required IReadOnlyDictionary<string, long> Counters { get; init; }

    public EntryVerdict? Verdict { get; init; }
}

/// <summary>Reads a nightly run directory into a <see cref="HistoryEntry"/>, and reads/writes <c>history.jsonl</c>.</summary>
public static class RunHistory
{
    public const string FileName = "history.jsonl";

    private static readonly JsonSerializerOptions LineOptions = new(BenchJson.Options) { WriteIndented = false };

    /// <summary>Metric ids: <c>search.&lt;scenario&gt;.&lt;bucket&gt;.p50|p95|p99</c> (ms), <c>coding-to-searchable.&lt;scenario&gt;.*</c> (ms),
    /// <c>index-lag.&lt;scenario&gt;.p95|max</c> (s), <c>stage.&lt;stage&gt;.docs-per-second|seconds</c>.</summary>
    public static HistoryEntry FromRun(string runDirectory, bool rebaseline = false, string? url = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ResultBundle bundle = BenchJson.Deserialize<ResultBundle>(File.ReadAllText(Path.Combine(runDirectory, ResultBundle.FileName)));
        string sidecar = Path.Combine(runDirectory, ScaleRunRecord.FileName);
        ScaleRunRecord record = File.Exists(sidecar)
            ? BenchJson.Deserialize<ScaleRunRecord>(File.ReadAllText(sidecar))
            : throw new FileNotFoundException($"{runDirectory} has no {ScaleRunRecord.FileName}: not a scale-regression run.", sidecar);

        var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (Scenario scenario in bundle.Scenarios)
        {
            foreach (QueryClassResult queryClass in scenario.QueryClasses.Where(q => q.Latency.Count > 0))
            {
                AddLatency(metrics, $"search.{scenario.Id}.{queryClass.Name}", queryClass.Latency);
            }

            if (scenario.CodingToSearchable is { Latency.Count: > 0 } probe)
            {
                AddLatency(metrics, $"coding-to-searchable.{scenario.Id}", probe.Latency);
            }

            if (scenario.IndexLag is { Samples: > 0 } lag)
            {
                metrics[$"index-lag.{scenario.Id}.p95"] = lag.P95Seconds;
                metrics[$"index-lag.{scenario.Id}.max"] = lag.MaxSeconds;
            }
        }

        foreach (StageResult stage in record.Stages)
        {
            metrics[$"stage.{stage.Name}.docs-per-second"] = stage.DocsPerSecond;
            metrics[$"stage.{stage.Name}.seconds"] = Math.Round(stage.Seconds, 3);
        }

        var counters = new SortedDictionary<string, long>(record.Counters.ToDictionary(c => c.Key, c => c.Value), StringComparer.Ordinal);
        AddOracle(counters, bundle.Oracles);

        (string series, string label) = SeriesOf(bundle.Environment, record);
        return new HistoryEntry
        {
            RunId = bundle.RunId,
            StartedUtc = bundle.Run.StartedUtc,
            GitSha = bundle.Run.GitSha,
            GitRef = bundle.Run.GitRef,
            Url = url,
            Series = series,
            SeriesLabel = label,
            Rebaseline = rebaseline,
            Metrics = metrics,
            Counters = counters,
        };
    }

    /// <summary>
    /// Runs are comparable only on the same runner shape, corpus and scenario version (test-strategy §3: a different
    /// machine starts a new series).
    /// </summary>
    public static (string Key, string Label) SeriesOf(EnvironmentManifest environment, ScaleRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(record);
        HostInfo host = environment.Hosts[0];
        long memoryGiB = (long)Math.Round(host.Memory.TotalBytes / (1024.0 * 1024 * 1024));
        string label = string.Create(CultureInfo.InvariantCulture,
            $"{record.Documents} docs, seed {record.Seed}, scenario {record.ScenarioVersion} on {host.Cpu.Model} ({host.Cpu.LogicalCores} vCPU, {memoryGiB} GiB)");
        string key = BenchJson.Sha256Hex(Encoding.UTF8.GetBytes(string.Join('\n',
            JsonNamingPolicy.KebabCaseLower.ConvertName(environment.Profile.ToString()), host.Cpu.Model, host.Cpu.Architecture,
            host.Cpu.LogicalCores.ToString(CultureInfo.InvariantCulture), memoryGiB.ToString(CultureInfo.InvariantCulture),
            record.Documents.ToString(CultureInfo.InvariantCulture), record.Seed.ToString(CultureInfo.InvariantCulture), record.ScenarioVersion)))[..16];
        return (key, label);
    }

    public static IReadOnlyList<HistoryEntry> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        return [.. File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(l => BenchJson.Deserialize<HistoryEntry>(l)).OrderBy(e => e.StartedUtc)];
    }

    /// <summary>Writes the entries (oldest first), dropping those older than <paramref name="retentionDays"/> before <paramref name="nowUtc"/>.</summary>
    public static IReadOnlyList<HistoryEntry> Save(string path, IEnumerable<HistoryEntry> entries, DateTime nowUtc, int retentionDays)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DateTime cutoff = nowUtc.AddDays(-retentionDays);
        var kept = entries.Where(e => e.StartedUtc >= cutoff).GroupBy(e => e.RunId, StringComparer.Ordinal).Select(g => g.Last()).OrderBy(e => e.StartedUtc).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, string.Concat(kept.Select(e => JsonSerializer.Serialize(e, LineOptions) + "\n")), new UTF8Encoding(false));
        return kept;
    }

    private static void AddLatency(SortedDictionary<string, double> metrics, string prefix, LatencySummary latency)
    {
        metrics[prefix + ".p50"] = Milliseconds(latency.P50);
        metrics[prefix + ".p95"] = Milliseconds(latency.P95);
        metrics[prefix + ".p99"] = Milliseconds(latency.P99);
    }

    private static double Milliseconds(long micros) => Math.Round(micros / 1000.0, 3);

    private static void AddOracle(SortedDictionary<string, long> counters, Oracles oracles)
    {
        if (oracles.ShadowLedger.Status != OracleStatus.NotRun)
        {
            counters["shadow-ledger.stale-overwrites"] = oracles.ShadowLedger.StaleOverwrites ?? 0;
            counters["shadow-ledger.version-regressions"] = oracles.ShadowLedger.VersionRegressions ?? 0;
            counters["shadow-ledger.missing-docs"] = oracles.ShadowLedger.MissingDocs ?? 0;
            counters["shadow-ledger.value-mismatches"] = oracles.ShadowLedger.ValueMismatches ?? 0;
        }

        if (oracles.Security.Status != OracleStatus.NotRun)
        {
            counters["security.unauthorized-retrievals"] = oracles.Security.UnauthorizedRetrievals ?? 0;
        }
    }
}
