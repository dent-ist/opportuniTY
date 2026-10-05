using AwesomeAssertions;

using Opportunity.Core.Snapshots;

namespace Opportunity.UnitTests.Snapshots;

/// <summary>E10-T03: the §22 / ADR-002 deterministic PIT-vs-materialized rule.</summary>
public class SnapshotStrategyRulesTests
{
    private const SelectionStrategy Pit = SelectionStrategy.PointInTime;
    private const SelectionStrategy Mat = SelectionStrategy.Materialized;

    /// <summary>ADR-002 §3, row for row: #, L, R, A, X, strategy, governing predicates.</summary>
    private static readonly (int Row, bool L, bool R, bool A, bool X, SelectionStrategy Strategy, string Governing)[] Table =
    [
        (1, false, false, false, false, Pit, "none"),
        (2, false, false, false, true, Mat, "X"),
        (3, false, false, true, false, Mat, "A"),
        (4, false, false, true, true, Mat, "A, X"),
        (5, false, true, false, false, Mat, "R"),
        (6, false, true, false, true, Mat, "R, X"),
        (7, false, true, true, false, Mat, "R, A"),
        (8, false, true, true, true, Mat, "R, A, X"),
        (9, true, false, false, false, Mat, "L"),
        (10, true, false, false, true, Mat, "L, X"),
        (11, true, false, true, false, Mat, "L, A"),
        (12, true, false, true, true, Mat, "L, A, X"),
        (13, true, true, false, false, Mat, "L, R"),
        (14, true, true, false, true, Mat, "L, R, X"),
        (15, true, true, true, false, Mat, "L, R, A"),
        (16, true, true, true, true, Mat, "L, R, A, X"),
    ];

    public static TheoryData<int, bool, bool, bool, bool, SelectionStrategy, string> DecisionTable
    {
        get
        {
            var data = new TheoryData<int, bool, bool, bool, bool, SelectionStrategy, string>();
            foreach (var t in Table)
            {
                data.Add(t.Row, t.L, t.R, t.A, t.X, t.Strategy, t.Governing);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DecisionTable))]
    public void Decision_table_row(int row, bool l, bool r, bool a, bool x, SelectionStrategy expected, string governing)
    {
        var predicates = (l ? StrategyPredicates.LongRunning : 0) | (r ? StrategyPredicates.ExactRestart : 0)
            | (a ? StrategyPredicates.SurvivesIndexChange : 0) | (x ? StrategyPredicates.LegalReproducibility : 0);

        SnapshotStrategyRules.Choose(l, r, a, x).Should().Be(expected, "row {0}", row);
        SnapshotStrategyRules.Choose(predicates).Should().Be(expected, "row {0}", row);
        SnapshotStrategyRules.Format(predicates).Should().Be(governing);
    }

    [Fact]
    public void Decision_table_covers_all_16_predicate_combinations_exactly_once()
    {
        var rows = Table.Select(t => (t.L ? 8 : 0) | (t.R ? 4 : 0) | (t.A ? 2 : 0) | (t.X ? 1 : 0)).ToList();

        rows.Should().HaveCount(16).And.OnlyHaveUniqueItems().And.BeEquivalentTo(Enumerable.Range(0, 16));
        Enumerable.Range(0, 16).Count(m => SnapshotStrategyRules.Choose((m & 8) != 0, (m & 4) != 0, (m & 2) != 0, (m & 1) != 0) == Pit)
            .Should().Be(1, "only the all-false row uses a reader");
    }

    /// <summary>ADR-002 §4: R, A and X per operation.</summary>
    public static TheoryData<SetOperationKind, StrategyPredicates> JobTypes => new()
    {
        { SetOperationKind.BulkCoding, StrategyPredicates.ExactRestart },
        { SetOperationKind.ApplyToFamily, StrategyPredicates.ExactRestart },
        { SetOperationKind.Export, StrategyPredicates.ExactRestart | StrategyPredicates.SurvivesIndexChange | StrategyPredicates.LegalReproducibility },
        { SetOperationKind.Production, StrategyPredicates.ExactRestart | StrategyPredicates.SurvivesIndexChange | StrategyPredicates.LegalReproducibility },
        { SetOperationKind.PrivilegeLog, StrategyPredicates.LegalReproducibility },
        { SetOperationKind.SavedSearchTermReport, StrategyPredicates.SurvivesIndexChange | StrategyPredicates.LegalReproducibility },
        { SetOperationKind.SearchTermReportPreview, StrategyPredicates.None },
        { SetOperationKind.ReviewBatch, StrategyPredicates.ExactRestart | StrategyPredicates.SurvivesIndexChange },
        { SetOperationKind.InteractiveCursor, StrategyPredicates.None },
    };

    [Theory]
    [MemberData(nameof(JobTypes))]
    public void Job_type_table(SetOperationKind operation, StrategyPredicates fixedPredicates)
    {
        SnapshotStrategyRules.FixedPredicates(operation).Should().Be(fixedPredicates);
        var small = SnapshotStrategyRules.Decide(operation, new SetSizeEstimate(10), new PitPolicy());
        var large = SnapshotStrategyRules.Decide(operation, new SetSizeEstimate(100_000_000), new PitPolicy());
        if (operation == SetOperationKind.InteractiveCursor)
        {
            small.Strategy.Should().Be(Pit);
            large.Strategy.Should().Be(Pit, "interactive cursors are never materialized in the MVP (Q-33)");
        }
        else if (operation == SetOperationKind.SearchTermReportPreview)
        {
            small.Strategy.Should().Be(Pit, "the only job that may run under a reader");
            large.Strategy.Should().Be(Mat);
            large.Predicates.Should().Be(StrategyPredicates.LongRunning, "row 9");
        }
        else
        {
            small.Strategy.Should().Be(Mat);
            large.Strategy.Should().Be(Mat);
            small.Predicates.Should().Be(fixedPredicates);
            large.Predicates.Should().Be(fixedPredicates | StrategyPredicates.LongRunning);
        }
    }

    public static TheoryData<long?> Sizes => new() { (long?)null, 0L, 1L, 1_000L, 3_900_000L, 1_000_000_000L, long.MaxValue / 4 };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Exports_and_productions_are_always_materialized(long? count)
    {
        // Even under a policy that would let almost anything run under a reader.
        var lenient = new PitPolicy { JobMaxAge = TimeSpan.FromHours(1), SafetyFactor = 1, SetupTime = TimeSpan.Zero, IdPagingThroughput = 1e12 };
        foreach (var policy in new[] { new PitPolicy(), lenient })
        {
            foreach (var operation in new[] { SetOperationKind.Export, SetOperationKind.Production })
            {
                var estimate = count is { } n ? new SetSizeEstimate(n, Throughput: 1e12) : null;
                var decision = SnapshotStrategyRules.Decide(operation, estimate, policy);
                decision.Strategy.Should().Be(Mat);
                decision.Predicates.Should().HaveFlag(StrategyPredicates.ExactRestart | StrategyPredicates.SurvivesIndexChange
                    | StrategyPredicates.LegalReproducibility);
                SnapshotStrategyRules.AlwaysMaterialized(operation).Should().BeTrue();
            }
        }
    }

    [Fact]
    public void Short_lived_means_an_estimate_within_200_seconds_by_default()
    {
        var policy = new PitPolicy();
        policy.ShortLivedLimit.Should().Be(TimeSpan.FromSeconds(200));

        // Preview: T_est = 5 s + terms / 0.5 per s → 97 terms = 199 s (reader), 98 terms = 201 s (materialized).
        var fits = SnapshotStrategyRules.Decide(SetOperationKind.SearchTermReportPreview, new SetSizeEstimate(97), policy);
        fits.Strategy.Should().Be(Pit);
        fits.EstimatedRuntime.Should().Be(TimeSpan.FromSeconds(199));
        fits.Governing.Should().Be("none");
        var over = SnapshotStrategyRules.Decide(SetOperationKind.SearchTermReportPreview, new SetSizeEstimate(98), policy);
        over.Strategy.Should().Be(Mat);
        over.Governing.Should().Be("L");

        // ID paging at 20,000 docs/s: exactly 200 s is still short-lived, a hair more is not.
        SnapshotStrategyRules.EstimateRuntime(SetOperationKind.BulkCoding, new SetSizeEstimate(3_900_000), policy)
            .Should().Be(TimeSpan.FromSeconds(200));
        SnapshotStrategyRules.IsLongRunning(TimeSpan.FromSeconds(200), policy).Should().BeFalse();
        SnapshotStrategyRules.IsLongRunning(TimeSpan.FromSeconds(200.01), policy).Should().BeTrue();

        // Expansion multiplies N; a measured throughput replaces the default.
        SnapshotStrategyRules.EstimateRuntime(SetOperationKind.BulkCoding, new SetSizeEstimate(100_000, 4.0), policy)
            .Should().Be(TimeSpan.FromSeconds(25));
        SnapshotStrategyRules.EstimateRuntime(SetOperationKind.SearchTermReportPreview, new SetSizeEstimate(10, Throughput: 2), policy)
            .Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void An_unsized_job_counts_as_long_running_and_the_rule_is_deterministic()
    {
        var policy = new PitPolicy();
        var unsized = SnapshotStrategyRules.Decide(SetOperationKind.SearchTermReportPreview, null, policy);
        unsized.Strategy.Should().Be(Mat);
        unsized.EstimatedRuntime.Should().BeNull();
        SnapshotStrategyRules.Describe(unsized).Should().Be("Materialized (L)");

        foreach (var operation in Enum.GetValues<SetOperationKind>())
        {
            foreach (var count in new long[] { 0, 97, 98, 50_000, 10_000_000 })
            {
                SnapshotStrategyRules.Decide(operation, new SetSizeEstimate(count), policy)
                    .Should().Be(SnapshotStrategyRules.Decide(operation, new SetSizeEstimate(count), new PitPolicy()));
            }
        }

        SnapshotStrategyRules.Describe(SnapshotStrategyRules.Decide(SetOperationKind.Export, new SetSizeEstimate(150_000), policy))
            .Should().Be("Materialized (R, A, X; T_est 12.5 s)");
    }

    [Fact]
    public void Snapshots_are_accepted_only_by_the_operation_they_were_frozen_for()
    {
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.BulkCoding, SnapshotPurpose.BulkCoding).Should().BeTrue();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.Export, SnapshotPurpose.Export).Should().BeTrue();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.Production, SnapshotPurpose.Production).Should().BeTrue();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.SavedSearchTermReport, SnapshotPurpose.Report).Should().BeTrue();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.BulkCoding, SnapshotPurpose.Export).Should().BeFalse();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.Export, SnapshotPurpose.BulkCoding).Should().BeFalse();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.SearchTermReportPreview, SnapshotPurpose.Report).Should().BeFalse();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.InteractiveCursor, SnapshotPurpose.Report).Should().BeFalse();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.BulkCoding, (SnapshotPurpose)99).Should().BeFalse();

        foreach (var purpose in Enum.GetValues<SnapshotPurpose>())
        {
            SnapshotStrategyRules.AlwaysMaterialized(SnapshotStrategyRules.OperationFor(purpose)).Should().BeTrue(
                "every snapshot purpose is an operation the rule always materializes");
        }
    }

    [Fact]
    public void Policy_validation_rejects_out_of_range_settings()
    {
        new PitPolicy().Validate("Snapshots:Pit");
        Action zeroAge = () => new PitPolicy { JobMaxAge = TimeSpan.Zero }.Validate("Snapshots:Pit");
        Action factor = () => new PitPolicy { SafetyFactor = 0.5 }.Validate("Snapshots:Pit");
        Action throughput = () => new PitPolicy { IdPagingThroughput = 0 }.Validate("Snapshots:Pit");
        zeroAge.Should().Throw<InvalidOperationException>().WithMessage("*JobMaxAge*");
        factor.Should().Throw<InvalidOperationException>().WithMessage("*SafetyFactor*");
        throughput.Should().Throw<InvalidOperationException>().WithMessage("*IdPagingThroughput*");
    }
}
