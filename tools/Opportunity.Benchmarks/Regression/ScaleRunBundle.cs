using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using HdrHistogram;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Gates;
using Opportunity.Benchmarks.Infrastructure;
using Opportunity.Benchmarks.Workloads;

namespace Opportunity.Benchmarks.Regression;

/// <summary>
/// Latencies of one scenario per query bucket (a taxonomy class or an operation), plus the gated <c>simple</c> and
/// <c>complex</c> aggregates the §26 gates read. Thread-safe; failed requests count as errors and stay out of the histogram.
/// </summary>
public sealed class LatencyRecorder
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public void Record(string bucket, GateClass? gate, TimeSpan elapsed, bool succeeded = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        Get(bucket).Add(elapsed, succeeded);
        if (gate is { } g)
        {
            Get(g.Name()).Add(elapsed, succeeded);
        }
    }

    public long Count(string bucket) => _buckets.TryGetValue(bucket, out Bucket? b) ? b.Requests : 0;

    public IReadOnlyList<QueryClassResult> Results()
    {
        IEnumerable<string> order = ["simple", "complex", .. QueryTaxonomy.Classes.Select(c => c.Id)];
        return [.. order.Concat(_buckets.Keys.Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)
            .Where(_buckets.ContainsKey)
            .Select(name => _buckets[name].ToResult(name, gated: name is "simple" or "complex"))];
    }

    private Bucket Get(string name) => _buckets.GetOrAdd(name, _ => new Bucket());

    private sealed class Bucket
    {
        private readonly Lock _gate = new();
        private readonly LongHistogram _histogram = Latency.NewHistogram();

        public long Requests { get; private set; }

        private long Errors { get; set; }

        public void Add(TimeSpan elapsed, bool succeeded)
        {
            lock (_gate)
            {
                Requests++;
                if (!succeeded)
                {
                    Errors++;
                    return;
                }

                _histogram.RecordValue(Math.Clamp((long)Math.Round(elapsed.TotalMicroseconds), 1, _histogram.HighestTrackableValue));
            }
        }

        public QueryClassResult ToResult(string name, bool gated)
        {
            lock (_gate)
            {
                return new QueryClassResult { Name = name, Gated = gated, Requests = Requests, Errors = Errors, Latency = Latency.Summarize(_histogram.Copy()) };
            }
        }
    }
}

/// <summary>
/// Search-index lag sampled at a fixed resolution (test-strategy §5.1: now − commit time of the oldest search work not yet
/// reflected in the watermark). Thread-safe.
/// </summary>
public sealed class LagSeries(double resolutionSeconds = 1)
{
    private readonly ConcurrentQueue<(DateTime Utc, double Seconds)> _samples = new();

    public double ResolutionSeconds { get; } = resolutionSeconds;

    public int Count => _samples.Count;

    public void Add(DateTime utc, double seconds) => _samples.Enqueue((utc, Math.Max(0, seconds)));

    public IndexLag Summarize(string? seriesFile = null)
    {
        double[] values = [.. _samples.Select(s => s.Seconds).Order()];
        return new IndexLag
        {
            ResolutionSeconds = ResolutionSeconds,
            Samples = values.Length,
            MaxSeconds = values.Length == 0 ? 0 : Math.Round(values[^1], 3),
            P95Seconds = values.Length == 0 ? 0 : Math.Round(values[Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1)], 3),
            SeriesFile = seriesFile,
        };
    }

    public string ToCsv()
    {
        var text = new StringBuilder("utc,lagSeconds\n");
        foreach ((DateTime utc, double seconds) in _samples.OrderBy(s => s.Utc))
        {
            text.Append(CultureInfo.InvariantCulture, $"{utc:yyyy-MM-dd'T'HH:mm:ss.fff'Z'},{seconds:0.###}\n");
        }

        return text.ToString();
    }
}

/// <summary>Whole-host CPU utilisation from <c>/proc/stat</c> (Linux), sampled once a second while running.</summary>
public sealed class HostCpuSampler : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<CpuSample> _samples = new();
    private readonly Task _loop;
    private readonly string _stat;

    private HostCpuSampler(string stat)
    {
        _stat = stat;
        _loop = Task.Run(LoopAsync);
    }

    public static HostCpuSampler Start(string systemRoot = "/") => new(Path.Combine(systemRoot, "proc", "stat"));

    /// <summary>Highest sample in the window (0 when nothing was sampled there, e.g. not Linux).</summary>
    public double MaxPercent(DateTime fromUtc, DateTime toUtc) =>
        _samples.Where(s => s.Time >= fromUtc && s.Time <= toUtc).Select(s => s.CpuPercent).DefaultIfEmpty(0).Max();

    public bool Sampled(DateTime fromUtc, DateTime toUtc) => _samples.Any(s => s.Time >= fromUtc && s.Time <= toUtc);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        (long Busy, long Total)? previous = Read();
        if (previous is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Read() is not { } current)
                {
                    return;
                }

                long total = current.Total - previous.Value.Total;
                if (total > 0)
                {
                    _samples.Enqueue(new CpuSample(DateTime.UtcNow, Math.Round(100.0 * (current.Busy - previous.Value.Busy) / total, 2)));
                }

                previous = current;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private (long Busy, long Total)? Read()
    {
        try
        {
            string? line = File.ReadLines(_stat).FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            long[] values = [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(v => long.Parse(v, CultureInfo.InvariantCulture))];
            long idle = values[3] + (values.Length > 4 ? values[4] : 0);
            long total = values.Take(8).Sum();
            return (total - idle, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Everything a nightly scale-regression run hands to <see cref="ScaleRunBundle.Write"/>.</summary>
public sealed record ScaleRunBundleInput
{
    public const string Suite = "nightly/scale-regression";

    /// <summary>Parent directory; the run directory (named by run id) is created below it.</summary>
    public required string OutputDirectory { get; init; }

    public required References.GitState Git { get; init; }

    public string Operator { get; init; } = System.Environment.UserName;

    public required DateTime StartedUtc { get; init; }

    public required DateTime EndedUtc { get; init; }

    public required EnvironmentManifest Environment { get; init; }

    public required string CorpusManifestPath { get; init; }

    public required string GatesPath { get; init; }

    /// <summary>Workload definition files to bundle under <c>workload/</c> (source path → bundled name), e.g. the resolved queries.</summary>
    public required IReadOnlyDictionary<string, string> WorkloadFiles { get; init; }

    /// <summary>Additional generated files (bundled path → content), e.g. index-lag series.</summary>
    public IReadOnlyDictionary<string, string> Texts { get; init; } = new Dictionary<string, string>();

    public required IReadOnlyList<Scenario> Scenarios { get; init; }

    public required Oracles Oracles { get; init; }

    public required ScaleRunRecord Record { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Writes a nightly scale-regression run as a schema-valid E17-T03 result bundle (tier T2) plus its sidecar.</summary>
public static class ScaleRunBundle
{
    public const string Tier = BundleValidator.NightlyTier;

    /// <summary>Validates and writes the bundle; returns the bundle.json path.</summary>
    public static string Write(ScaleRunBundleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var writer = new BundleWriter(Path.Combine(input.OutputDirectory, References.RunId(input.StartedUtc, ScaleRunBundleInput.Suite, null, 1)));
        CorpusReference corpus = References.Corpus(input.CorpusManifestPath, CorpusLoadPath.Import);
        writer.AddFile(input.CorpusManifestPath, References.CorpusManifestFile, description: "corpus manifest (E17-T01)");
        GatesFile gates = GatesFile.Load(input.GatesPath);
        writer.AddFile(gates.Path, GatesFile.FileName, description: "gates the run was evaluated against");
        var scripts = new List<WorkloadScript>();
        foreach ((string source, string name) in input.WorkloadFiles.OrderBy(f => f.Value, StringComparer.Ordinal))
        {
            BundleFile file = writer.AddFile(source, "workload/" + name, description: "scale-regression workload definition");
            scripts.Add(new WorkloadScript(file.Path, file.Sha256));
        }

        foreach ((string path, string content) in input.Texts.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            writer.AddText(path, content);
        }

        writer.AddText(ScaleRunRecord.FileName, BenchJson.Serialize(input.Record), description: "stages, configuration and correctness counters (E18-T06)");

        var bundle = new ResultBundle
        {
            RunId = Path.GetFileName(writer.Directory),
            Run = new RunInfo
            {
                GitSha = input.Git.Sha,
                GitDirty = input.Git.Dirty,
                GitRef = input.Git.Ref,
                StartedUtc = input.StartedUtc,
                EndedUtc = input.EndedUtc,
                Operator = input.Operator,
                Tier = Tier,
                Suite = ScaleRunBundleInput.Suite,
                Repetition = new Repetition(1, 1),
                CacheState = CacheState.Cold,
                Notes = input.Notes,
            },
            Environment = input.Environment,
            Corpus = corpus,
            Workload = new WorkloadReference
            {
                Name = "scale-regression",
                Version = input.Record.ScenarioVersion,
                TaxonomyVersion = QueryTaxonomy.Version,
                QuerySeed = input.Record.Seed,
                Model = WorkloadModel.Closed,
                Scripts = scripts,
                WorkloadSha256 = References.WorkloadSha256(scripts),
            },
            Gates = gates.ToReference(),
            Scenarios = input.Scenarios,
            Oracles = input.Oracles,
        };
        return writer.Write(bundle);
    }
}
