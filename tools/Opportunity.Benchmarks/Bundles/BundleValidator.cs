using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using HdrHistogram;

using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Gates;
using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Bundles;

public sealed record BundleValidationOptions
{
    /// <summary>The gates.yaml the bundle must have been run against (hash must match), e.g. the frozen file in the repo.</summary>
    public string? GatesPath { get; init; }

    /// <summary>Check that every listed file exists next to bundle.json with the recorded size and hash.</summary>
    public bool VerifyFiles { get; init; } = true;
}

public sealed record BundleValidationReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The "complete manifest" rule (E17-T03): schema validity plus the cross-checks a schema cannot express — file
/// integrity, corpus/workload/gates hashes, durability honesty (Q-05), frozen gates for comparative tiers (Q-04),
/// pinned image digests, and percentiles that agree with the raw HDR histograms.
/// </summary>
public static class BundleValidator
{
    private const double DefaultMaxDroppedIterationsRatio = 0.005;
    private const double DefaultMaxLoadGeneratorCpuPercent = 70;

    /// <summary>The nightly developer-regression tier (Q-05).</summary>
    public const string NightlyTier = "T2";

    /// <summary>The PR smoke tier: exempt from the durability rule because it never feeds a benchmark decision (Q-46).</summary>
    public const string PullRequestTier = "T1";

    public static BundleValidationReport Validate(string path, BundleValidationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string file = Directory.Exists(path) ? Path.Combine(path, ResultBundle.FileName) : path;
        if (!File.Exists(file))
        {
            return new BundleValidationReport([$"{file} does not exist."], []);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(file));
        }
        catch (JsonException ex)
        {
            return new BundleValidationReport([$"{file} is not valid JSON: {ex.Message}"], []);
        }

        return Validate(node, Path.GetDirectoryName(Path.GetFullPath(file))!, options ?? new BundleValidationOptions());
    }

    public static BundleValidationReport Validate(JsonNode? node, string directory, BundleValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var warnings = new List<string>();

        IReadOnlyList<string> schemaErrors = BundleSchemas.ValidateBundle(node);
        if (schemaErrors.Count > 0)
        {
            errors.AddRange(schemaErrors.Select(e => "schema: " + e));
            return new BundleValidationReport(errors, warnings);
        }

        ResultBundle bundle = node.Deserialize<ResultBundle>(BenchJson.Options)!;
        GatesFile? gates = LoadGates(options.GatesPath, errors);

        CheckRun(bundle, errors, warnings);
        CheckEnvironment(bundle, errors, warnings);
        CheckScenarios(bundle, gates, errors);
        CheckOracles(bundle.Oracles, errors);
        CheckReferences(bundle, gates, errors);
        if (options.VerifyFiles)
        {
            CheckFiles(bundle, directory, errors, warnings);
        }

        return new BundleValidationReport(errors, warnings);
    }

    public static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrEmpty(path)
        && !Path.IsPathRooted(path)
        && !path.Contains('\\', StringComparison.Ordinal)
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..")
        && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/');

    private static GatesFile? LoadGates(string? path, List<string> errors)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            return GatesFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            errors.Add($"gates: cannot read {path}: {ex.Message}");
            return null;
        }
    }

    private static void CheckRun(ResultBundle bundle, List<string> errors, List<string> warnings)
    {
        RunInfo run = bundle.Run;
        if (run.EndedUtc < run.StartedUtc)
        {
            errors.Add("run: endedUtc is before startedUtc.");
        }

        if (run.Repetition.Index > run.Repetition.Of)
        {
            errors.Add($"run: repetition {run.Repetition.Index} of {run.Repetition.Of}.");
        }

        bool comparative = run.Tier is "T3" or "T4";
        if (comparative && bundle.Gates.Status != GatesStatus.Frozen)
        {
            errors.Add($"gates: tier {run.Tier} needs a frozen, signed-off gates file (Q-04); this run used a {bundle.Gates.Status.ToString().ToUpperInvariant()} file.");
        }

        if (comparative && run.GitDirty)
        {
            errors.Add($"run: tier {run.Tier} results must come from a clean checkout (gitDirty is true).");
        }
        else if (run.GitDirty)
        {
            warnings.Add("run: built from a dirty working tree.");
        }

        if (comparative && run.Repetition.Of < 3)
        {
            errors.Add($"run: tier {run.Tier} needs >= 3 repetitions per scenario (repetition.of = {run.Repetition.Of}).");
        }

        if (run.Tier == "T4" && bundle.Environment.Profile != BenchmarkProfile.EnterpriseReference)
        {
            errors.Add("run: tier T4 (10M validation) runs only on the enterprise-reference profile.");
        }
    }

    private static void CheckEnvironment(ResultBundle bundle, List<string> errors, List<string> warnings)
    {
        EnvironmentManifest env = bundle.Environment;
        bool reference = env.Profile == BenchmarkProfile.EnterpriseReference;

        // Q-05: recompute deviations from the recorded settings; the manifest cannot claim more durability than it has.
        var expected = DurabilityPolicy.Evaluate(env).Select(Key).ToHashSet(StringComparer.Ordinal);
        var recorded = env.Durability.Deviations.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (string missing in expected.Except(recorded).Order(StringComparer.Ordinal))
        {
            errors.Add($"durability: recorded settings deviate from production ({missing}) but the deviation is not declared (Q-05).");
        }

        foreach (string extra in recorded.Except(expected).Order(StringComparer.Ordinal))
        {
            errors.Add($"durability: declared deviation {extra} does not match the recorded settings.");
        }

        // Q-05/Q-46: relaxed settings only on the nightly developer tier (T2) and the PR smoke tier (T1); the T3 1M
        // comparative spike and T4 need production-like durability even on developer hardware.
        if (reference && env.Durability.Mode == DurabilityMode.Relaxed)
        {
            errors.Add("durability: the enterprise-reference profile requires production durability; relaxed settings are allowed only on nightly developer-regression runs (tier T2, Q-05).");
        }
        else if (env.Durability.Mode == DurabilityMode.Relaxed && bundle.Run.Tier is not (NightlyTier or PullRequestTier))
        {
            errors.Add($"durability: tier {bundle.Run.Tier} requires production-like durability; relaxed settings are allowed only on nightly developer-regression ({NightlyTier}) and PR smoke ({PullRequestTier}) runs (Q-05, Q-46).");
        }

        foreach (ContainerInfo container in env.Containers ?? [])
        {
            if (container.Pin is { Matches: false } pin)
            {
                string message = $"images: container {container.Name} runs {container.Image.Reference} but versions.env pins {pin.Key} to {pin.ExpectedDigest}.";
                (reference ? errors : warnings).Add(message);
            }
        }

        if (reference && env.Postgres!.All(p => p.Role != PostgresRole.Replica))
        {
            errors.Add("topology: the enterprise reference needs a PostgreSQL replica (§29).");
        }

        if (reference && env.Opensearch!.Nodes.Count < 3)
        {
            errors.Add($"topology: the enterprise reference needs a 3-node OpenSearch cluster (§29); found {env.Opensearch.Nodes.Count}.");
        }

        static string Key(DurabilityDeviation d) => $"{d.Component}:{d.Instance}:{d.Setting}={d.Actual}";
    }

    private static void CheckScenarios(ResultBundle bundle, GatesFile? gates, List<string> errors)
    {
        double maxDropped = gates?.Document.Policy.Validity.MaxDroppedIterationsRatio ?? DefaultMaxDroppedIterationsRatio;
        double maxCpu = gates?.Document.Policy.Validity.MaxLoadGeneratorCpuPercent ?? DefaultMaxLoadGeneratorCpuPercent;
        foreach (IGrouping<string, Scenario> duplicate in bundle.Scenarios.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"scenarios: id '{duplicate.Key}' is used {duplicate.Count()} times.");
        }

        foreach (Scenario scenario in bundle.Scenarios)
        {
            string where = $"scenario '{scenario.Id}'";
            if (scenario.EndedUtc < scenario.StartedUtc || scenario.StartedUtc < bundle.Run.StartedUtc || scenario.EndedUtc > bundle.Run.EndedUtc)
            {
                errors.Add($"{where}: its time window must lie within the run and end after it starts.");
            }

            if (scenario.Validity.Valid
                && (scenario.Validity.DroppedIterationsRatio > maxDropped || scenario.Validity.LoadGeneratorCpuMaxPercent >= maxCpu))
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{where}: marked valid with {scenario.Validity.DroppedIterationsRatio:P2} dropped iterations / {scenario.Validity.LoadGeneratorCpuMaxPercent}% load-generator CPU (limits {maxDropped:P2} / < {maxCpu}%)."));
            }

            if (!scenario.Validity.Valid && scenario.Validity.Reasons.Count == 0)
            {
                errors.Add($"{where}: invalid scenarios must state their reasons.");
            }

            foreach (QueryClassResult queryClass in scenario.QueryClasses)
            {
                CheckLatency($"{where} class '{queryClass.Name}'", queryClass.Latency, errors);
                if (queryClass.PostFilterLatency is { } postFilter)
                {
                    CheckLatency($"{where} class '{queryClass.Name}' post-filter", postFilter, errors);
                }
            }

            if (scenario.CodingToSearchable is { } probe)
            {
                CheckLatency($"{where} coding→searchable", probe.Latency, errors);
                if (probe.Samples != probe.Latency.Count)
                {
                    errors.Add($"{where}: coding→searchable samples ({probe.Samples}) differ from the histogram count ({probe.Latency.Count}).");
                }
            }

            if (scenario.IndexLag is { } lag && lag.P95Seconds > lag.MaxSeconds)
            {
                errors.Add($"{where}: index-lag p95 exceeds its max.");
            }
        }
    }

    /// <summary>The histogram is authoritative: recorded count and percentiles must be the ones it yields.</summary>
    private static void CheckLatency(string where, LatencySummary latency, List<string> errors)
    {
        HistogramBase histogram;
        try
        {
            histogram = Latency.Decode(latency.Hdr);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or InvalidDataException)
        {
            errors.Add($"{where}: HDR payload cannot be decoded ({ex.Message}).");
            return;
        }

        LatencySummary recomputed = Latency.Summarize(histogram);
        var mismatches = new List<string>();
        void Compare(string name, long recorded, long actual)
        {
            if (recorded != actual)
            {
                mismatches.Add($"{name} {recorded} != {actual}");
            }
        }

        Compare("count", latency.Count, recomputed.Count);
        Compare("min", latency.Min, recomputed.Min);
        Compare("p50", latency.P50, recomputed.P50);
        Compare("p95", latency.P95, recomputed.P95);
        Compare("p99", latency.P99, recomputed.P99);
        Compare("max", latency.Max, recomputed.Max);
        if (mismatches.Count > 0)
        {
            errors.Add($"{where}: summary disagrees with its HDR histogram ({string.Join(", ", mismatches)}).");
        }
    }

    private static void CheckOracles(Oracles oracles, List<string> errors)
    {
        if (oracles.ShadowLedger is { Status: not OracleStatus.NotRun } ledger)
        {
            long failures = (ledger.StaleOverwrites ?? 0) + (ledger.VersionRegressions ?? 0) + (ledger.MissingDocs ?? 0) + (ledger.ValueMismatches ?? 0);
            CheckStatus("shadowLedger", ledger.Status, failures > 0, errors);
        }

        if (oracles.Security is { Status: not OracleStatus.NotRun } security)
        {
            CheckStatus("security", security.Status, security.UnauthorizedRetrievals > 0, errors);
        }

        if (oracles.Idempotency is { Status: not OracleStatus.NotRun, Trials: { } trials, IdempotentTrials: { } ok, IdempotentRatio: { } ratio } idempotency)
        {
            if (ok > trials || (trials > 0 && Math.Abs(ratio - ((double)ok / trials)) > 1e-9) || (trials == 0 && ratio != 0))
            {
                errors.Add("oracles.idempotency: idempotentRatio must equal idempotentTrials / trials.");
            }

            CheckStatus("idempotency", idempotency.Status, ok < trials || trials == 0, errors);
        }
    }

    private static void CheckStatus(string oracle, OracleStatus status, bool failing, List<string> errors)
    {
        if ((status == OracleStatus.Passed) == failing)
        {
            errors.Add($"oracles.{oracle}: status '{status.ToString().ToUpperInvariant()}' contradicts its counters.");
        }
    }

    private static void CheckReferences(ResultBundle bundle, GatesFile? gates, List<string> errors)
    {
        Dictionary<string, BundleFile> files = bundle.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

        // Corpus: the manifest travels with the bundle so the corpus can be regenerated (seed + profile).
        if (bundle.Corpus.ManifestFile is null)
        {
            errors.Add("corpus: manifestFile is required so the corpus can be regenerated from the bundle alone.");
        }
        else if (!files.TryGetValue(bundle.Corpus.ManifestFile, out BundleFile? manifest))
        {
            errors.Add($"corpus: {bundle.Corpus.ManifestFile} is not listed in files.");
        }
        else if (manifest.Sha256 != bundle.Corpus.ManifestSha256)
        {
            errors.Add("corpus: manifestSha256 does not match the bundled corpus manifest.");
        }

        foreach (WorkloadScript script in bundle.Workload.Scripts)
        {
            if (!files.TryGetValue(script.Path, out BundleFile? file))
            {
                errors.Add($"workload: script {script.Path} is not bundled (§29: store workload scripts with every result).");
            }
            else if (file.Sha256 != script.Sha256)
            {
                errors.Add($"workload: script {script.Path} hash differs from the bundled file.");
            }
        }

        if (References.WorkloadSha256(bundle.Workload.Scripts) != bundle.Workload.WorkloadSha256)
        {
            errors.Add("workload: workloadSha256 does not match its scripts.");
        }

        if (!files.TryGetValue(bundle.Gates.Path, out BundleFile? gatesFile))
        {
            errors.Add($"gates: {bundle.Gates.Path} is not bundled.");
        }
        else if (gatesFile.Sha256 != bundle.Gates.Sha256)
        {
            errors.Add("gates: sha256 does not match the bundled gates file.");
        }

        if (gates is not null && gates.Sha256 != bundle.Gates.Sha256)
        {
            errors.Add($"gates: the bundle ran against gates {bundle.Gates.Sha256[..12]}…, not {gates.Path} ({gates.Sha256[..12]}…).");
        }

        foreach (string series in bundle.Scenarios.SelectMany(s => (s.MetricsFiles ?? []).Append(s.IndexLag?.SeriesFile)).OfType<string>())
        {
            if (!files.ContainsKey(series))
            {
                errors.Add($"metrics: {series} is referenced but not listed in files.");
            }
        }
    }

    private static void CheckFiles(ResultBundle bundle, string directory, List<string> errors, List<string> warnings)
    {
        foreach (BundleFile file in bundle.Files)
        {
            if (!IsSafeRelativePath(file.Path))
            {
                errors.Add($"files: '{file.Path}' is not a safe relative path.");
                continue;
            }

            string full = Path.Combine(directory, file.Path);
            if (!File.Exists(full))
            {
                errors.Add($"files: {file.Path} is missing.");
                continue;
            }

            if (new FileInfo(full).Length != file.Bytes || BenchJson.Sha256OfFile(full) != file.Sha256)
            {
                errors.Add($"files: {file.Path} does not match its recorded size/hash.");
            }
        }

        if (bundle.Corpus.ManifestFile is { } manifestPath && bundle.Files.Any(f => f.Path == manifestPath) && File.Exists(Path.Combine(directory, manifestPath)))
        {
            try
            {
                CorpusReference actual = References.Corpus(Path.Combine(directory, manifestPath), bundle.Corpus.LoadPath, manifestPath);
                if (actual.Seed != bundle.Corpus.Seed || actual.ProfileHash != bundle.Corpus.ProfileHash
                    || actual.Generator != bundle.Corpus.Generator || actual.DocumentCount != bundle.Corpus.DocumentCount)
                {
                    errors.Add("corpus: seed/profileHash/generator/documentCount disagree with the bundled corpus manifest.");
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                errors.Add($"corpus: {manifestPath} is not a corpus manifest ({ex.Message}).");
            }
        }

        var listed = bundle.Files.Select(f => f.Path).Append(ResultBundle.FileName).ToHashSet(StringComparer.Ordinal);
        foreach (string present in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))
            .Where(f => !listed.Contains(f))
            .Order(StringComparer.Ordinal))
        {
            warnings.Add($"files: {present} is in the bundle directory but not listed (it will not be published).");
        }
    }
}
