using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Gates;
using Opportunity.Benchmarks.Infrastructure;
using Opportunity.Benchmarks.Regression;
using Opportunity.Benchmarks.Workloads;

namespace Opportunity.Benchmarks.Tests;

/// <summary>
/// E18-T06 regression guard: relative gates against a rolling per-series baseline, the 15% tolerance band with a noise
/// floor, alerts on performance only when sustained over two runs and on correctness at once, rebaselines, 90-day history and the
/// dashboard; and the nightly run's bundle (schema-valid, with its sidecar) reduced to history metrics.
/// </summary>
public sealed class ScaleRegressionTests
{
    private static readonly DateTime Night = new(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);

    private static RegressionPolicy Policy => RegressionPolicy.Load(RegressionPolicy.Locate()!);

    [Fact]
    public void Repository_policy_gates_p95_and_throughput_relative_to_the_baseline()
    {
        RegressionPolicy policy = Policy;
        policy.Tolerance.Should().Be(0.15, "AC: > 15% p95 regression");
        policy.SustainedRuns.Should().Be(2, "AC: sustained over 2 nights");
        policy.RetentionDays.Should().Be(90, "AC: 90-day trends");

        policy.RuleFor("search.idle.term.p95").Gated.Should().BeTrue();
        policy.RuleFor("search.bulk.simple.p95").Gated.Should().BeTrue();
        policy.RuleFor("search.idle.term.p50").Gated.Should().BeFalse();
        policy.RuleFor("coding-to-searchable.bulk.p95").Gated.Should().BeTrue();
        policy.RuleFor("index-lag.bulk.p95").Gated.Should().BeTrue();
        policy.RuleFor("index-lag.bulk.max").Gated.Should().BeFalse();
        policy.RuleFor("stage.bulk-tag.docs-per-second").Should().Match<MetricRule>(r => r.Gated && r.IsHigherBetter);
        policy.RuleFor("stage.generate.docs-per-second").Gated.Should().BeFalse("the corpus generator is not the product");
        policy.RuleFor("stage.export.seconds").Gated.Should().BeFalse();
    }

    [Fact]
    public void A_new_series_collects_its_baseline_before_judging()
    {
        var history = Accepted(2, p95: 100);
        RegressionVerdict verdict = RegressionEvaluator.Evaluate(Run(3, p95: 500), history, Policy);

        verdict.Status.Should().Be(RunStatus.CollectingBaseline);
        verdict.Alert.Should().BeFalse();
        verdict.Comparisons.Single(c => c.Metric == "search.idle.term.p95").Baseline.Should().BeNull();
    }

    [Fact]
    public void Within_tolerance_passes_and_the_baseline_is_the_median_of_the_window()
    {
        var history = Accepted(9, p95: 100).Select((h, i) => i == 8 ? With(h, p95: 1_000) : h).ToList();
        RegressionVerdict verdict = RegressionEvaluator.Evaluate(Run(10, p95: 114), history, Policy);

        verdict.Status.Should().Be(RunStatus.Pass);
        MetricComparison term = verdict.Comparisons.Single(c => c.Metric == "search.idle.term.p95");
        term.Baseline.Should().Be(100, "the median of the last 7 accepted runs ignores one outlier");
        term.BaselineRuns.Should().Be(7);
        term.Worse.Should().BeApproximately(0.14, 1e-9);
        verdict.BaselineRunIds.Should().HaveCount(7).And.NotContain("run-01").And.NotContain("run-02");
    }

    [Fact]
    public void One_night_over_tolerance_regresses_without_alerting_and_a_second_alerts()
    {
        var history = Accepted(5, p95: 100);
        RegressionVerdict first = RegressionEvaluator.Evaluate(Run(6, p95: 120), history, Policy);

        first.Status.Should().Be(RunStatus.Regressed);
        first.Alert.Should().BeFalse("a single night on a shared runner is not yet a trend");
        first.Comparisons.Single(c => c.Regressed).Metric.Should().Be("search.idle.term.p95");
        first.Reasons.Should().ContainSingle(r => r.StartsWith("search.idle.term.p95: 120 vs baseline 100 (20.0% worse", StringComparison.Ordinal));

        var night6 = Run(6, p95: 120) with { Verdict = first.ToEntryVerdict() };
        RegressionVerdict second = RegressionEvaluator.Evaluate(Run(7, p95: 121), [.. history, night6], Policy);

        second.Status.Should().Be(RunStatus.Regressed);
        second.Alert.Should().BeTrue();
        second.Comparisons.Single(c => c.Metric == "search.idle.term.p95").Should().Match<MetricComparison>(c => c.Sustained && c.Baseline == 100,
            "the regressed night stays out of the baseline, so a lasting regression keeps alerting");
    }

    [Fact]
    public void A_regression_on_a_different_bucket_the_next_night_is_not_sustained()
    {
        var history = Accepted(5, p95: 100);
        var night6 = Run(6, p95: 130);
        night6 = night6 with { Verdict = RegressionEvaluator.Evaluate(night6, history, Policy).ToEntryVerdict() };
        HistoryEntry night7 = Run(7, p95: 100, complexP95: 300);

        RegressionVerdict verdict = RegressionEvaluator.Evaluate(night7, [.. history, night6], Policy);

        verdict.Status.Should().Be(RunStatus.Regressed);
        verdict.Alert.Should().BeFalse();
    }

    [Fact]
    public void Changes_under_the_noise_floor_never_regress()
    {
        var history = Accepted(5, p95: 10);
        RegressionVerdict verdict = RegressionEvaluator.Evaluate(Run(6, p95: 12.5), history, Policy);

        MetricComparison term = verdict.Comparisons.Single(c => c.Metric == "search.idle.term.p95");
        term.Worse.Should().BeApproximately(0.25, 1e-9);
        term.Regressed.Should().BeFalse("2.5 ms is below the 3 ms floor");
        verdict.Status.Should().Be(RunStatus.Pass);
    }

    [Fact]
    public void Throughput_regresses_when_it_drops()
    {
        var history = Accepted(5, p95: 100);
        RegressionVerdict verdict = RegressionEvaluator.Evaluate(Run(6, p95: 100, bulkDocsPerSecond: 800), history, Policy);

        verdict.Comparisons.Single(c => c.Metric == "stage.bulk-tag.docs-per-second").Should().Match<MetricComparison>(c => c.Regressed && c.Worse == 0.2);
        RegressionEvaluator.Evaluate(Run(6, p95: 100, bulkDocsPerSecond: 2_000), history, Policy).Status.Should().Be(RunStatus.Pass);
    }

    [Fact]
    public void A_correctness_counter_alerts_on_the_first_night_and_is_marked_sustained_when_it_happens_again()
    {
        var history = Accepted(5, p95: 100);
        HistoryEntry night6 = Run(6, p95: 100, staleOverwrites: 1);
        RegressionVerdict first = RegressionEvaluator.Evaluate(night6, history, Policy);

        first.Status.Should().Be(RunStatus.CorrectnessFailed);
        first.Alert.Should().BeTrue("a correctness failure is never noise");
        first.SustainedCorrectnessFailure.Should().BeFalse();
        first.CorrectnessFailures.Should().Equal("shadow-ledger.stale-overwrites");

        night6 = night6 with { Verdict = first.ToEntryVerdict() };
        RegressionVerdict second = RegressionEvaluator.Evaluate(Run(7, p95: 100, staleOverwrites: 2), [.. history, night6], Policy);

        second.Alert.Should().BeTrue();
        second.SustainedCorrectnessFailure.Should().BeTrue();
        RegressionEvaluator.Evaluate(Run(7, p95: 100), [.. history, night6], Policy).Status.Should().Be(RunStatus.Pass);
    }

    [Fact]
    public void Other_series_are_never_compared()
    {
        var history = Accepted(5, p95: 10).Select(h => h with { Series = "other-runner" }).ToList();
        RegressionVerdict verdict = RegressionEvaluator.Evaluate(Run(6, p95: 100), history, Policy);

        verdict.Status.Should().Be(RunStatus.CollectingBaseline);
        verdict.BaselineRunIds.Should().BeEmpty();
    }

    [Fact]
    public void A_rebaseline_starts_a_new_baseline_within_the_series()
    {
        var history = Accepted(5, p95: 100);
        HistoryEntry rebased = Run(6, p95: 200) with { Rebaseline = true };
        RegressionVerdict verdict = RegressionEvaluator.Evaluate(rebased, history, Policy);
        verdict.Status.Should().Be(RunStatus.CollectingBaseline, "the accepted change starts collecting again");

        var after = new List<HistoryEntry>(history) { rebased with { Verdict = verdict.ToEntryVerdict() } };
        for (int night = 7; night <= 8; night++)
        {
            HistoryEntry entry = Run(night, p95: 200);
            after.Add(entry with { Verdict = RegressionEvaluator.Evaluate(entry, after, Policy).ToEntryVerdict() });
        }

        RegressionVerdict judged = RegressionEvaluator.Evaluate(Run(9, p95: 205), after, Policy);
        judged.Status.Should().Be(RunStatus.Pass);
        judged.Comparisons.Single(c => c.Metric == "search.idle.term.p95").Baseline.Should().Be(200);
    }

    [Fact]
    public void History_keeps_90_days_and_round_trips()
    {
        string dir = SampleBundle.NewDirectory();
        try
        {
            string path = Path.Combine(dir, RunHistory.FileName);
            var entries = Accepted(3, p95: 100).Prepend(Run(-100, p95: 1) with { Verdict = new EntryVerdict { Status = RunStatus.Pass } }).ToList();
            RunHistory.Save(path, entries, Night.AddDays(3), retentionDays: 90).Should().HaveCount(3);

            IReadOnlyList<HistoryEntry> loaded = RunHistory.Load(path);
            loaded.Select(e => e.RunId).Should().Equal("run-01", "run-02", "run-03");
            loaded[0].Metrics["search.idle.term.p95"].Should().Be(100);
            loaded[0].Verdict!.Status.Should().Be(RunStatus.CollectingBaseline);
            File.ReadAllLines(path).Should().HaveCount(3).And.OnlyContain(l => l.StartsWith('{') && l.EndsWith('}'));
            RunHistory.Load(Path.Combine(dir, "missing.jsonl")).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_nightly_bundle_is_schema_valid_and_reduces_to_history_metrics()
    {
        string dir = SampleBundle.NewDirectory();
        try
        {
            string bundlePath = WriteNightlyBundle(dir);
            BundleValidator.Validate(bundlePath, new BundleValidationOptions { GatesPath = SampleBundle.RepositoryGates }).Errors.Should().BeEmpty();

            HistoryEntry entry = RunHistory.FromRun(Path.GetDirectoryName(bundlePath)!, url: "https://ci.example/run/1");
            entry.Metrics.Should().ContainKeys(
                "search.idle.term.p50", "search.idle.term.p95", "search.idle.term.p99", "search.idle.simple.p95", "search.bulk.complex.p95",
                "coding-to-searchable.bulk.p95", "index-lag.bulk.p95", "index-lag.bulk.max", "stage.bulk-tag.docs-per-second", "stage.import.seconds");
            entry.Metrics["stage.bulk-tag.docs-per-second"].Should().Be(250);
            entry.Metrics["index-lag.bulk.max"].Should().Be(4);
            entry.Counters.Should().ContainKey("import.row-errors").And.ContainKey("shadow-ledger.stale-overwrites").And.ContainKey("security.unauthorized-retrievals");
            entry.SeriesLabel.Should().Be("1000 docs, seed 7, scenario 1 on Example CPU (16 vCPU, 64 GiB)");
            entry.Url.Should().Be("https://ci.example/run/1");

            JsonNode sidecar = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(bundlePath)!, ScaleRunRecord.FileName)))!;
            sidecar["config"]!["documents"]!.GetValue<string>().Should().Be("1000");
            sidecar["stages"]!.AsArray().Should().HaveCount(2);

            // The same corpus on another runner shape is another series.
            var record = BenchJson.Deserialize<ScaleRunRecord>(sidecar.ToJsonString());
            var environment = SampleBundle.Environment();
            var otherHost = environment with { Hosts = [environment.Hosts[0] with { Cpu = environment.Hosts[0].Cpu with { LogicalCores = 4 } }] };
            RunHistory.SeriesOf(otherHost, record).Key.Should().NotBe(entry.Series);
            RunHistory.SeriesOf(environment, record with { ScenarioVersion = "2" }).Key.Should().NotBe(entry.Series);
            RunHistory.SeriesOf(environment, record).Key.Should().Be(entry.Series);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_markdown_summary_states_the_verdict_and_every_gated_metric()
    {
        var history = Accepted(5, p95: 100);
        HistoryEntry run = Run(6, p95: 120);
        RegressionVerdict verdict = RegressionEvaluator.Evaluate(run, history, Policy);
        string markdown = RegressionReport.Markdown(verdict, run with { Url = "https://ci.example/run/6" }, Policy);

        markdown.Should().StartWith("## Nightly scale regression: regressed (first night, no alert yet)");
        markdown.Should().Contain("| `search.idle.term.p95` | 120 | 100 | +20.0% | regressed |");
        markdown.Should().Contain("| `shadow-ledger.stale-overwrites` | 0 |");
        markdown.Should().Contain("tolerance 15%").And.Contain("([workflow run](https://ci.example/run/6))");
    }

    [Fact]
    public void The_dashboard_draws_90_day_trends_of_one_series()
    {
        var history = Accepted(5, p95: 100);
        HistoryEntry regressed = Run(6, p95: 130);
        history = [.. history, regressed with { Verdict = RegressionEvaluator.Evaluate(regressed, history, Policy).ToEntryVerdict() },
            Run(7, p95: 1) with { Series = "other", SeriesLabel = "<other runner>" }, Run(-200, p95: 1)];

        string html = TrendDashboard.Render(history, "series-a", Night.AddDays(6), days: 90);

        html.Should().StartWith("<!doctype html>");
        foreach (string title in new[] { "Search p95 by bucket, simple classes (idle) <span class=\"unit\">(ms)", "Coding → searchable p95 <span class=\"unit\">(ms)",
            "Search-index lag p95 <span class=\"unit\">(s)", "Throughput by stage <span class=\"unit\">(docs/s)" })
        {
            html.Should().Contain("<h2>" + title.Replace("→", System.Net.WebUtility.HtmlEncode("→"), StringComparison.Ordinal));
        }

        html.Should().Contain("6 run(s)", "the run older than 90 days is outside the window");
        html.Should().Contain("class=\"dot s1 bad\"", "the regressed night is ringed");
        html.Should().Contain("&lt;other runner&gt;", "other series are listed (escaped), not plotted");
        html.Should().Contain("<details><summary>Table</summary>");
        html.Should().NotContain("<script");
        TrendDashboard.Render([], "none", Night, 90).Should().Contain("No data yet.");
    }

    // ---- fixtures -------------------------------------------------------------------------------------------------

    private static List<HistoryEntry> Accepted(int nights, double p95)
    {
        var entries = new List<HistoryEntry>();
        for (int night = 1; night <= nights; night++)
        {
            entries.Add(Run(night, p95) with { Verdict = new EntryVerdict { Status = night <= 3 ? RunStatus.CollectingBaseline : RunStatus.Pass } });
        }

        return entries;
    }

    private static HistoryEntry With(HistoryEntry entry, double p95) =>
        entry with { Metrics = new Dictionary<string, double>(entry.Metrics) { ["search.idle.term.p95"] = p95 } };

    private static HistoryEntry Run(int night, double p95, double complexP95 = 200, double bulkDocsPerSecond = 1_000, long staleOverwrites = 0) => new()
    {
        RunId = night < 0 ? $"run-old{-night}" : $"run-{night:00}",
        StartedUtc = Night.AddDays(night - 1),
        GitSha = "0123456789abcdef0123456789abcdef01234567",
        Series = "series-a",
        SeriesLabel = "1000 docs on Example CPU",
        Metrics = new Dictionary<string, double>
        {
            ["search.idle.term.p50"] = p95 / 2,
            ["search.idle.term.p95"] = p95,
            ["search.idle.complex.p95"] = complexP95,
            ["coding-to-searchable.bulk.p95"] = 1_500,
            ["index-lag.bulk.p95"] = 2,
            ["stage.bulk-tag.docs-per-second"] = bulkDocsPerSecond,
        },
        Counters = new Dictionary<string, long> { ["import.row-errors"] = 0, ["shadow-ledger.stale-overwrites"] = staleOverwrites },
    };

    private static string WriteNightlyBundle(string dir)
    {
        var idle = new LatencyRecorder();
        var load = new LatencyRecorder();
        var probe = new LatencyRecorder();
        for (int i = 1; i <= 50; i++)
        {
            idle.Record("term", GateClass.Simple, TimeSpan.FromMilliseconds(10 + i));
            load.Record("boolean", GateClass.Complex, TimeSpan.FromMilliseconds(30 + i));
            probe.Record("coding-to-searchable", null, TimeSpan.FromMilliseconds(900 + i));
        }

        idle.Record("term", GateClass.Simple, TimeSpan.FromMilliseconds(5), succeeded: false);
        var lag = new LagSeries();
        lag.Add(SampleBundle.Start.AddMinutes(20), 4);
        lag.Add(SampleBundle.Start.AddMinutes(21), 1);
        QueryClassResult probeResult = probe.Results().Single(r => r.Name == "coding-to-searchable");

        static Scenario Scenario(string id, ScenarioRole role, int minute, IReadOnlyList<QueryClassResult> classes, Throughput throughput, IndexLag? indexLag, CodingToSearchable? toSearchable) => new()
        {
            Id = id,
            Role = role,
            StartedUtc = SampleBundle.Start.AddMinutes(minute),
            EndedUtc = SampleBundle.Start.AddMinutes(minute + 5),
            WarmupSeconds = 0,
            DrainSeconds = 0,
            Validity = new Validity { Valid = true, DroppedIterationsRatio = 0, LoadGeneratorCpuMaxPercent = 40 },
            QueryClasses = classes,
            Throughput = throughput,
            IndexLag = indexLag,
            CodingToSearchable = toSearchable,
            MetricsFiles = indexLag?.SeriesFile is { } file ? [file] : null,
        };

        string queries = Path.Combine(dir, "queries.json");
        File.WriteAllText(queries, "[]\n");
        return ScaleRunBundle.Write(new ScaleRunBundleInput
        {
            OutputDirectory = Path.Combine(dir, "out"),
            Git = new References.GitState("0123456789abcdef0123456789abcdef01234567", false, "main"),
            Operator = "ci:scale-regression",
            StartedUtc = SampleBundle.Start,
            EndedUtc = SampleBundle.Start.AddHours(1),
            Environment = SampleBundle.Environment(),
            CorpusManifestPath = SampleBundle.CorpusManifest,
            GatesPath = SampleBundle.RepositoryGates,
            WorkloadFiles = new Dictionary<string, string> { [queries] = "queries.json" },
            Texts = new Dictionary<string, string> { ["metrics/index-lag-bulk.csv"] = lag.ToCsv() },
            Scenarios =
            [
                Scenario("idle", ScenarioRole.IdleBaseline, 0, idle.Results(), new Throughput(), null, null),
                Scenario("bulk", ScenarioRole.BulkLoad, 10, load.Results(), new Throughput { BulkDocsPerSecond = 250 }, lag.Summarize("metrics/index-lag-bulk.csv"),
                    new CodingToSearchable { Samples = probeResult.Latency.Count, PollIntervalMs = 50, Latency = probeResult.Latency }),
            ],
            Oracles = Oracles.NotRun with
            {
                ShadowLedger = new ShadowLedgerOracle { Status = OracleStatus.Passed, TouchedDocs = 1000, StaleOverwrites = 0, VersionRegressions = 0, MissingDocs = 0, ValueMismatches = 0 },
                Security = new SecurityOracle { Status = OracleStatus.Passed, Checks = 11, UnauthorizedRetrievals = 0 },
            },
            Record = new ScaleRunRecord
            {
                ScenarioVersion = "1",
                Documents = 1000,
                Seed = 7,
                Config = new Dictionary<string, string> { ["documents"] = "1000" },
                Stages =
                [
                    new StageResult { Name = "import", StartedUtc = SampleBundle.Start, Seconds = 10, Documents = 1000 },
                    new StageResult { Name = "bulk-tag", StartedUtc = SampleBundle.Start.AddMinutes(10), Seconds = 2, Documents = 500 },
                ],
                Counters = new Dictionary<string, long> { ["import.row-errors"] = 0 },
            },
        });
    }
}
