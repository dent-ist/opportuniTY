using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

using HdrHistogram;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Gates;
using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Workloads;

public sealed record K6IngestOptions
{
    /// <summary>k6 <c>--out json=</c> output (NDJSON, optionally .gz): the raw samples the histograms are built from.</summary>
    public required string RawPath { get; init; }

    /// <summary>k6 <c>--summary-export</c> output; bundled as k6/summary.json.</summary>
    public string? SummaryPath { get; init; }

    /// <summary>Load-generator CPU samples (JSON lines <c>{"time": "...Z", "cpuPercent": n}</c>), e.g. from k6/run.sh.</summary>
    public string? LoadGeneratorCpuPath { get; init; }

    public required string QuerySetPath { get; init; }

    /// <summary>Directory of the k6 scripts that ran (every *.js below it is bundled).</summary>
    public required string ScriptsDirectory { get; init; }

    public required string EnvironmentPath { get; init; }

    public required string CorpusManifestPath { get; init; }

    public required string GatesPath { get; init; }

    public required string OutputDirectory { get; init; }

    public required string Tier { get; init; }

    public required string Suite { get; init; }

    public string? Candidate { get; init; }

    public int Repetition { get; init; } = 1;

    public int RepetitionOf { get; init; } = 1;

    public CacheState CacheState { get; init; } = CacheState.Warm;

    public required string Operator { get; init; }

    public required References.GitState Git { get; init; }

    public CorpusLoadPath LoadPath { get; init; } = CorpusLoadPath.Import;

    public string WorkloadName { get; init; } = "mixed";

    public WorkloadModel Model { get; init; } = WorkloadModel.Mixed;

    /// <summary>k6 phase tag -> bundle scenario role. Unlisted phases default by name (see <see cref="DefaultRole"/>).</summary>
    public IReadOnlyDictionary<string, ScenarioRole> PhaseRoles { get; init; } = new Dictionary<string, ScenarioRole>(StringComparer.Ordinal);

    public double WarmupSeconds { get; init; }

    public double DrainSeconds { get; init; }

    public double? OfferedQueriesPerSecond { get; init; }

    public double? OfferedBulkDocsPerSecond { get; init; }

    public int? Reviewers { get; init; }

    public Oracles? Oracles { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// <c>ingest-k6</c>: turns a k6 run (raw samples + summary + load-generator CPU) into a result bundle. One scenario per
/// k6 <c>phase</c> tag; per scenario one query class per taxonomy bucket plus the gated <c>simple</c>/<c>complex</c>
/// classes and the reviewer operations, each with a raw HDR histogram in microseconds (samples in the warm-up window
/// and failed requests are excluded from latency, counted in requests/errors). A scenario is valid only when dropped
/// iterations and load-generator CPU are within the gates' validity policy and the CPU was actually sampled.
/// </summary>
public static class K6Ingest
{
    public const string RequestMetric = "opp_request";

    public static ScenarioRole DefaultRole(string phase) => phase switch
    {
        "idle" or "idle-baseline" => ScenarioRole.IdleBaseline,
        "bulk" or "bulk-load" => ScenarioRole.BulkLoad,
        "ceiling" or "bulk-ceiling" => ScenarioRole.BulkCeiling,
        "fault" or "fault-window" => ScenarioRole.FaultWindow,
        "calibration" => ScenarioRole.Calibration,
        "smoke" => ScenarioRole.Smoke,
        _ => ScenarioRole.Other,
    };

    /// <summary>Builds and writes the bundle (validated by <see cref="BundleWriter"/>); returns the bundle.json path.</summary>
    public static string Ingest(K6IngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        QuerySet querySet = QuerySet.Load(options.QuerySetPath);
        CorpusReference corpus = References.Corpus(options.CorpusManifestPath, options.LoadPath);
        if (corpus.ManifestSha256 != querySet.Corpus.ManifestSha256)
        {
            throw new InvalidOperationException($"{options.QuerySetPath} was generated from corpus manifest {querySet.Corpus.ManifestSha256[..12]}…, not {options.CorpusManifestPath} ({corpus.ManifestSha256[..12]}…).");
        }

        GatesFile gates = GatesFile.Load(options.GatesPath);
        EnvironmentManifest environment = BenchJson.Deserialize<EnvironmentManifest>(File.ReadAllText(options.EnvironmentPath));
        RawRun raw = RawRun.Read(options.RawPath, options.WarmupSeconds);
        if (raw.Phases.Count == 0)
        {
            throw new InvalidOperationException($"{options.RawPath} has no {RequestMetric} samples: was it written by the opportuniTY k6 scripts with --out json=...?");
        }

        IReadOnlyList<CpuSample> cpu = options.LoadGeneratorCpuPath is { } cpuPath ? CpuSample.Read(cpuPath) : [];

        DateTime started = raw.Phases.Values.Min(p => p.First);
        DateTime ended = raw.Phases.Values.Max(p => p.Last);
        var writer = new BundleWriter(Path.Combine(options.OutputDirectory, References.RunId(started, options.Suite, options.Candidate, options.Repetition)));
        writer.AddFile(options.CorpusManifestPath, References.CorpusManifestFile, description: "corpus manifest (E17-T01)");
        writer.AddFile(gates.Path, GatesFile.FileName, description: "gates the run was evaluated against");
        var scripts = new List<WorkloadScript>();
        BundleFile queriesFile = writer.AddFile(options.QuerySetPath, "workload/queries.json", description: "query set replayed by the workload (E17-T04)");
        scripts.Add(new WorkloadScript(queriesFile.Path, queriesFile.Sha256));
        string scriptRoot = Path.GetFullPath(options.ScriptsDirectory);
        foreach (string script in Directory.EnumerateFiles(scriptRoot, "*.js", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = "workload/k6/" + Path.GetRelativePath(scriptRoot, script).Replace('\\', '/');
            BundleFile file = writer.AddFile(script, relative);
            scripts.Add(new WorkloadScript(file.Path, file.Sha256));
        }

        if (options.SummaryPath is { } summary)
        {
            writer.AddFile(summary, "k6/summary.json", description: "k6 end-of-test summary");
        }

        string? cpuFile = options.LoadGeneratorCpuPath is { } c ? writer.AddFile(c, "metrics/loadgen-cpu.jsonl", description: "load-generator CPU %, 1 s samples").Path : null;
        double maxDropped = gates.Document.Policy.Validity.MaxDroppedIterationsRatio;
        double maxCpu = gates.Document.Policy.Validity.MaxLoadGeneratorCpuPercent;

        var scenarios = raw.Phases.Values.OrderBy(p => p.First).Select(phase =>
        {
            ScenarioRole role = options.PhaseRoles.TryGetValue(phase.Name, out ScenarioRole r) ? r : DefaultRole(phase.Name);
            var cpuInWindow = cpu.Where(s => s.Time >= phase.First && s.Time <= phase.Last).ToList();
            double cpuMax = cpuInWindow.Count == 0 ? 0 : cpuInWindow.Max(s => s.CpuPercent);
            long attempts = phase.Iterations + phase.Dropped;
            double dropped = attempts == 0 ? 0 : Math.Round((double)phase.Dropped / attempts, 6);
            var reasons = new List<string>();
            if (dropped > maxDropped)
            {
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{dropped:P2} dropped iterations (limit {maxDropped:P2})"));
            }

            if (cpuInWindow.Count == 0)
            {
                reasons.Add("load-generator CPU was not sampled in this window");
            }
            else if (cpuMax >= maxCpu)
            {
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"load-generator CPU reached {cpuMax:0.#}% (limit < {maxCpu}%)"));
            }

            double seconds = Math.Max(0.001, (phase.Last - phase.First).TotalSeconds);
            long searches = phase.Requests.Where(kv => kv.Key.StartsWith("search:", StringComparison.Ordinal)).Sum(kv => kv.Value.Requests);
            bool bulk = phase.Classes.ContainsKey("bulk-submit");
            return new Scenario
            {
                Id = phase.Name,
                Role = role,
                StartedUtc = phase.First,
                EndedUtc = phase.Last,
                WarmupSeconds = options.WarmupSeconds,
                DrainSeconds = options.DrainSeconds,
                Validity = new Validity { Valid = reasons.Count == 0, DroppedIterationsRatio = dropped, LoadGeneratorCpuMaxPercent = Math.Round(cpuMax, 2), Reasons = reasons },
                QueryClasses = QueryClasses(phase),
                Throughput = new Throughput
                {
                    QueriesPerSecond = Math.Round(searches / seconds, 3),
                    CodingOpsPerSecond = phase.Classes.TryGetValue("coding-write", out ClassAccumulator? coding) ? Math.Round(coding.Requests / seconds, 3) : null,
                },
                OfferedBulkDocsPerSecond = bulk ? options.OfferedBulkDocsPerSecond : null,
                MetricsFiles = cpuFile is null ? null : [cpuFile],
            };
        }).ToList();

        var bundle = new ResultBundle
        {
            RunId = Path.GetFileName(writer.Directory),
            Run = new RunInfo
            {
                GitSha = options.Git.Sha,
                GitDirty = options.Git.Dirty,
                GitRef = options.Git.Ref,
                StartedUtc = started,
                EndedUtc = ended,
                Operator = options.Operator,
                Tier = options.Tier,
                Suite = options.Suite,
                Candidate = options.Candidate,
                Repetition = new Repetition(options.Repetition, options.RepetitionOf),
                CacheState = options.CacheState,
                Notes = options.Notes,
            },
            Environment = environment,
            Corpus = corpus,
            Workload = new WorkloadReference
            {
                Name = options.WorkloadName,
                Version = HarnessInfo.Version,
                TaxonomyVersion = querySet.TaxonomyVersion,
                QuerySeed = querySet.QuerySeed,
                Model = options.Model,
                Scripts = scripts,
                WorkloadSha256 = References.WorkloadSha256(scripts),
                Mix = querySet.Mix,
                OfferedRate = options.OfferedQueriesPerSecond is null && options.OfferedBulkDocsPerSecond is null
                    ? null
                    : new OfferedRate { QueriesPerSecond = options.OfferedQueriesPerSecond, BulkDocsPerSecond = options.OfferedBulkDocsPerSecond },
                Reviewers = options.Reviewers is { } count
                    ? new Reviewers { Count = count, ThinkTimeMedianSeconds = ReviewerSessionModel.ThinkTimeMedianSeconds, ThinkTimeP90Seconds = ReviewerSessionModel.ThinkTimeP90Seconds }
                    : null,
            },
            Gates = gates.ToReference(),
            Scenarios = scenarios,
            Oracles = options.Oracles ?? Oracles.NotRun,
        };
        return writer.Write(bundle);
    }

    private static List<QueryClassResult> QueryClasses(PhaseAccumulator phase)
    {
        IEnumerable<string> order = ["simple", "complex", .. QueryTaxonomy.Classes.Select(c => c.Id), "document-view", "coding-write", "bulk-submit"];
        var results = new List<QueryClassResult>();
        foreach (string name in order.Concat(phase.Classes.Keys.Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            if (phase.Classes.TryGetValue(name, out ClassAccumulator? acc))
            {
                results.Add(new QueryClassResult
                {
                    Name = name,
                    Gated = name is "simple" or "complex",
                    Requests = acc.Requests,
                    Errors = acc.Errors,
                    Latency = Latency.Summarize(acc.Histogram),
                });
            }
        }

        return results;
    }

    private sealed class ClassAccumulator
    {
        public LongHistogram Histogram { get; } = Latency.NewHistogram();

        public long Requests { get; set; }

        public long Errors { get; set; }
    }

    private sealed class PhaseAccumulator(string name)
    {
        public string Name { get; } = name;

        public DateTime First { get; set; } = DateTime.MaxValue;

        public DateTime Last { get; set; } = DateTime.MinValue;

        public Dictionary<string, ClassAccumulator> Classes { get; } = new(StringComparer.Ordinal);

        /// <summary>Per op:qclass request counts (throughput).</summary>
        public Dictionary<string, ClassAccumulator> Requests { get; } = new(StringComparer.Ordinal);

        public long Iterations { get; set; }

        public long Dropped { get; set; }

        public void See(DateTime time)
        {
            First = time < First ? time : First;
            Last = time > Last ? time : Last;
        }
    }

    private sealed class RawRun
    {
        public Dictionary<string, PhaseAccumulator> Phases { get; } = new(StringComparer.Ordinal);

        public static RawRun Read(string path, double warmupSeconds)
        {
            var run = new RawRun();
            var scenarioPhase = new Dictionary<string, string>(StringComparer.Ordinal);

            // Pass 1: phase windows and the scenario -> phase map (dropped_iterations carries only the scenario tag).
            foreach (Sample s in Samples(path))
            {
                if (s.Tags.TryGetValue("phase", out string? phase) && s.Metric is RequestMetric or "iterations")
                {
                    PhaseAccumulator acc = run.Phase(phase);
                    acc.See(s.Time);
                    if (s.Metric == "iterations" && s.Tags.TryGetValue("scenario", out string? scenario))
                    {
                        scenarioPhase.TryAdd(scenario, phase);
                    }
                }
            }

            var windows = run.Phases.Values.OrderBy(p => p.First).ToList();
            foreach (Sample s in Samples(path))
            {
                switch (s.Metric)
                {
                    case RequestMetric when s.Tags.TryGetValue("phase", out string? phase):
                        Record(run.Phases[phase], s, warmupSeconds);
                        break;
                    case "iterations" or "dropped_iterations":
                        PhaseAccumulator? target = s.Tags.TryGetValue("phase", out string? p) ? run.Phases.GetValueOrDefault(p)
                            : s.Tags.TryGetValue("scenario", out string? sc) && scenarioPhase.TryGetValue(sc, out string? mapped) ? run.Phases[mapped]
                            : windows.FirstOrDefault(w => s.Time >= w.First && s.Time <= w.Last);
                        if (target is not null)
                        {
                            long n = (long)s.Value;
                            if (s.Metric == "iterations")
                            {
                                target.Iterations += n;
                            }
                            else
                            {
                                target.Dropped += n;
                            }
                        }

                        break;
                    default:
                        break;
                }
            }

            return run;
        }

        private PhaseAccumulator Phase(string name)
        {
            if (!Phases.TryGetValue(name, out PhaseAccumulator? acc))
            {
                Phases[name] = acc = new PhaseAccumulator(name);
            }

            return acc;
        }

        private static void Record(PhaseAccumulator phase, Sample s, double warmupSeconds)
        {
            string op = s.Tags.GetValueOrDefault("op") ?? "unknown";
            string outcome = s.Tags.GetValueOrDefault("outcome") ?? "ok";
            bool measured = (s.Time - phase.First).TotalSeconds >= warmupSeconds;
            var names = new List<string>();
            if (op == "search")
            {
                names.Add(s.Tags.GetValueOrDefault("gate") ?? "unknown");
                names.Add(s.Tags.GetValueOrDefault("qclass") ?? "unknown");
            }
            else
            {
                names.Add(op);
            }

            string key = op + ":" + s.Tags.GetValueOrDefault("qclass");
            Count(phase.Requests, key, outcome, measured: false, s.Value);
            foreach (string name in names)
            {
                Count(phase.Classes, name, outcome, measured, s.Value);
            }
        }

        private static void Count(Dictionary<string, ClassAccumulator> map, string name, string outcome, bool measured, double milliseconds)
        {
            if (!map.TryGetValue(name, out ClassAccumulator? acc))
            {
                map[name] = acc = new ClassAccumulator();
            }

            acc.Requests++;
            if (outcome == "error")
            {
                acc.Errors++;
            }
            else if (outcome == "ok" && measured)
            {
                long micros = Math.Clamp((long)Math.Round(milliseconds * 1000, MidpointRounding.AwayFromZero), 1, acc.Histogram.HighestTrackableValue);
                acc.Histogram.RecordValue(micros);
            }
        }

        private static IEnumerable<Sample> Samples(string path)
        {
            using FileStream file = File.OpenRead(path);
            using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || !line.Contains("\"Point\"", StringComparison.Ordinal))
                {
                    continue;
                }

                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.GetProperty("type").GetString() != "Point")
                {
                    continue;
                }

                string metric = root.GetProperty("metric").GetString()!;
                if (metric is not (RequestMetric or "iterations" or "dropped_iterations"))
                {
                    continue;
                }

                JsonElement data = root.GetProperty("data");
                var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                if (data.TryGetProperty("tags", out JsonElement tagElement) && tagElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty tag in tagElement.EnumerateObject())
                    {
                        tags[tag.Name] = tag.Value.ToString();
                    }
                }

                DateTime time = DateTime.Parse(data.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                yield return new Sample(metric, time, data.GetProperty("value").GetDouble(), tags);
            }
        }
    }

    private sealed record Sample(string Metric, DateTime Time, double Value, Dictionary<string, string> Tags);
}

/// <summary>One load-generator CPU sample (whole-host utilisation, percent of all cores).</summary>
public sealed record CpuSample(DateTime Time, double CpuPercent)
{
    public static IReadOnlyList<CpuSample> Read(string path)
    {
        var samples = new List<CpuSample>();
        foreach (string line in File.ReadLines(path).Where(l => l.Trim().Length > 0))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            samples.Add(new CpuSample(
                DateTime.Parse(root.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                root.GetProperty("cpuPercent").GetDouble()));
        }

        return samples;
    }
}
