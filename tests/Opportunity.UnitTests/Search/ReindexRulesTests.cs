using AwesomeAssertions;

using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Reindex;
using Opportunity.Core.Jobs;
using Opportunity.Search.Indexing;
using Opportunity.Search.Reindex;

namespace Opportunity.UnitTests.Search;

/// <summary>E07-T11: key-range arithmetic, explicit job completion, revisioned names and the persisted run shapes.</summary>
public sealed class ReindexRulesTests
{
    [Fact]
    public void The_key_successor_follows_the_order_PostgreSQL_and_Guid_CompareTo_share()
    {
        var random = new Random(11);
        for (var i = 0; i < 2_000; i++)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            if (i % 3 == 0)
            {
                // Runs of 0xFF in the low bytes exercise the carry.
                Array.Fill(bytes, (byte)0xFF, 16 - (i % 16) - 1, (i % 16) + 1);
            }

            var id = new Guid(bytes, bigEndian: true);
            if (id == Guid.AllBitsSet)
            {
                continue;
            }

            var next = DocumentKeyRanges.Successor(id);
            next.CompareTo(id).Should().BePositive();
            string.CompareOrdinal(next.ToString("D"), id.ToString("D")).Should().BePositive("the indexed documentId keyword sorts the same way");
            new System.Numerics.BigInteger(next.ToByteArray(bigEndian: true), isUnsigned: true, isBigEndian: true)
                .Should().Be(new System.Numerics.BigInteger(id.ToByteArray(bigEndian: true), isUnsigned: true, isBigEndian: true) + 1);
        }

        DocumentKeyRanges.Successor(Guid.Empty).Should().Be(new Guid("00000000-0000-0000-0000-000000000001"));
        DocumentKeyRanges.Successor(new Guid("00000000-0000-0000-00ff-ffffffffffff")).Should().Be(new Guid("00000000-0000-0000-0100-000000000000"));
        FluentActions.Invoking(() => DocumentKeyRanges.Successor(Guid.AllBitsSet)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_reindex_job_stays_running_after_its_last_chunk_until_the_coordinator_completes_it()
    {
        var done = new JobCounters { ChunksTotal = 3, ChunksCommitted = 3 };

        JobSettlement.CompletesExplicitly(JobType.Reindex).Should().BeTrue();
        JobSettlement.CompletesExplicitly(JobType.Import).Should().BeFalse();
        JobSettlement.Next(JobStatus.Running, done, 0, completesExplicitly: true).Should().BeNull();
        JobSettlement.Next(JobStatus.Running, done with { ChunksCommitted = 2, ChunksFailed = 1 }, 1, completesExplicitly: true).Should().BeNull();
        JobSettlement.Next(JobStatus.Running, new JobCounters { ChunksTotal = 10, ChunksFailed = 5 }, 5, completesExplicitly: true)
            .Should().Be(JobTrigger.Pause, "the circuit breaker still applies");
        JobSettlement.Next(JobStatus.Cancelling, done with { ChunksCommitted = 1, ChunksCancelled = 2 }, 0, completesExplicitly: true)
            .Should().Be(JobTrigger.FinishCancelling);
        JobSettlement.Next(JobStatus.Running, done, 0).Should().Be(JobTrigger.Complete);
    }

    [Fact]
    public void Dedicated_revisions_get_their_own_physical_name_that_the_generation_template_still_matches()
    {
        IndexNames.Physical("opp-ws-abc", 2).Should().Be("opp-ws-abc-g2");
        IndexNames.Physical("opp-ws-abc", 2, 0).Should().Be("opp-ws-abc-g2");
        IndexNames.Physical("opp-ws-abc", 2, 3).Should().Be("opp-ws-abc-r3-g2");
        var pattern = new IndexNames("opp").TemplatePatterns(2)[1];
        System.Text.RegularExpressions.Regex.IsMatch("opp-ws-abc-r3-g2", "^" + pattern.Replace("*", ".*", StringComparison.Ordinal) + "$")
            .Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch("opp-ws-abc-r3-g12", "^" + pattern.Replace("*", ".*", StringComparison.Ordinal) + "$")
            .Should().BeFalse();
    }

    [Fact]
    public void Request_and_validation_round_trip_through_their_stored_json()
    {
        var request = new ReindexRequest(IndexPlacementKind.Dedicated, 2, 4);
        ReindexRequest.Parse(request.ToJson()).Should().Be(request);
        ReindexRequest.Parse(new ReindexRequest().ToJson()).Should().Be(new ReindexRequest());

        var validation = new ReindexValidation
        {
            PostgresDocuments = 10,
            IndexDocuments = 9,
            FullCheck = true,
            DocumentsCompared = 10,
            MissingDocuments = 1,
            Examples = [Guid.CreateVersion7()],
            Failure = "1 missing",
        };
        var restored = ReindexValidation.FromJson(validation.ToJson())!;
        restored.Should().BeEquivalentTo(validation);
        restored.Passed.Should().BeFalse();
    }

    [Fact]
    public void Phases_split_into_in_flight_and_finished_and_name_the_job_status()
    {
        Enum.GetValues<ReindexPhase>().Where(ReindexPhases.IsInFlight).Should().Equal(
            ReindexPhase.Pending, ReindexPhase.Building, ReindexPhase.Backfilling, ReindexPhase.Validating, ReindexPhase.Switching);
        Enum.GetValues<ReindexPhase>().Where(ReindexPhases.IsFinished).Should().Equal(ReindexPhase.Completed, ReindexPhase.Aborted);
        var run = new ReindexRun { WorkspaceId = Guid.NewGuid(), JobId = Guid.NewGuid(), Phase = ReindexPhase.Aborted, Request = new(), Error = "boom" };
        run.StatusReason.Should().Contain("boom").And.Contain("keeps serving");
    }

    [Fact]
    public void Options_are_validated()
    {
        new ReindexOptions().Validate();
        FluentActions.Invoking(() => new ReindexOptions { TaskWindow = 0 }.Validate()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => new ReindexOptions { LeaseDuration = TimeSpan.FromSeconds(1) }.Validate()).Should().Throw<InvalidOperationException>();
    }
}
