using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Infrastructure;
using Opportunity.Core.Fields;
using Opportunity.Correctness.ShadowLedger;

namespace Opportunity.Benchmarks.Tests;

/// <summary>
/// E17-T07: verdict.json validates against its schema, and its <c>shadowLedger</c> object is exactly the result bundle's
/// <c>oracles.shadowLedger</c>; the value normalizer compares PostgreSQL and indexed coding values independently of the
/// product's projection builder.
/// </summary>
public class ShadowLedgerVerdictTests
{
    [Fact]
    public void A_verdict_validates_and_its_counters_drop_into_a_result_bundle_unchanged()
    {
        var verdict = Verdict(stale: 1);
        var json = JsonNode.Parse(verdict.ToJson())!;

        BundleSchemas.ValidateShadowLedgerVerdict(json).Should().BeEmpty();
        json["shadowLedger"]!["status"]!.GetValue<string>().Should().Be("failed");
        json["findings"]![0]!["kind"]!.GetValue<string>().Should().Be("external-version-mismatch");

        string dir = SampleBundle.Write(bundle => bundle with
        {
            Oracles = bundle.Oracles with
            {
                ShadowLedger = BenchJson.Deserialize<ShadowLedgerOracle>(json["shadowLedger"]!.ToJsonString())!,
            },
        });
        var bundleJson = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ResultBundle.FileName)))!;
        BundleSchemas.ValidateBundle(bundleJson).Should().BeEmpty();
        JsonNode.DeepEquals(bundleJson["oracles"]!["shadowLedger"], json["shadowLedger"]).Should().BeTrue();
    }

    [Fact]
    public void A_verdict_missing_a_counter_is_rejected()
    {
        var json = JsonNode.Parse(Verdict(stale: 0).ToJson())!;
        json["shadowLedger"]!.AsObject().Remove("missingDocs");

        BundleSchemas.ValidateShadowLedgerVerdict(json).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(FieldType.Boolean, "true", "true")]
    [InlineData(FieldType.Boolean, "false", "\"false\"")]
    [InlineData(FieldType.Integer, "42", "\"42\"")]
    [InlineData(FieldType.Decimal, "1.50", "1.5")]
    [InlineData(FieldType.Text, "[\"a\",\"b\"]", "[\"a\",\"b\"]")]
    [InlineData(FieldType.Keyword, "\"x\"", "\"x\"")]
    public void Equal_values_compare_equal_across_slot_and_overflow_forms(FieldType type, string postgres, string indexed)
    {
        var field = new CodingFieldInfo(1001, type, "kw.1", true, false, null);

        CodingValues.Equal(CodingValues.FromPostgres(field, JsonNode.Parse(postgres), null), CodingValues.FromIndex(field, JsonNode.Parse(indexed)))
            .Should().BeTrue();
    }

    [Fact]
    public void Dates_and_choices_are_compared_by_meaning()
    {
        var date = new CodingFieldInfo(1002, FieldType.Date, "dt.1", true, false, DatePrecision.Date);
        CodingValues.Equal(CodingValues.FromPostgres(date, JsonValue.Create("2026-10-05"), null),
            CodingValues.FromIndex(date, JsonValue.Create("2026-10-05T00:00:00Z"))).Should().BeTrue();

        var choices = new CodingFieldInfo(1003, FieldType.MultiChoice, "ch.1", true, false, null);
        CodingValues.Equal(CodingValues.FromPostgres(choices, null, [7, 3]), CodingValues.FromIndex(choices, JsonNode.Parse("[\"7\",\"3\"]")))
            .Should().BeTrue();
        CodingValues.Equal(CodingValues.FromPostgres(choices, null, [7]), CodingValues.FromIndex(choices, JsonNode.Parse("[\"7\",\"3\"]")))
            .Should().BeFalse();
        CodingValues.Equal(CodingValues.FromPostgres(choices, null, null), CodingValues.FromIndex(choices, null)).Should().BeTrue();
    }

    [Fact]
    public void The_candidate_A_layout_reads_slots_and_overflow_and_unknown_candidates_are_refused()
    {
        var source = JsonNode.Parse("""{"coding":{"bool":{"1":true}},"codingOverflow":{"f1005":"abc"}}""")!.AsObject();
        var layout = ProjectionLayouts.For(null);

        layout.CodingValue(source, new CodingFieldInfo(1004, FieldType.Boolean, "bool.1", true, false, null))!.GetValue<bool>().Should().BeTrue();
        layout.CodingValue(source, new CodingFieldInfo(1005, FieldType.Text, FieldRules.OverflowSlot, true, false, null))!.GetValue<string>().Should().Be("abc");
        layout.CodingValue(source, new CodingFieldInfo(1006, FieldType.Text, "txt.1", false, false, null)).Should().BeNull();
        FluentActions.Invoking(() => ProjectionLayouts.For("D")).Should().Throw<ArgumentException>();
    }

    private static ShadowLedgerVerdict Verdict(long stale) => new()
    {
        WorkspaceId = Guid.Parse("0199b3c4-0000-7000-8000-000000000001"),
        Candidate = CandidateAProjectionLayout.Id,
        Seed = 42,
        StartedUtc = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero),
        EndedUtc = new DateTimeOffset(2026, 10, 5, 12, 1, 0, TimeSpan.Zero),
        ShadowLedger = new ShadowLedgerCounters
        {
            Status = stale == 0 ? "passed" : "failed",
            TouchedDocs = 100,
            StaleOverwrites = stale,
            VersionRegressions = 0,
            MissingDocs = 0,
            ValueMismatches = 0,
            VersionConflictRejections = 3,
        },
        Sampling = new LedgerSampling { Passes = 5, Observations = 500, DocumentsTracked = 100, CommittedEventsCaptured = 140 },
        Reconciliation = new LedgerReconciliation { Documents = 100, Batches = 1, BatchSize = 1_000, DurationMs = 12.5, DocumentsPerSecond = 8_000 },
        Findings = stale == 0 ? [] :
        [
            new LedgerFinding(Guid.Parse("0199b3c4-0000-7000-8000-000000000002"), LedgerFindingKind.ExternalVersionMismatch, 3, 2, 4, "a write bypassed external versioning"),
        ],
    };
}
