using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;

namespace Opportunity.UnitTests.Jobs;

/// <summary>Idempotency keys, chunk bounds, retry and circuit breaker rules of ADR-010 §5–§7.</summary>
public class JobRulesTests
{
    private static readonly Guid Workspace = Guid.Parse("0199A7C2-0000-7000-8000-00000000000A");
    private static readonly Guid Job = Guid.Parse("0199a7c2-0000-7000-8000-0000000000b1");

    [Fact]
    public void Chunk_key_follows_the_ADR_010_formula()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "v1|0199a7c2-0000-7000-8000-00000000000a|0199a7c2-0000-7000-8000-0000000000b1|12|BulkCodingChunk|0")));

        ChunkIdempotencyKey.ForChunk(Workspace, Job, 12, ChunkOperationKind.BulkCodingChunk, 0).Should().Be(expected);
        // Golden value: changing the formula re-keys every chunk in flight.
        expected.Should().Be("ecdcb9d3a4b91612026648e1ad75ece13ca6a9dea890a5eaf9b927bff91ebe2b");
        expected.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Chunk_keys_differ_by_every_input()
    {
        var keys = new[]
        {
            ChunkIdempotencyKey.ForChunk(Workspace, Job, 1, ChunkOperationKind.ReindexChunk, 3),
            ChunkIdempotencyKey.ForChunk(Workspace, Job, 2, ChunkOperationKind.ReindexChunk, 3),
            ChunkIdempotencyKey.ForChunk(Workspace, Job, 1, ChunkOperationKind.IndexChunk, 3),
            ChunkIdempotencyKey.ForChunk(Workspace, Job, 1, ChunkOperationKind.ReindexChunk, 4),
            ChunkIdempotencyKey.ForChunk(Guid.CreateVersion7(), Job, 1, ChunkOperationKind.ReindexChunk, 3),
            ChunkIdempotencyKey.ForChunk(Workspace, Guid.CreateVersion7(), 1, ChunkOperationKind.ReindexChunk, 3),
            ChunkIdempotencyKey.ForOutbox(Workspace, Job),
        };
        keys.Should().OnlyHaveUniqueItems();
        ChunkIdempotencyKey.ForOutbox(Workspace, Job).Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "v1|0199a7c2-0000-7000-8000-00000000000a|outbox|0199a7c2-0000-7000-8000-0000000000b1"))));
        FluentActions.Invoking(() => ChunkIdempotencyKey.ForChunk(Workspace, Job, 0, ChunkOperationKind.ImportChunk, 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Operation_kind_names_are_part_of_the_key_and_never_change()
    {
        Enum.GetNames<ChunkOperationKind>().Should().Equal(
            "ImportChunk", "BulkCodingChunk", "RelationshipChunk", "IndexChunk", "ReindexChunk", "ExportChunk",
            "ProductionChunk", "RenderChunk");
    }

    [Theory]
    [InlineData(1, 1, 100)]
    [InlineData(1, 1000, 1000)]
    [InlineData(1, 1001, 1000)]
    [InlineData(5, 9_999, 250)]
    [InlineData(1, 0, 10)]
    public void Split_by_count_is_dense_and_bounded(long first, long last, int max)
    {
        var ranges = ChunkPlanner.SplitByCount(first, last, max);

        ranges.Sum(r => r.Count).Should().Be(Math.Max(0, last - first + 1));
        ranges.Should().OnlyContain(r => r.Count >= 1 && r.Count <= max);
        for (var i = 1; i < ranges.Count; i++)
        {
            ranges[i].From.Should().Be(ranges[i - 1].To + 1);
        }

        if (ranges.Count > 0)
        {
            ranges[0].From.Should().Be(first);
            ranges[^1].To.Should().Be(last);
        }
    }

    [Theory]
    [MemberData(nameof(PlannerSeeds))]
    public void Split_by_count_and_bytes_respects_both_bounds(int seed)
    {
        var random = new Random(seed);
        var bounds = new ChunkBounds(random.Next(1, 50), random.Next(1, 4) == 1 ? null : random.Next(100, 5_000));
        var sizes = Enumerable.Range(0, random.Next(0, 400)).Select(_ => (long)random.Next(0, 3_000)).ToArray();

        var ranges = ChunkPlanner.Split(1, sizes, bounds);

        ranges.Sum(r => r.Count).Should().Be(sizes.Length, $"seed {seed}");
        long position = 1;
        foreach (var range in ranges)
        {
            range.From.Should().Be(position, $"seed {seed}: ranges are contiguous");
            range.Count.Should().BeInRange(1, bounds.MaxItems);
            range.Bytes.Should().Be(sizes.Skip((int)(range.From - 1)).Take((int)range.Count).Sum());
            if (range.Count > 1 && bounds.MaxBytes is { } maxBytes)
            {
                range.Bytes.Should().BeLessThanOrEqualTo(maxBytes, $"seed {seed}: only a single oversized item may exceed the byte bound");
            }

            position = range.To + 1;
        }
    }

    public static TheoryData<int> PlannerSeeds => new(Enumerable.Range(1, 100));

    [Fact]
    public void Default_bounds_follow_ADR_010_section_6()
    {
        ChunkBounds.For(JobType.BulkCoding).MaxItems.Should().Be(1_000);
        ChunkBounds.For(JobType.BulkCoding, writesSecurityAffectingField: true).MaxItems.Should().Be(500);
        ChunkBounds.For(JobType.Import).Should().Be(new ChunkBounds(500, 512L * 1024 * 1024));
        ChunkBounds.For(JobType.Export).Should().Be(new ChunkBounds(250, 2_048L * 1024 * 1024));
        ChunkBounds.For(JobType.Production).MaxItems.Should().Be(100);
        ChunkBounds.For(JobType.Reindex).MaxItems.Should().Be(2_000);
        Enum.GetValues<JobType>().Should().OnlyContain(t => ChunkBounds.For(t, false).MaxItems > 0);
    }

    [Fact]
    public void Backoff_doubles_from_five_seconds_with_twenty_percent_jitter_and_caps_at_five_minutes()
    {
        ChunkRetryPolicy.Backoff(1, 0.5).Should().Be(TimeSpan.FromSeconds(5));
        ChunkRetryPolicy.Backoff(2, 0.5).Should().Be(TimeSpan.FromSeconds(10));
        ChunkRetryPolicy.Backoff(4, 0.5).Should().Be(TimeSpan.FromSeconds(40));
        ChunkRetryPolicy.Backoff(1, 0).Should().Be(TimeSpan.FromSeconds(4));
        ChunkRetryPolicy.Backoff(1, 0.999999).TotalSeconds.Should().BeApproximately(6, 0.001);
        ChunkRetryPolicy.Backoff(10, 0.5).Should().Be(TimeSpan.FromMinutes(5));
        ChunkRetryPolicy.Backoff(1_000, 0.999).Should().Be(TimeSpan.FromMinutes(5));

        var random = new Random(7);
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var nominal = Math.Min(5 * Math.Pow(2, Math.Min(attempt - 1, 16)), 300);
            var delay = ChunkRetryPolicy.Backoff(attempt, random.NextDouble()).TotalSeconds;
            delay.Should().BeInRange(nominal * 0.8 - 1e-9, Math.Min(nominal * 1.2, 300) + 1e-9);
        }
    }

    [Theory]
    [InlineData(ChunkErrorClass.Transient, 1, 5, JobChunkTrigger.RetryLater)]
    [InlineData(ChunkErrorClass.Transient, 4, 5, JobChunkTrigger.RetryLater)]
    [InlineData(ChunkErrorClass.Transient, 5, 5, JobChunkTrigger.FailPermanently)]
    [InlineData(ChunkErrorClass.Permanent, 1, 5, JobChunkTrigger.FailPermanently)]
    [InlineData(ChunkErrorClass.AttemptsExhausted, 1, 5, JobChunkTrigger.FailPermanently)]
    public void Errors_retry_only_while_transient_and_attempts_remain(ChunkErrorClass errorClass, int attempts, int max, JobChunkTrigger expected)
    {
        ChunkRetryPolicy.OnError(errorClass, attempts, max).Should().Be(expected);
    }

    [Theory]
    [InlineData(4, 0, 4, false)]
    [InlineData(5, 0, 5, true)]
    [InlineData(0, 18, 1, false)]
    [InlineData(0, 17, 2, false)]
    [InlineData(0, 17, 3, true)]
    [InlineData(1, 10, 2, false)]
    public void Circuit_breaker_pauses_on_consecutive_failures_or_failure_ratio(int consecutive, long committed, long failed, bool pause)
    {
        JobCircuitBreaker.ShouldPause(consecutive, committed, failed).Should().Be(pause);
    }

    [Fact]
    public void Settlement_completes_cancels_or_pauses_from_counters_only()
    {
        var done = new JobCounters { ChunksTotal = 3, ChunksCommitted = 3 };
        JobSettlement.Next(JobStatus.Running, done, 0).Should().Be(JobTrigger.Complete);
        JobSettlement.Next(JobStatus.Running, done with { ItemsFailed = 1 }, 0).Should().Be(JobTrigger.CompleteWithErrors);
        JobSettlement.Next(JobStatus.Running, done with { ChunksCommitted = 2, ChunksFailed = 1 }, 1).Should().Be(JobTrigger.CompleteWithErrors);
        JobSettlement.Next(JobStatus.Running, done with { ChunksCommitted = 1 }, 0).Should().BeNull();
        JobSettlement.Next(JobStatus.Running, new JobCounters { ChunksTotal = 10, ChunksFailed = 5 }, 5).Should().Be(JobTrigger.Pause);
        JobSettlement.Next(JobStatus.Cancelling, new JobCounters { ChunksTotal = 3, ChunksCommitted = 1, ChunksCancelled = 1 }, 0).Should().BeNull();
        JobSettlement.Next(JobStatus.Cancelling, new JobCounters { ChunksTotal = 3, ChunksCommitted = 1, ChunksCancelled = 2 }, 0)
            .Should().Be(JobTrigger.FinishCancelling);
        JobSettlement.Next(JobStatus.Paused, done, 0).Should().BeNull("a paused job completes after resume");
        JobSettlement.Next(JobStatus.Running, new JobCounters(), 0).Should().Be(JobTrigger.Complete, "a job without chunks is done");
    }

    [Fact]
    public void Membership_references_are_validated_per_kind()
    {
        var snapshot = ChunkMembership.SnapshotRange(Guid.CreateVersion7(), 1, 1_000);
        snapshot.KnownCount.Should().Be(1_000);
        ChunkMembership.ImportRows(Guid.CreateVersion7(), 501, 1_000).KnownCount.Should().Be(500);
        ChunkMembership.DocumentKeyRange(2, Guid.Empty, Guid.AllBitsSet).KnownCount.Should().BeNull();
        ChunkMembership.ExplicitIds([Guid.CreateVersion7(), Guid.CreateVersion7()]).KnownCount.Should().Be(2);

        FluentActions.Invoking(() => ChunkMembership.SnapshotRange(Guid.Empty, 1, 2)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ChunkMembership.SnapshotRange(Guid.CreateVersion7(), 0, 2)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ChunkMembership.ImportRows(Guid.CreateVersion7(), 5, 4)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ChunkMembership.DocumentKeyRange(0, Guid.Empty, Guid.AllBitsSet)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ChunkMembership.DocumentKeyRange(1, Guid.AllBitsSet, Guid.Empty)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ChunkMembership.ExplicitIds([])).Should().Throw<ArgumentException>();
        var id = Guid.CreateVersion7();
        FluentActions.Invoking(() => ChunkMembership.ExplicitIds([id, id])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ChunkMembership.ExplicitIds([.. Enumerable.Range(0, 1_001).Select(_ => Guid.NewGuid())]))
            .Should().Throw<ArgumentException>();

        ChunkMembership.Restore(snapshot.Kind, snapshot.SnapshotId, null, 1, 1_000, null, null, null, null).Should().Be(snapshot);
    }

    [Fact]
    public void Api_job_enums_mirror_the_domain_enums()
    {
        Enum.GetNames<JobResourceType>().Should().Equal(Enum.GetNames<JobType>());
        Enum.GetNames<JobResourceStatus>().Should().Equal(Enum.GetNames<JobStatus>());
    }
}
