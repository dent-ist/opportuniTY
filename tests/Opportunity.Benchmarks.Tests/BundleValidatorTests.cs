using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Tests;

public class BundleValidatorTests
{
    [Fact]
    public void Tampered_bundled_file_is_detected()
    {
        string dir = SampleBundle.Write();
        File.AppendAllText(Path.Combine(dir, "workload", "search-mix.js"), "// edited after the run\n");

        BundleValidator.Validate(dir).Errors.Should().Contain(e => e.Contains("workload/search-mix.js does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Percentiles_must_agree_with_the_raw_histogram()
    {
        string dir = SampleBundle.Write();
        Rewrite(dir, b => b["scenarios"]![1]!["queryClasses"]![0]!["latency"]!["p95"] = 1);

        BundleValidator.Validate(dir).Errors.Should().Contain(e => e.Contains("summary disagrees with its HDR histogram", StringComparison.Ordinal));
    }

    [Fact]
    public void Undeclared_relaxed_durability_is_rejected()
    {
        // Q-05: settings say fsync=off, the manifest claims production.
        Action write = () => SampleBundle.Write(b => b with
        {
            Environment = b.Environment with
            {
                Postgres = [b.Environment.Postgres![0] with { Durability = b.Environment.Postgres[0].Durability with { Fsync = "off" } }],
            },
        });

        write.Should().Throw<BundleRejectedException>().Which.Errors.Should().Contain(e => e.Contains("not declared (Q-05)", StringComparison.Ordinal));
    }

    [Fact]
    public void Declared_relaxed_durability_is_accepted_on_the_developer_profile_only()
    {
        DurabilityDeviation fsync = new("postgres", "primary", "fsync", "off", "on");
        ResultBundle Relax(ResultBundle b, BenchmarkProfile profile) => b with
        {
            Environment = b.Environment with
            {
                Profile = profile,
                Postgres = [b.Environment.Postgres![0] with { Durability = b.Environment.Postgres[0].Durability with { Fsync = "off" } }],
                Durability = new DurabilityInfo { Mode = DurabilityMode.Relaxed, Deviations = [fsync], Justification = "nightly 100K regression on a laptop" },
            },
        };

        Action developer = () => SampleBundle.Write(b => Relax(b, BenchmarkProfile.DeveloperRegression));
        Action reference = () => SampleBundle.Write(b => Relax(b, BenchmarkProfile.EnterpriseReference));

        developer.Should().NotThrow();
        reference.Should().Throw<BundleRejectedException>().Which.Errors.Should().Contain(e => e.Contains("requires production durability", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("T1")]
    [InlineData("T3")]
    public void Relaxed_durability_is_rejected_outside_the_nightly_developer_tier_Q05(string tier)
    {
        // Q-05: relaxed settings only on the nightly developer tier (T2); the T3 1M comparative spike runs on developer
        // hardware but needs production-like durability.
        DurabilityDeviation fsync = new("postgres", "primary", "fsync", "off", "on");
        ResultBundle Relax(ResultBundle b, string t) => b with
        {
            Run = b.Run with { Tier = t },
            Environment = b.Environment with
            {
                Postgres = [b.Environment.Postgres![0] with { Durability = b.Environment.Postgres[0].Durability with { Fsync = "off" } }],
                Durability = new DurabilityInfo { Mode = DurabilityMode.Relaxed, Deviations = [fsync], Justification = "fsync off to save time" },
            },
        };

        Action other = () => SampleBundle.Write(b => Relax(b, tier));
        Action nightly = () => SampleBundle.Write(b => Relax(b, "T2"));

        other.Should().Throw<BundleRejectedException>().Which.Errors.Should().Contain(e => e.Contains($"tier {tier} requires production-like durability", StringComparison.Ordinal));
        nightly.Should().NotThrow();
    }

    [Fact]
    public void Comparative_tiers_need_frozen_gates_a_clean_tree_and_three_repetitions()
    {
        Action write = () => SampleBundle.Write(b => b with { Run = b.Run with { Tier = "T3", GitDirty = true, Repetition = new Repetition(1, 1) } });

        write.Should().Throw<BundleRejectedException>().Which.Errors.Should()
            .Contain(e => e.Contains("frozen", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("clean checkout", StringComparison.Ordinal))
            .And.Contain(e => e.Contains(">= 3 repetitions", StringComparison.Ordinal));
    }

    [Fact]
    public void Enterprise_reference_needs_the_reference_topology()
    {
        Action write = () => SampleBundle.Write(b => b with { Environment = b.Environment with { Profile = BenchmarkProfile.EnterpriseReference } });

        write.Should().Throw<BundleRejectedException>().Which.Errors.Should()
            .Contain(e => e.Contains("PostgreSQL replica", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("3-node OpenSearch", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("index.number_of_replicas", StringComparison.Ordinal));
    }

    [Fact]
    public void Scenario_marked_valid_beyond_the_load_generator_limits_is_rejected()
    {
        Action write = () => SampleBundle.Write(b => b with
        {
            Scenarios = [.. b.Scenarios.Select(s => s with { Validity = s.Validity with { DroppedIterationsRatio = 0.02 } })],
        });

        write.Should().Throw<BundleRejectedException>().Which.Errors.Should().Contain(e => e.Contains("dropped iterations", StringComparison.Ordinal));
    }

    [Fact]
    public void Oracle_status_must_agree_with_its_counters()
    {
        Action write = () => SampleBundle.Write(b => b with
        {
            Oracles = b.Oracles with { ShadowLedger = b.Oracles.ShadowLedger with { StaleOverwrites = 1 } },
        });

        write.Should().Throw<BundleRejectedException>().Which.Errors.Should().Contain(e => e.Contains("oracles.shadowLedger", StringComparison.Ordinal));
    }

    [Fact]
    public void Corpus_reference_must_match_the_bundled_corpus_manifest()
    {
        Action write = () => SampleBundle.Write(b => b with { Corpus = b.Corpus with { Seed = b.Corpus.Seed + 1 } });

        write.Should().Throw<BundleRejectedException>().Which.Errors.Should().Contain(e => e.StartsWith("corpus:", StringComparison.Ordinal));
    }

    [Fact]
    public void Bundle_run_against_other_gates_is_rejected_when_checked_against_the_repository_file()
    {
        string other = Path.Combine(SampleBundle.NewDirectory(), "gates.yaml");
        File.WriteAllText(other, File.ReadAllText(SampleBundle.RepositoryGates).Replace("max: 0.25", "max: 0.20", StringComparison.Ordinal));

        BundleValidator.Validate(SampleBundle.Write(), new BundleValidationOptions { GatesPath = other })
            .Errors.Should().ContainSingle(e => e.StartsWith("gates: the bundle ran against gates", StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_rejects_an_incomplete_bundle_and_copies_nothing()
    {
        string dir = SampleBundle.Write();
        Rewrite(dir, b => b["environment"]!.AsObject().Remove("postgres"));
        string destination = SampleBundle.NewDirectory();

        Action publish = () => BundlePublisher.Publish(dir, destination);

        publish.Should().Throw<BundleRejectedException>();
        Directory.EnumerateFileSystemEntries(destination).Should().BeEmpty();
    }

    [Fact]
    public void Publish_is_write_once_and_indexes_the_run()
    {
        string dir = SampleBundle.Write();
        string destination = SampleBundle.NewDirectory();

        PublishedRun first = BundlePublisher.Publish(dir, destination);
        PublishedRun again = BundlePublisher.Publish(dir, destination);

        again.Should().Be(first);
        first.Location.Should().Be($"developer-regression/{first.RunId}");
        string published = Path.Combine(destination, first.Location);
        BundleValidator.Validate(published).IsValid.Should().BeTrue();
        File.ReadAllLines(Path.Combine(destination, BundlePublisher.IndexFile)).Should().ContainSingle()
            .Which.Should().Contain(first.BundleSha256);

        // Same run id, different content: refused.
        Rewrite(dir, b => b["run"]!["notes"] = "changed");
        Action conflict = () => BundlePublisher.Publish(dir, destination);
        conflict.Should().Throw<BundleRejectedException>().WithMessage("*immutable*");
    }

    [Fact]
    public void Writer_refuses_paths_outside_the_bundle()
    {
        var writer = new BundleWriter(SampleBundle.NewDirectory());

        Action escape = () => writer.AddText("../outside.txt", "x");

        escape.Should().Throw<ArgumentException>();
    }

    private static void Rewrite(string dir, Action<JsonNode> change)
    {
        string path = Path.Combine(dir, ResultBundle.FileName);
        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        change(node);
        File.WriteAllText(path, node.ToJsonString(BenchJson.Options));
    }
}
