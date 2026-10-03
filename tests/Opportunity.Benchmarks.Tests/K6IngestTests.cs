using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Infrastructure;
using Opportunity.Benchmarks.Workloads;

namespace Opportunity.Benchmarks.Tests;

/// <summary>ingest-k6 on a hand-written k6 JSON output, so every count, percentile and validity verdict is known.</summary>
public class K6IngestTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Phases_become_scenarios_with_HDR_histograms_per_class_and_validity_from_dropped_iterations_and_cpu()
    {
        string dir = SampleBundle.NewDirectory();
        string raw = WriteRaw(dir);
        string cpu = Path.Combine(dir, "cpu.jsonl");
        File.WriteAllText(cpu, Cpu(t => t < 5 ? 20 : 40));

        ResultBundle bundle = Ingest(dir, raw, cpu);

        bundle.Scenarios.Select(s => (s.Id, s.Role)).Should().Equal(("idle", ScenarioRole.IdleBaseline), ("bulk", ScenarioRole.BulkLoad));
        Scenario idle = bundle.Scenarios[0], bulk = bundle.Scenarios[1];

        QueryClassResult simple = idle.QueryClasses.Single(c => c.Name == "simple");
        simple.Gated.Should().BeTrue();
        simple.Requests.Should().Be(102, "100 measured + 1 error + 1 in the warm-up second");
        simple.Errors.Should().Be(1);
        simple.Latency.Count.Should().Be(100, "failed requests and warm-up samples are not latency samples");
        simple.Latency.P50.Should().BeCloseTo(10_000, 10);
        idle.QueryClasses.Single(c => c.Name == "term").Latency.Count.Should().Be(100);
        QueryClassResult complex = idle.QueryClasses.Single(c => c.Name == "complex");
        complex.Latency.P99.Should().BeCloseTo(30_000, 30);
        idle.QueryClasses.Single(c => c.Name == "boolean").Gated.Should().BeFalse();
        idle.QueryClasses.Select(c => c.Name).Should().StartWith(["simple", "complex"]);

        // 2 dropped of 202 attempts: 0.99% > 0.5% -> invalid; dropped_iterations carries only the scenario tag.
        idle.Validity.DroppedIterationsRatio.Should().Be(Math.Round(2.0 / 202, 6));
        idle.Validity.Valid.Should().BeFalse();
        idle.Validity.Reasons.Should().ContainSingle().Which.Should().Contain("dropped iterations");
        idle.Validity.LoadGeneratorCpuMaxPercent.Should().Be(20);

        // 1 of 1,001 dropped (reviewer iterations without a phase tag are placed by time) -> valid.
        bulk.Validity.DroppedIterationsRatio.Should().Be(Math.Round(1.0 / 1_011, 6));
        bulk.Validity.Valid.Should().BeTrue(string.Join("; ", bulk.Validity.Reasons));
        QueryClassResult coding = bulk.QueryClasses.Single(c => c.Name == "coding-write");
        (coding.Requests, coding.Errors, coding.Latency.Count).Should().Be((2L, 0L, 1L), "a 412 conflict is neither an error nor a latency sample");
        bulk.QueryClasses.Single(c => c.Name == "bulk-submit").Requests.Should().Be(3);
        bulk.OfferedBulkDocsPerSecond.Should().Be(2_000);
        idle.OfferedBulkDocsPerSecond.Should().BeNull();
        bulk.Throughput.QueriesPerSecond.Should().Be(40, "100 searches in the 2.5 s bulk window");
    }

    [Fact]
    public void Load_generator_cpu_at_or_above_70_percent_invalidates_the_scenario()
    {
        string dir = SampleBundle.NewDirectory();
        string raw = WriteRaw(dir);
        string cpu = Path.Combine(dir, "cpu.jsonl");
        File.WriteAllText(cpu, Cpu(t => t < 5 ? 20 : 70));

        Scenario bulk = Ingest(dir, raw, cpu).Scenarios[1];

        bulk.Validity.Valid.Should().BeFalse();
        bulk.Validity.LoadGeneratorCpuMaxPercent.Should().Be(70);
        bulk.Validity.Reasons.Should().ContainSingle().Which.Should().Contain("load-generator CPU");
    }

    [Fact]
    public void A_query_set_from_another_corpus_is_refused()
    {
        string dir = SampleBundle.NewDirectory();
        string raw = WriteRaw(dir);

        Action ingest = () => Ingest(dir, raw, null, SampleBundle.CorpusManifest);

        ingest.Should().Throw<InvalidOperationException>().WithMessage("*was generated from corpus manifest*");
    }

    private static ResultBundle Ingest(string dir, string raw, string? cpu, string? corpusManifest = null)
    {
        string environment = Path.Combine(dir, "environment.json");
        File.WriteAllText(environment, BenchJson.Serialize(SampleBundle.Environment()));
        string bundlePath = K6Ingest.Ingest(new K6IngestOptions
        {
            RawPath = raw,
            LoadGeneratorCpuPath = cpu,
            QuerySetPath = QueryTestCorpus.QueryFile,
            ScriptsDirectory = K6Tool.ScriptsDirectory,
            EnvironmentPath = environment,
            CorpusManifestPath = corpusManifest ?? QueryTestCorpus.Manifest,
            GatesPath = SampleBundle.RepositoryGates,
            OutputDirectory = Path.Combine(dir, "bundles"),
            Tier = "T2",
            Suite = "nightly/regression",
            Operator = "ci:test",
            Git = new References.GitState(new string('c', 40), false, "main"),
            WarmupSeconds = 1,
            OfferedBulkDocsPerSecond = 2_000,
            Reviewers = 100,
        });
        BundleValidator.Validate(Path.GetDirectoryName(bundlePath)!).Errors.Should().BeEmpty();
        return BenchJson.Deserialize<ResultBundle>(File.ReadAllText(bundlePath));
    }

    private static string WriteRaw(string dir)
    {
        var lines = new StringBuilder();
        void Point(string metric, double seconds, double value, params (string Key, string Value)[] tags)
        {
            var tagObject = tags.ToDictionary(t => t.Key, t => t.Value);
            lines.Append(JsonSerializer.Serialize(new
            {
                metric,
                type = "Point",
                data = new { time = T0.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), value, tags = tagObject },
            })).Append('\n');
        }

        (string, string)[] Search(string phase, string scenario, string qclass, string gate, string outcome) =>
            [("op", "search"), ("qclass", qclass), ("gate", gate), ("bucket", gate == "simple" ? "simple" : "boolean"), ("phase", phase), ("scenario", scenario), ("source", "open"), ("outcome", outcome)];

        lines.Append("{\"type\":\"Metric\",\"data\":{\"name\":\"opp_request\",\"type\":\"trend\",\"contains\":\"time\"},\"metric\":\"opp_request\"}\n");
        Point("opp_request", 0, 99, Search("idle", "search_idle", "term", "simple", "ok"));
        for (int i = 0; i < 100; i++)
        {
            Point("opp_request", 1 + (i * 0.02), 10, Search("idle", "search_idle", "term", "simple", "ok"));
        }

        Point("opp_request", 2.5, 500, Search("idle", "search_idle", "term", "simple", "error"));
        for (int i = 0; i < 50; i++)
        {
            Point("opp_request", 1 + (i * 0.04), 30, Search("idle", "search_idle", "boolean", "complex", "ok"));
        }

        Point("iterations", 3, 200, ("phase", "idle"), ("scenario", "search_idle"));
        Point("dropped_iterations", 3, 2, ("scenario", "search_idle"));

        for (int i = 0; i < 100; i++)
        {
            Point("opp_request", 10 + (i * 0.02), 12, Search("bulk", "search_bulk", "term", "simple", "ok"));
        }

        Point("opp_request", 11, 8, ("op", "coding-write"), ("phase", "bulk"), ("scenario", "reviewers"), ("source", "session"), ("outcome", "ok"));
        Point("opp_request", 11.5, 9, ("op", "coding-write"), ("phase", "bulk"), ("scenario", "reviewers"), ("source", "session"), ("outcome", "conflict"));
        for (int i = 0; i < 3; i++)
        {
            Point("opp_request", 10.5 + i, 4, ("op", "bulk-submit"), ("phase", "bulk"), ("scenario", "bulk"), ("source", "bulk"), ("outcome", "ok"));
        }

        Point("iterations", 11.9, 1_000, ("phase", "bulk"), ("scenario", "search_bulk"));
        Point("iterations", 11.95, 10, ("scenario", "reviewers"));
        Point("dropped_iterations", 12, 1, ("scenario", "search_bulk"));

        string path = Path.Combine(dir, "k6-raw.json.gz");
        using (var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            gz.Write(Encoding.UTF8.GetBytes(lines.ToString()));
        }

        return path;
    }

    private static string Cpu(Func<double, double> percentAt)
    {
        var text = new StringBuilder();
        for (double t = 0; t <= 13; t += 0.5)
        {
            text.Append(CultureInfo.InvariantCulture, $"{{\"time\":\"{T0.AddSeconds(t):yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\",\"cpuPercent\":{percentAt(t)}}}\n");
        }

        return text.ToString();
    }
}
