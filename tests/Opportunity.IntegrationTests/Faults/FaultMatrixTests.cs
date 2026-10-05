#if OPPORTUNITY_FAILPOINTS
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Application.Faults;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>
/// E18-T01: the seeded fault-injection matrix (fault × failpoint × workload) over the real dispatcher and worker hosts,
/// PostgreSQL, OpenSearch and RabbitMQ (§26 "100% idempotent in the fault-injection suite", "0 stale-version
/// overwrites", "0 unauthorized retrievals"). Pull requests run the <see cref="FaultMatrix.PullRequest"/> subset with one
/// trial per cell; the nightly workflow runs <see cref="FaultMatrix.Full"/> with five. Every trial prints its seed and
/// replay command; set <c>OPPORTUNITY_ORACLE_RESULTS</c> to keep each trial's verdict.json and a matrix.jsonl line.
/// </summary>
[Collection(FaultCollectionDefinition.Name)]
[Trait("Category", "FaultMatrix")]
public sealed class FaultMatrixTests(FaultWorldFixture world) : IClassFixture<FaultWorldFixture>
{
    public static TheoryData<string, string, string> Cells
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var cell in FaultMatrix.Selected)
            {
                data.Add(cell.Fault.ToString(), cell.Failpoint, cell.Workload.ToString());
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Fault_matrix_cell_is_idempotent(string fault, string failpoint, string workload)
    {
        var cell = new FaultCell(Enum.Parse<FaultKind>(fault), failpoint, Enum.Parse<FaultWorkload>(workload));
        var outcomes = new List<TrialOutcome>();
        foreach (var seed in FaultMatrix.Seeds(cell))
        {
            var outcome = await FaultTrial.RunAsync(world, cell, seed);
            outcomes.Add(outcome);
            TestContext.Current.TestOutputHelper?.WriteLine(outcome.ToString());
            await RecordAsync(outcome);
        }

        if (outcomes.Any(o => !o.Passed))
        {
            Assert.Fail(FaultTrial.Summary(outcomes));
        }
    }

    private static async Task RecordAsync(TrialOutcome outcome)
    {
        if (Environment.GetEnvironmentVariable(LedgerSupport.ResultsVariable) is not { Length: > 0 } root)
        {
            return;
        }

        Directory.CreateDirectory(root);
        var line = JsonSerializer.Serialize(new
        {
            fault = outcome.Cell.Fault.ToString(),
            failpoint = outcome.Cell.Failpoint,
            workload = outcome.Cell.Workload.ToString(),
            candidate = outcome.Verdict?.Candidate,
            seed = outcome.Seed,
            passed = outcome.Passed,
            firedAt = outcome.FiredAt,
            durationSeconds = Math.Round(outcome.Duration.TotalSeconds, 1),
            securityChecks = outcome.SecurityChecks,
            unauthorizedRetrievals = outcome.UnauthorizedRetrievals,
            shadowLedger = outcome.Verdict?.ShadowLedger,
            failures = outcome.Failures,
            replay = outcome.Replay,
        }, JsonSerializerOptions.Web);
        await File.AppendAllTextAsync(Path.Combine(root, "matrix.jsonl"), line + "\n", TestContext.Current.CancellationToken);
    }
}

/// <summary>The matrix's shape (no containers needed): the E18-T01 coverage floor and the reproducibility of its seeds.</summary>
public sealed class FaultMatrixShapeTests
{
    [Fact]
    public void The_full_matrix_covers_at_least_8_fault_types_at_5_failpoints_each_and_the_PR_subset_touches_every_one()
    {
        var full = FaultMatrix.Full;
        full.Select(c => c.Fault).Distinct().Should().HaveCountGreaterThanOrEqualTo(8);
        full.GroupBy(c => c.Fault).Should().OnlyContain(g => g.Select(c => c.Failpoint).Distinct().Count() >= 5);
        full.Select(c => c.Failpoint).Distinct().Should().HaveCountGreaterThanOrEqualTo(5);

        var pr = FaultMatrix.PullRequest;
        pr.Select(c => c.Fault).Distinct().Should().BeEquivalentTo(Enum.GetValues<FaultKind>());
        pr.Select(c => c.Failpoint).Distinct().Should().BeEquivalentTo(FaultMatrix.AllFailpoints);
        pr.Should().OnlyContain(c => full.Contains(c));
        FaultMatrix.AllFailpoints.Should().OnlyContain(f => Failpoints.Catalog.Contains(f));
    }

    [Fact]
    public void Trial_seeds_depend_only_on_the_base_seed_the_cell_and_the_trial()
    {
        var cell = new FaultCell(FaultKind.Crash, Failpoints.AfterCommit, FaultWorkload.Mixed);

        FaultMatrix.Seed(1, cell, 1).Should().Be(FaultMatrix.Seed(1, cell, 1));
        FaultMatrix.Seed(1, cell, 2).Should().NotBe(FaultMatrix.Seed(1, cell, 1));
        FaultMatrix.Seed(2, cell, 1).Should().NotBe(FaultMatrix.Seed(1, cell, 1));
        FaultMatrix.Seed(1, cell with { Failpoint = Failpoints.AfterClaim }, 1).Should().NotBe(FaultMatrix.Seed(1, cell, 1));
    }
}
#endif
