using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Gates;

namespace Opportunity.Benchmarks.Tests;

public class GatesFileTests
{
    private static readonly GatesFile Gates = GatesFile.Load(SampleBundle.RepositoryGates);

    private static JsonNode BundleSchema => JsonNode.Parse(BundleSchemas.ReadResource(BundleSchemas.ResultBundleResource))!;

    [Fact]
    public void Repository_gates_file_parses_and_passes_its_structural_rules()
    {
        Gates.Check().Should().BeEmpty();
        Gates.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Gates_stay_draft_until_signed_off()
    {
        // Q-04: frozen only by PO + lead architect sign-off. Flip this test together with the sign-off.
        Gates.Document.Status.Should().Be("draft");
        Gates.IsFrozen.Should().BeFalse();
        Gates.Document.SignOff.ProductOwner.Should().BeNull();
        Gates.Document.SignOff.LeadArchitect.Should().BeNull();
    }

    [Fact]
    public void Every_gate_path_is_declared_by_the_result_bundle_schema()
    {
        Gates.CheckAgainstSchema(BundleSchema).Should().BeEmpty();
    }

    [Fact]
    public void Every_gate_path_selects_a_value_in_a_schema_valid_bundle()
    {
        JsonNode bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(SampleBundle.Write(), ResultBundle.FileName)))!;

        BundleSchemas.ValidateBundle(bundle).Should().BeEmpty();
        Gates.CheckAgainstBundle(bundle).Should().BeEmpty();
    }

    [Fact]
    public void Schema_check_catches_typos_in_paths_and_selector_values()
    {
        SchemaPaths.Explain(BundleSchema, "scenarios[role=bulk-laod].indexLag.maxSeconds").Should().Contain("not an allowed role");
        SchemaPaths.Explain(BundleSchema, "scenarios[role=bulk-load].indexLag.maxSecs").Should().Contain("maxSecs");
        SchemaPaths.Explain(BundleSchema, "oracles.shadowLedger[status=passed].staleOverwrites").Should().Contain("not an array");
        SchemaPaths.Explain(BundleSchema, "oracles.shadowLedger.staleOverwrites").Should().BeNull();
    }

    [Fact]
    public void Thresholds_encode_the_section_26_gates()
    {
        GateDefinition Gate(string id) => Gates.Document.Gates.Single(g => g.Id == id);

        Gate("simple-search-p95-degradation").Threshold!.Max.Should().Be(0.25);
        Gate("complex-search-p95-degradation").Threshold!.Max.Should().Be(0.35);
        Gate("index-lag-max").Threshold!.Max.Should().Be(120);
        Gate("coding-to-searchable-p95").Threshold!.Max.Should().Be(1_000_000);
        Gate("coding-to-searchable-p95").Unit.Should().Be("us");
        Gate("stale-version-overwrites").Threshold!.Max.Should().Be(0);
        Gate("unauthorized-retrievals").Threshold!.Max.Should().Be(0);
        Gate("worker-retry-idempotency").Threshold!.Min.Should().Be(1.0);
        Gate("bulk-throughput-ceiling").Target.Should().Be(10_000);
        Gate("bulk-throughput-ceiling").Enforcement.Should().Be("informational");
    }

    [Fact]
    public void Policy_encodes_Q04_and_Q44()
    {
        GatesPolicy policy = Gates.Document.Policy;

        policy.TightenOnly.Should().BeTrue();
        policy.CorrectnessBeforePerformance.Should().BeTrue();
        policy.Repetitions.Minimum.Should().Be(3);
        policy.MaterialAdvantage.P95ImprovementMin.Should().Be(0.20);
        policy.MaterialAdvantage.BulkThroughputRatioMin.Should().Be(1.5);
        policy.MaterialFailureAt10M.P95GrowthFrom1MMax.Should().Be(0.50);
        policy.Validity.MaxDroppedIterationsRatio.Should().Be(0.005);
        policy.Validity.MaxLoadGeneratorCpuPercent.Should().Be(70);
        policy.AbsoluteGatesByProfile.Should().Equal(new Dictionary<string, string>
        {
            ["developer-regression"] = "comparative",
            ["enterprise-reference"] = "hard",
        });

        // Absolute latency gates follow the profile (Q-44); correctness/security are hard everywhere.
        Gates.Document.Gates.Where(g => g.Id is "index-lag-max" or "coding-to-searchable-p95").Should().OnlyContain(g => g.Enforcement == "by-profile");
        Gates.Document.Gates.Where(g => g.Category is "correctness" or "security").Should().HaveCount(3).And.OnlyContain(g => g.Enforcement == "hard");
    }

    [Fact]
    public void Frozen_gates_need_both_sign_offs()
    {
        string path = Path.Combine(SampleBundle.NewDirectory(), "gates.yaml");
        File.WriteAllText(path, File.ReadAllText(SampleBundle.RepositoryGates).Replace("status: draft", "status: frozen", StringComparison.Ordinal));

        GatesFile frozen = GatesFile.Load(path);

        frozen.IsFrozen.Should().BeTrue();
        frozen.Check().Should().ContainSingle(e => e.Contains("signOff", StringComparison.Ordinal));
        frozen.ToReference().Status.Should().Be(GatesStatus.Frozen);
    }

    [Fact]
    public void Unknown_keys_are_rejected()
    {
        string path = Path.Combine(SampleBundle.NewDirectory(), "gates.yaml");
        File.WriteAllText(path, File.ReadAllText(SampleBundle.RepositoryGates) + "\nunexpected: true\n");

        Action load = () => GatesFile.Load(path);

        load.Should().Throw<FormatException>();
    }
}
