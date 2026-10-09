using AwesomeAssertions;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Jobs.Dispatch;

namespace Opportunity.UnitTests.Jobs;

/// <summary>E06-T04: fair scheduling of dispatcher passes and the job chunk message/queue mapping.</summary>
public sealed class DispatchSchedulerTests
{
    private static readonly DispatchItem Busy = new(Guid.CreateVersion7(), DispatchWork.Outbox);
    private static readonly DispatchItem Quiet = new(Guid.CreateVersion7(), DispatchWork.Outbox);
    private static readonly DispatchItem Secure = new(Guid.CreateVersion7(), DispatchWork.Outbox);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_workspace_with_more_work_goes_to_the_back_so_others_get_a_turn()
    {
        using var scheduler = new DispatchScheduler();
        scheduler.Signal(Busy);
        (await scheduler.TakeAsync(Ct)).Should().Be(Busy);
        scheduler.Signal(Quiet);
        scheduler.Complete(Busy, more: true);

        (await scheduler.TakeAsync(Ct)).Should().Be(Quiet);
        (await scheduler.TakeAsync(Ct)).Should().Be(Busy);
    }

    [Fact]
    public async Task An_urgent_signal_jumps_the_queue_and_is_taken_once()
    {
        using var scheduler = new DispatchScheduler();
        scheduler.Signal(Busy);
        scheduler.Signal(Quiet);
        scheduler.Signal(Secure);
        scheduler.Signal(Secure, urgent: true);

        (await scheduler.TakeAsync(Ct)).Should().Be(Secure);
        (await scheduler.TakeAsync(Ct)).Should().Be(Busy);
        (await scheduler.TakeAsync(Ct)).Should().Be(Quiet);
        scheduler.Pending.Should().Be(0, "the stale normal-queue copy of the urgent item is skipped");
        using var none = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var take = async () => await scheduler.TakeAsync(none.Token);
        await take.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Duplicate_signals_queue_an_item_once_and_a_signal_during_its_pass_runs_it_again_afterwards()
    {
        using var scheduler = new DispatchScheduler();
        scheduler.Signal(Busy);
        scheduler.Signal(Busy);
        scheduler.Pending.Should().Be(1);

        (await scheduler.TakeAsync(Ct)).Should().Be(Busy);
        scheduler.Signal(Busy);
        scheduler.Pending.Should().Be(0, "an item never runs on two passes at once");
        scheduler.Complete(Busy, more: false);

        scheduler.Pending.Should().Be(1, "the wake-up during the pass is not lost");
        (await scheduler.TakeAsync(Ct)).Should().Be(Busy);
        scheduler.Complete(Busy, more: false);
        scheduler.Pending.Should().Be(0);
    }

    [Theory]
    [InlineData(ChunkOperationKind.ImportChunk, "import.chunks")]
    [InlineData(ChunkOperationKind.BulkCodingChunk, "bulkcoding.chunks")]
    [InlineData(ChunkOperationKind.RenderChunk, "render.chunks")]
    [InlineData(ChunkOperationKind.ExportChunk, "export.chunks")]
    [InlineData(ChunkOperationKind.ProductionChunk, "production.chunks")]
    [InlineData(ChunkOperationKind.ProductionVolumeChunk, "render.chunks")]
    [InlineData(ChunkOperationKind.RelationshipChunk, "import.chunks")]
    public void Job_chunks_go_to_their_operation_queue_payload_free(ChunkOperationKind operation, string queue)
    {
        var chunk = new ClaimedJobChunk(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 7, JobChunkStatus.Pending, operation, new string('a', 64), 2, null);

        var message = JobChunkRelay.Message(chunk);

        message.Destination.Name.Should().Be(queue);
        message.Payload.Should().Be(new JobChunkMessage { ChunkId = chunk.ChunkId, Sequence = 7, Operation = Enum.Parse<JobChunkOperation>(operation.ToString()) });
        message.Correlation.WorkspaceId.Should().Be(chunk.WorkspaceId);
        message.Correlation.JobId.Should().Be(chunk.JobId);
        message.IdempotencyKey.Should().Be(chunk.IdempotencyKey);
        message.Attempt.Should().Be(2);
        JobChunkRelay.DispatchedOperations.Should().Contain(operation);
    }

    [Fact]
    public void Operations_without_a_worker_queue_are_not_dispatched()
    {
        JobChunkRelay.QueueFor(ChunkOperationKind.ReindexChunk).Should().BeNull();
        JobChunkRelay.DispatchedOperations.Should().NotContain([ChunkOperationKind.ReindexChunk, ChunkOperationKind.IndexChunk]);
        DispatchMetrics.LaneName(MessageLane.SecurityBulk).Should().Be("security-bulk");
    }
}
