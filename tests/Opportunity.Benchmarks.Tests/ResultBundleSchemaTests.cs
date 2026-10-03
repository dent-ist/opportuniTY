using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Tests;

public class ResultBundleSchemaTests
{
    [Fact]
    public void Sample_bundle_validates_against_the_schema_and_bundle_rules()
    {
        string dir = SampleBundle.Write();

        BundleValidationReport report = BundleValidator.Validate(dir, new BundleValidationOptions { GatesPath = SampleBundle.RepositoryGates });

        report.Errors.Should().BeEmpty();
        report.Warnings.Should().BeEmpty();
        JsonNode bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ResultBundle.FileName)))!;
        BundleSchemas.ValidateBundle(bundle).Should().BeEmpty();
    }

    [Fact]
    public void Committed_example_bundle_validates_against_the_schema()
    {
        string example = Path.Combine(Path.GetDirectoryName(Gates.GatesFile.Locate())!, "schema", "examples", "bundle.example.json");

        BundleSchemas.ValidateBundle(JsonNode.Parse(File.ReadAllText(example))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("environment.postgres")]
    [InlineData("environment.opensearch")]
    [InlineData("environment.rabbitmq")]
    [InlineData("environment.workers")]
    [InlineData("environment.hosts")]
    [InlineData("corpus")]
    [InlineData("corpus.seed")]
    [InlineData("corpus.profileHash")]
    [InlineData("workload.scripts")]
    [InlineData("gates.sha256")]
    [InlineData("run.gitSha")]
    [InlineData("run.operator")]
    [InlineData("oracles.security")]
    public void A_bundle_without_a_complete_manifest_is_rejected(string path)
    {
        JsonNode bundle = SampleNode();
        string[] parts = path.Split('.');
        JsonObject parent = parts[..^1].Aggregate(bundle.AsObject(), (node, part) => node[part]!.AsObject());
        parent.Remove(parts[^1]).Should().BeTrue();

        BundleSchemas.ValidateBundle(bundle).Should().NotBeEmpty();
    }

    [Fact]
    public void Relaxed_durability_requires_declared_deviations_and_a_justification()
    {
        JsonNode bundle = SampleNode();
        bundle["environment"]!["durability"]!["mode"] = "relaxed";

        BundleSchemas.ValidateBundle(bundle).Should().NotBeEmpty("relaxed needs deviations and a justification (Q-05)");
    }

    [Fact]
    public void An_oracle_that_ran_must_report_its_counters()
    {
        JsonNode bundle = SampleNode();
        bundle["oracles"]!["shadowLedger"] = new JsonObject { ["status"] = "passed" };

        BundleSchemas.ValidateBundle(bundle).Should().Contain(e => e.Contains("staleOverwrites", StringComparison.Ordinal));
    }

    [Fact]
    public void Oracles_may_be_marked_not_run_but_never_silently_omitted()
    {
        JsonNode bundle = SampleNode();
        bundle["oracles"]!["shadowLedger"] = new JsonObject { ["status"] = "not-run" };

        BundleSchemas.ValidateBundle(bundle).Should().BeEmpty();
    }

    [Theory]
    [InlineData("runId", "Has Spaces")]
    [InlineData("run.tier", "T9")]
    [InlineData("run.startedUtc", "2026-10-03T12:00:00+02:00")]
    [InlineData("corpus.manifestFile", "../escape.json")]
    [InlineData("scenarios.0.role", "bulk")]
    public void Malformed_values_are_rejected(string path, string value)
    {
        JsonNode bundle = SampleNode();
        string[] parts = path.Split('.');
        JsonNode parent = parts[..^1].Aggregate(bundle, (node, part) => int.TryParse(part, out int i) ? node[i]! : node[part]!);
        parent[parts[^1]] = value;

        BundleSchemas.ValidateBundle(bundle).Should().NotBeEmpty();
    }

    [Fact]
    public void Environment_manifest_written_by_the_model_matches_its_own_schema()
    {
        EnvironmentManifest manifest = SampleBundle.Environment();

        BundleSchemas.ValidateEnvironment(BundleSchemas.ToNode(manifest)).Should().BeEmpty();
    }

    [Fact]
    public void Bundle_round_trips_through_the_model()
    {
        string dir = SampleBundle.Write();
        string json = File.ReadAllText(Path.Combine(dir, ResultBundle.FileName));

        ResultBundle bundle = BenchJson.Deserialize<ResultBundle>(json);

        BenchJson.Serialize(bundle).Should().Be(json);
        bundle.Environment.Profile.Should().Be(BenchmarkProfile.DeveloperRegression);
        json.Should().Contain("\"profile\": \"developer-regression\"").And.Contain("\"role\": \"idle-baseline\"").And.Contain("\"loadPath\": \"fast-path\"");
    }

    private static JsonNode SampleNode() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(SampleBundle.Write(), ResultBundle.FileName)))!;
}
