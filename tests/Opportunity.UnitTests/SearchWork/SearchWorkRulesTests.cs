using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;

namespace Opportunity.UnitTests.SearchWork;

/// <summary>E06-T03: lanes, outbox retry policy, outbox keys and the relay's confirm-then-mark rule (ADR-001 §5, §6).</summary>
public class SearchWorkRulesTests
{
    private static readonly Guid Workspace = Guid.Parse("0199a7c2-0000-7000-8000-00000000000a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SearchChangeMask.Coding, MessageLane.Interactive, MessageLane.Bulk)]
    [InlineData(SearchChangeMask.Coding | SearchChangeMask.Security, MessageLane.Security, MessageLane.SecurityBulk)]
    [InlineData(SearchChangeMask.Security | SearchChangeMask.Delete, MessageLane.Security, MessageLane.SecurityBulk)]
    [InlineData(SearchChangeMask.Content | SearchChangeMask.Metadata, MessageLane.Interactive, MessageLane.Bulk)]
    public void Security_affecting_work_takes_the_security_lanes(SearchChangeMask mask, MessageLane outbox, MessageLane task)
    {
        SearchLanes.ForOutbox(mask).Should().Be(outbox);
        SearchLanes.ForChunkTask(mask).Should().Be(task);
    }

    [Fact]
    public void Every_indexing_lane_has_its_own_queue()
    {
        SearchLanes.Queue(MessageLane.Security).Should().Be(WorkQueues.IndexSecurity);
        SearchLanes.Queue(MessageLane.Interactive).Should().Be(WorkQueues.IndexInteractive);
        SearchLanes.Queue(MessageLane.SecurityBulk).Should().Be(WorkQueues.IndexSecurityBulk);
        SearchLanes.Queue(MessageLane.Bulk).Should().Be(WorkQueues.IndexBulk);
        var none = () => SearchLanes.Queue(MessageLane.None);
        none.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 1_000)]
    [InlineData(6, 16_000)]
    [InlineData(7, 30_000)]
    [InlineData(40, 30_000)]
    public void Outbox_backoff_doubles_from_half_a_second_and_caps_at_thirty(int attempt, int milliseconds) =>
        SearchOutboxRetryPolicy.Backoff(attempt).Should().Be(TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public void Outbox_key_follows_the_ADR_010_formula_for_bigint_ids()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "v1|0199a7c2-0000-7000-8000-00000000000a|outbox|42")));

        ChunkIdempotencyKey.ForOutbox(Workspace, 42L).Should().Be(expected);
        ChunkIdempotencyKey.ForOutbox(Workspace, 43L).Should().NotBe(expected);
        var zero = () => ChunkIdempotencyKey.ForOutbox(Workspace, 0L);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Relay_marks_only_confirmed_rows_dispatched_and_releases_the_rest()
    {
        var outbox = new FakeOutbox([Row(1), Row(2), Row(3)]);
        var tasks = new FakeTasks([Task(Guid.CreateVersion7()), Task(Guid.CreateVersion7())]);
        var publisher = new FakePublisher(failOutboxId: 2, failTaskId: tasks.Claimed[1].TaskId);
        var relay = new SearchWorkRelay(outbox, tasks, publisher, new SearchWorkRelayOptions { Owner = "d1" });

        var result = await relay.RelayOnceAsync(Workspace, Ct);

        result.Should().Be(new SearchWorkRelayResult(2, 1, 1, 1));
        outbox.Dispatched.Select(r => r.OutboxId).Should().BeEquivalentTo([1L, 3L]);
        outbox.Released.Should().ContainSingle().Which.OutboxId.Should().Be(2);
        outbox.ReleaseReason.Should().Be("nack");
        tasks.Dispatched.Should().Equal(tasks.Claimed[0].TaskId);
        tasks.Released.Should().Equal(tasks.Claimed[1].TaskId);
        publisher.Keys.Should().Contain(ChunkIdempotencyKey.ForOutbox(Workspace, 1L)).And.Contain(tasks.Claimed[0].IdempotencyKey);
        publisher.Attempts.Should().OnlyContain(a => a == 3, "the envelope attempt is the row's AttemptCount");
    }

    [Fact]
    public async Task Relay_with_nothing_to_claim_publishes_nothing()
    {
        var publisher = new FakePublisher(0, Guid.Empty);
        var result = await new SearchWorkRelay(new FakeOutbox([]), new FakeTasks([]), publisher, new SearchWorkRelayOptions())
            .RelayOnceAsync(Workspace, Ct);

        result.Claimed.Should().Be(0);
        publisher.Keys.Should().BeEmpty();
    }

    private static ClaimedOutboxRow Row(long id) => new(
        Workspace, id, DateTimeOffset.UtcNow, Guid.CreateVersion7(), 2, SearchChangeMask.Coding, MessageLane.Interactive, id,
        DateTimeOffset.UtcNow, 3);

    private static ClaimedIndexTask Task(Guid id) => new(
        Workspace, id, Guid.CreateVersion7(), Guid.CreateVersion7(), IndexTaskKind.BulkCoding, MessageLane.Bulk, 1,
        DateTimeOffset.UtcNow, ChunkIdempotencyKey.ForChunk(Workspace, id, 1, ChunkOperationKind.IndexChunk, 0), 3);

    private sealed class FakePublisher(long failOutboxId, Guid failTaskId) : IMessagePublisher
    {
        private readonly object _gate = new();

        public List<string> Keys { get; } = [];

        public List<int> Attempts { get; } = [];

        public Task<MessageEnvelope> PublishAsync<TPayload>(OutgoingMessage<TPayload> message, CancellationToken cancellationToken = default)
            where TPayload : class
        {
            var fail = message.Payload switch
            {
                SearchOutboxMessage m => m.OutboxId == failOutboxId,
                IndexChunkTaskMessage t => t.TaskId == failTaskId,
                _ => false,
            };
            if (fail)
            {
                throw new MessagePublishException("nack");
            }

            lock (_gate)
            {
                Keys.Add(message.IdempotencyKey);
                Attempts.Add(message.Attempt);
            }

            return System.Threading.Tasks.Task.FromResult<MessageEnvelope>(null!);
        }
    }

    private sealed class FakeOutbox(IReadOnlyList<ClaimedOutboxRow> claimable) : ISearchOutboxRepository
    {
        public List<ClaimedOutboxRow> Dispatched { get; } = [];

        public List<ClaimedOutboxRow> Released { get; } = [];

        public string? ReleaseReason { get; private set; }

        public Task<IReadOnlyList<ClaimedOutboxRow>> ClaimAsync(
            Guid workspaceId, string owner, int limit, TimeSpan claimDuration, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(claimable);

        public Task<int> MarkDispatchedAsync(
            Guid workspaceId, string owner, IReadOnlyCollection<ClaimedOutboxRow> rows, CancellationToken cancellationToken = default)
        {
            Dispatched.AddRange(rows);
            return System.Threading.Tasks.Task.FromResult(rows.Count);
        }

        public Task<int> ReleaseAsync(
            Guid workspaceId, string owner, IReadOnlyCollection<ClaimedOutboxRow> rows, string reason, CancellationToken cancellationToken = default)
        {
            Released.AddRange(rows);
            ReleaseReason = reason;
            return System.Threading.Tasks.Task.FromResult(rows.Count);
        }

        public Task<int> MarkAppliedThroughAsync(Guid workspaceId, Guid documentId, long documentVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SearchOutboxRow?> GetAsync(Guid workspaceId, long outboxId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTasks(IReadOnlyList<ClaimedIndexTask> claimable) : IIndexChunkTaskRepository
    {
        public IReadOnlyList<ClaimedIndexTask> Claimed => claimable;

        public List<Guid> Dispatched { get; } = [];

        public List<Guid> Released { get; } = [];

        public Task<IReadOnlyList<ClaimedIndexTask>> ClaimForDispatchAsync(
            Guid workspaceId, string owner, int limit, TimeSpan claimDuration, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(claimable);

        public Task<int> MarkDispatchedAsync(Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default)
        {
            Dispatched.AddRange(taskIds);
            return System.Threading.Tasks.Task.FromResult(taskIds.Count);
        }

        public Task<int> ReleaseClaimAsync(
            Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, TimeSpan retryAfter, CancellationToken cancellationToken = default)
        {
            Released.AddRange(taskIds);
            return System.Threading.Tasks.Task.FromResult(taskIds.Count);
        }

        public Task<IndexTaskLeaseResult> LeaseAsync(
            Guid workspaceId, Guid taskId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CompleteAsync(IndexTaskLease lease, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IndexTaskFailureResult> FailAsync(IndexTaskLease lease, ChunkError failure, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IndexChunkTaskInfo?> GetAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IndexChunkTaskInfo>> GetByJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
