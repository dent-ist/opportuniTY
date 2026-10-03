using System.Globalization;
using System.Text;

using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Infrastructure;
using Opportunity.Benchmarks.Workloads;

namespace Opportunity.Benchmarks.Tests;

/// <summary>
/// The k6 workloads in stub mode, CI-sized: each script runs for a few seconds against the fake API with STRICT=1
/// (any failed contract check fails k6), and the stub must not have seen a single contract violation.
/// </summary>
public class K6WorkloadTests
{
    private static QuerySet Set => QueryTestCorpus.QuerySet;

    private static Dictionary<string, string> Env(StubApi stub, params (string Name, string Value)[] extra)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QUERIES"] = QueryTestCorpus.QueryFile,
            ["BASE_URL"] = stub.BaseAddress.ToString().TrimEnd('/'),
            ["STRICT"] = "1",
            ["THINK_SCALE"] = "0.001",
            ["SEARCH_PRE_VUS"] = "20",
            ["SEARCH_MAX_VUS"] = "40",
            ["BULK_PRE_VUS"] = "10",
            ["K6_NO_USAGE_REPORT"] = "true",
        };
        foreach (var (name, value) in extra)
        {
            env[name] = value;
        }

        return env;
    }

    private static void ShouldBeClean(K6Result result, StubApi stub)
    {
        result.ExitCode.Should().Be(0, result.Output);
        stub.Stats.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_mix_replays_the_seeded_query_sequence()
    {
        await K6Tool.RequireAsync();
        var runs = new List<IReadOnlyList<StubSearch>>();
        for (int run = 0; run < 2; run++)
        {
            await using StubApi stub = await StubApi.StartAsync(Set, cancellationToken: TestContext.Current.CancellationToken);
            K6Result result = await K6Tool.RunAsync("search-mix.js", Env(stub, ("SEARCH_RATE", "40"), ("DURATION", "2s"), ("PHASE", "smoke")), SampleBundle.NewDirectory());

            ShouldBeClean(result, stub);
            stub.Stats.Searches.Should().HaveCountGreaterThan(40);
            foreach (StubSearch search in stub.Stats.Searches)
            {
                search.QueryId.Should().Be(Set.Queries[Set.Schedule[(int)(search.Sequence % Set.Schedule.Count)]].Id, "iteration i issues queries[schedule[i]]");
            }

            runs.Add([.. stub.Stats.Searches.OrderBy(s => s.Sequence)]);
        }

        int common = Math.Min(runs[0].Count, runs[1].Count);
        runs[0].Take(common).Should().Equal(runs[1].Take(common), "the same seed yields the same query sequence");
        runs[0].Take(common).Select(s => s.Sequence).Should().Equal(Enumerable.Range(0, common).Select(i => (long)i));
    }

    [Fact]
    public async Task Reviewer_sessions_search_open_at_most_25_documents_and_code_each_with_if_match()
    {
        await K6Tool.RequireAsync();
        await using StubApi stub = await StubApi.StartAsync(Set, cancellationToken: TestContext.Current.CancellationToken);

        K6Result result = await K6Tool.RunAsync("reviewer-sessions.js", Env(stub, ("REVIEWERS", "3"), ("DURATION", "3s")), SampleBundle.NewDirectory());

        ShouldBeClean(result, stub);
        long searches = stub.Stats.Count("search-ok"), views = stub.Stats.Count("document-view"), codings = stub.Stats.Count("coding-write");
        searches.Should().BeGreaterThan(3);
        views.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(searches * 25);
        codings.Should().BeLessThanOrEqualTo(views);
        (stub.Stats.Count("coding-ok") + stub.Stats.Count("coding-conflict")).Should().Be(codings);
    }

    [Fact]
    public async Task Bulk_driver_offers_the_configured_rate_with_unique_idempotency_keys()
    {
        await K6Tool.RequireAsync();
        await using StubApi stub = await StubApi.StartAsync(Set, cancellationToken: TestContext.Current.CancellationToken);

        // 500 docs/s in jobs of 100 docs = 5 jobs/s for 3 s.
        K6Result result = await K6Tool.RunAsync("bulk-coding.js", Env(stub, ("BULK_DOCS_PER_SEC", "500"), ("BULK_BATCH", "100"), ("DURATION", "3s")), SampleBundle.NewDirectory());

        ShouldBeClean(result, stub);
        stub.Stats.Count("bulk-submit").Should().BeInRange(13, 17);
        stub.Stats.Count("bulk-accepted").Should().Be(stub.Stats.Count("bulk-submit"));
    }

    [Fact]
    public async Task Mixed_run_ingests_into_a_schema_valid_bundle_with_HDR_histograms_per_class()
    {
        await K6Tool.RequireAsync();
        var slowComplex = new StubApiOptions { SimpleDelay = TimeSpan.FromMilliseconds(2), ComplexDelay = TimeSpan.FromMilliseconds(25) };
        await using StubApi stub = await StubApi.StartAsync(Set, slowComplex, TestContext.Current.CancellationToken);
        string run = SampleBundle.NewDirectory();
        DateTime started = DateTime.UtcNow;

        K6Result result = await K6Tool.RunAsync("mixed.js", Env(stub,
            ("SEARCH_RATE", "30"), ("REVIEWERS", "2"), ("BULK_DOCS_PER_SEC", "500"), ("BULK_BATCH", "100"), ("IDLE_DURATION", "3s"), ("BULK_DURATION", "3s")), run);

        ShouldBeClean(result, stub);
        string cpu = Path.Combine(run, "loadgen-cpu.jsonl");
        File.WriteAllText(cpu, CpuSamples(started.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1), percent: 12.5));

        string bundlePath = K6Ingest.Ingest(Options(run, result, cpu));

        string directory = Path.GetDirectoryName(bundlePath)!;
        BundleValidationReport report = BundleValidator.Validate(directory, new BundleValidationOptions { GatesPath = SampleBundle.RepositoryGates });
        report.Errors.Should().BeEmpty();
        ResultBundle bundle = BenchJson.Deserialize<ResultBundle>(File.ReadAllText(bundlePath));
        bundle.Scenarios.Select(s => s.Role).Should().Equal(ScenarioRole.IdleBaseline, ScenarioRole.BulkLoad);
        foreach (Scenario scenario in bundle.Scenarios)
        {
            // Dropped iterations depend on how loaded the test machine is; everything else about validity must hold.
            scenario.Validity.Reasons.Should().OnlyContain(r => r.Contains("dropped iterations", StringComparison.Ordinal));
            scenario.Validity.LoadGeneratorCpuMaxPercent.Should().Be(12.5);
            QueryClassResult simple = scenario.QueryClasses.Single(c => c.Name == "simple");
            QueryClassResult complex = scenario.QueryClasses.Single(c => c.Name == "complex");
            simple.Gated.Should().BeTrue();
            complex.Gated.Should().BeTrue();
            long perBucket = scenario.QueryClasses.Where(c => QueryTaxonomy.Classes.Any(t => t.Id == c.Name)).Sum(c => c.Latency.Count);
            (simple.Latency.Count + complex.Latency.Count).Should().Be(perBucket).And.BeGreaterThan(0);
            complex.Latency.Min.Should().BeGreaterThan(15_000, "the stub delays complex searches by 25 ms, so no simple search was counted as complex");
            scenario.QueryClasses.Should().Contain(c => c.Name == "document-view").And.Contain(c => c.Name == "coding-write");
        }

        // Every search the stub answered is in exactly one gated class, by the gate of the query it carried.
        var gateOf = Set.Queries.ToDictionary(q => q.Id, q => q.Gate);
        foreach (string gate in new[] { "simple", "complex" })
        {
            bundle.Scenarios.Sum(s => s.QueryClasses.Single(c => c.Name == gate).Requests)
                .Should().Be(stub.Stats.Searches.Count(x => gateOf[x.QueryId] == gate));
        }

        bundle.Scenarios[1].QueryClasses.Should().Contain(c => c.Name == "bulk-submit");
        bundle.Scenarios[1].OfferedBulkDocsPerSecond.Should().Be(500);
        bundle.Scenarios[0].QueryClasses.Should().NotContain(c => c.Name == "bulk-submit", "no bulk load in the idle baseline");
        bundle.Workload.Scripts.Select(s => s.Path).Should().Contain(["workload/queries.json", "workload/k6/mixed.js", "workload/k6/lib/api.js"]);
        bundle.Workload.TaxonomyVersion.Should().Be(QueryTaxonomy.Version);
        bundle.Workload.Reviewers!.ThinkTimeMedianSeconds.Should().Be(20);

        // Without load-generator CPU samples a scenario cannot be shown valid.
        string unsampled = K6Ingest.Ingest(Options(run, result, cpu: null) with { OutputDirectory = SampleBundle.NewDirectory() });
        BenchJson.Deserialize<ResultBundle>(File.ReadAllText(unsampled)).Scenarios.Should().OnlyContain(s => !s.Validity.Valid && s.Validity.Reasons.Any(r => r.Contains("not sampled", StringComparison.Ordinal)));
    }

    private static K6IngestOptions Options(string run, K6Result result, string? cpu)
    {
        string environment = Path.Combine(run, "environment.json");
        File.WriteAllText(environment, BenchJson.Serialize(SampleBundle.Environment()));
        return new K6IngestOptions
        {
            RawPath = result.RawPath,
            SummaryPath = result.SummaryPath,
            LoadGeneratorCpuPath = cpu,
            QuerySetPath = QueryTestCorpus.QueryFile,
            ScriptsDirectory = K6Tool.ScriptsDirectory,
            EnvironmentPath = environment,
            CorpusManifestPath = QueryTestCorpus.Manifest,
            GatesPath = SampleBundle.RepositoryGates,
            OutputDirectory = Path.Combine(run, "bundles"),
            Tier = "T2",
            Suite = "stub/mixed",
            Operator = "ci:test",
            Git = new References.GitState(new string('b', 40), false, "main"),
            WorkloadName = "mixed",
            OfferedQueriesPerSecond = 30,
            OfferedBulkDocsPerSecond = 500,
            Reviewers = 2,
        };
    }

    private static string CpuSamples(DateTime from, DateTime to, double percent)
    {
        var text = new StringBuilder();
        for (DateTime t = from; t <= to; t = t.AddMilliseconds(250))
        {
            text.Append(CultureInfo.InvariantCulture, $"{{\"time\":\"{t:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\",\"cpuPercent\":{percent}}}\n");
        }

        return text.ToString();
    }
}
