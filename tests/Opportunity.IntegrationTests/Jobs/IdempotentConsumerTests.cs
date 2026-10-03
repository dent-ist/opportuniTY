using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.IntegrationTests.Migrations;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Jobs.Faults;
#endif

namespace Opportunity.IntegrationTests.Jobs;

/// <summary>
/// E06-T05 acceptance against PostgreSQL: the job chunk consumer driven directly with deliveries, as the transport
/// would hand them over (the broker round trip is covered by <c>JobChunkConsumerTransportTests</c>).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class IdempotentConsumerTests(MigrationPostgresFixture postgres)
{
    private const int ChunkCount = 8;
    private const int MaxAttempts = 25;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

#if OPPORTUNITY_FAILPOINTS
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Delivering_every_message_1_to_3_times_with_random_crashes_yields_the_single_delivery_state(int seed)
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var audit = new InMemoryAuditEventWriter();

        // Reference: every message delivered exactly once, no faults.
        var (referenceWs, referenceJob) = await db.CreateRunningJobAsync(ChunkCount, MaxAttempts);
        var reference = new StateBasedExecutor();
        foreach (var chunk in await db.ChunksAsync(referenceWs, referenceJob))
        {
            await ChunkMessages.Consumer(db.Chunks, reference, audit).HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk), Ct);
        }

        var expected = await JobOutcome.CaptureAsync(db, referenceWs, referenceJob);
        expected.Status.Should().Be(JobStatus.Completed);
        expected.Counters.ItemsApplied.Should().Be(ChunkCount * 99);

        // Same job again: 1–3 copies of every message in random order, random crashes before and after the commit.
        var (ws, job) = await db.CreateRunningJobAsync(ChunkCount, MaxAttempts);
        var random = new Random(seed);
        var faults = new RandomCrashes(random, probability: 0.35, maxPerChunk: 4);
        var executor = new StateBasedExecutor();
        var broker = new List<ReceivedMessage>();
        foreach (var chunk in await db.ChunksAsync(ws, job))
        {
            broker.AddRange(Enumerable.Repeat(chunk, random.Next(1, 4)).Select(c => ChunkMessages.Received(c)));
        }

        for (var round = 0; ; round++)
        {
            round.Should().BeLessThan(50, "the job must converge");
            while (broker.Count > 0)
            {
                var index = random.Next(broker.Count);
                var message = broker[index];
                broker.RemoveAt(index);
                try
                {
                    await ChunkMessages.Consumer(db.Chunks, executor, audit, faults: faults)
                        .HandleAsync((JobChunkMessage)message.Payload, message, Ct);
                }
                catch (SimulatedCrashException)
                {
                    // The process died before acking: the broker delivers the message again.
                    broker.Add(message with { Redelivered = true });
                }
            }

            if ((await db.JobAsync(ws, job)).Status != JobStatus.Running)
            {
                break;
            }

            // Crashed claims still hold a lease and their redeliveries were dropped as LeaseHeld: the lease sweeper
            // returns them to Pending once the lease expired and the dispatcher (E06-T04) publishes them again.
            foreach (var running in (await db.ChunksAsync(ws, job)).Where(c => c.Status == JobChunkStatus.Running))
            {
                await db.ExpireAsync(running.ChunkId, TimeSpan.FromMinutes(5));
            }

            await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct);
            var dispatchable = (await db.Chunks.GetDispatchableAsync(ws, job, 100, Ct)).Select(d => d.ChunkId).ToHashSet();
            foreach (var chunk in (await db.ChunksAsync(ws, job)).Where(c => dispatchable.Contains(c.ChunkId)))
            {
                await db.Chunks.MarkDispatchedAsync(ws, chunk.ChunkId, Ct);
                broker.Add(ChunkMessages.Received(chunk));
            }
        }

        var actual = await JobOutcome.CaptureAsync(db, ws, job);
        actual.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());
        await db.AssertCountersMatchRowsAsync(ws, job);
        faults.Crashes.Should().BePositive("the run must actually have crashed");
        audit.Events.Should().BeEmpty();
    }

    /// <summary>Crashes at a random failpoint with <c>probability</c>, at most <c>maxPerChunk</c> times per chunk.</summary>
    private sealed class RandomCrashes(Random random, double probability, int maxPerChunk) : IFaultInjector
    {
        private readonly Dictionary<Guid, int> _perChunk = [];

        public int Crashes { get; private set; }

        public ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken)
        {
            var chunkId = ((JobChunkMessage)context.Message.Payload).ChunkId;
            var crashes = _perChunk.GetValueOrDefault(chunkId);
            if (crashes < maxPerChunk && random.NextDouble() < probability)
            {
                _perChunk[chunkId] = crashes + 1;
                Crashes++;
                throw new SimulatedCrashException($"Crash at {failpoint}.");
            }

            return ValueTask.CompletedTask;
        }
    }
#endif

    [Fact]
    public async Task A_permanent_error_skips_retries_and_marks_the_chunk_failed_with_its_last_error()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(2);
        var executor = new StateBasedExecutor
        {
            Before = (context, _) => context.Chunk.Sequence == 2
                ? throw new PermanentChunkException("FieldNotFound", "Field 1005 no longer exists.")
                : Task.CompletedTask,
        };
        var chunks = await db.ChunksAsync(ws, job);

        foreach (var chunk in chunks)
        {
            await ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter())
                .HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk), Ct);
        }

        // A redelivery of the failed chunk's message is a duplicate: no retry.
        await ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter())
            .HandleAsync(ChunkMessages.Payload(chunks[1]), ChunkMessages.Received(chunks[1], redelivered: true), Ct);

        var failed = (await db.ChunksAsync(ws, job))[1];
        failed.Status.Should().Be(JobChunkStatus.Failed);
        failed.ErrorClass.Should().Be(ChunkErrorClass.Permanent);
        failed.ErrorCode.Should().Be("FieldNotFound");
        failed.LastError.Should().Be("Field 1005 no longer exists.");
        failed.AttemptCount.Should().Be(1, "permanent errors are not retried");
        executor.Executions[failed.ChunkId].Should().Be(1);
        (await db.Chunks.GetDispatchableAsync(ws, job, 10, Ct)).Should().BeEmpty();
        var info = await db.JobAsync(ws, job);
        info.Status.Should().Be(JobStatus.CompletedWithErrors);
        info.Counters.ChunksFailed.Should().Be(1);
        info.Counters.ChunksCommitted.Should().Be(1);
    }

    [Fact]
    public async Task A_transient_error_waits_in_postgres_and_the_next_dispatch_commits_the_chunk()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(1);
        var attempts = 0;
        var executor = new StateBasedExecutor
        {
            Before = (_, _) => ++attempts == 1 ? throw new TimeoutException("OpenSearch did not answer.") : Task.CompletedTask,
        };
        var chunk = (await db.ChunksAsync(ws, job))[0];

        await ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter())
            .HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk), Ct);
        var waiting = (await db.ChunksAsync(ws, job))[0];
        waiting.Status.Should().Be(JobChunkStatus.RetryWait);
        waiting.ErrorClass.Should().Be(ChunkErrorClass.Transient);
        waiting.LastError.Should().Contain("OpenSearch did not answer.");

        // Before the backoff elapsed a redelivery is dropped; once due, the dispatcher's message commits it.
        await ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter())
            .HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk, redelivered: true), Ct);
        attempts.Should().Be(1);
        await db.ExpireAsync(chunk.ChunkId, TimeSpan.FromSeconds(1));
        await ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter())
            .HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk), Ct);

        var committed = (await db.ChunksAsync(ws, job))[0];
        committed.Status.Should().Be(JobChunkStatus.Committed);
        committed.AttemptCount.Should().Be(2);
        (await db.JobAsync(ws, job)).Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task A_message_whose_envelope_workspace_disagrees_with_the_row_is_rejected_audited_and_writes_nothing()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        await using var app = await db.AsAppRoleAsync();
        var (ws, job) = await db.CreateRunningJobAsync(1);
        var otherWorkspace = await db.Core.CreateWorkspaceAsync();
        var chunk = (await db.ChunksAsync(ws, job))[0];
        var executor = new StateBasedExecutor();
        var audit = new InMemoryAuditEventWriter();

        // Valid chunk and job of workspace 1, envelope claiming workspace 2; the worker runs as the RLS-bound app role.
        var handle = () => ChunkMessages.Consumer(app.Chunks, executor, audit)
            .HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk, workspaceId: otherWorkspace), Ct);

        await handle.Should().ThrowAsync<PermanentMessageException>("the transport dead-letters it");
        executor.Executions.Should().BeEmpty();
        var untouched = (await db.ChunksAsync(ws, job))[0];
        untouched.Status.Should().Be(JobChunkStatus.Pending);
        untouched.AttemptCount.Should().Be(0);
        untouched.LeaseToken.Should().Be(0);
        var rejected = audit.Events.Should().ContainSingle().Subject;
        rejected.Category.Should().Be(AuditTaxonomy.Integrity.Category);
        rejected.Action.Should().Be(AuditTaxonomy.Integrity.MessageRejected);
        rejected.WorkspaceId.Should().BeNull();
        rejected.ReasonCode.Should().Be("EnvelopeMismatch");
        rejected.Details["claimedWorkspaceId"].Should().Be(otherWorkspace.ToString());
        rejected.Details["claimedJobId"].Should().Be(job.ToString());

        // The genuine message still runs.
        await ChunkMessages.Consumer(app.Chunks, executor, audit).HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk), Ct);
        (await db.ChunksAsync(ws, job))[0].Status.Should().Be(JobChunkStatus.Committed);
    }

    [Fact]
    public async Task A_workspace_leaving_Active_stops_the_chunk_at_the_next_fence_without_committing()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(2);
        var chunks = await db.ChunksAsync(ws, job);
        var executor = new StateBasedExecutor
        {
            // Deletion starts while the first chunk is running: fence F2 stops it before any side effect.
            Before = (_, _) => db.Core.ExecuteAsync(
                "UPDATE opportunity.workspace SET status = 'Deleting', closed_at = now() WHERE workspace_id = @ws", ("ws", ws)),
        };

        await ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter())
            .HandleAsync(ChunkMessages.Payload(chunks[0]), ChunkMessages.Received(chunks[0]), Ct);
        var stopped = (await db.ChunksAsync(ws, job))[0];
        stopped.Status.Should().Be(JobChunkStatus.Pending, "released at the fence");
        stopped.AttemptCount.Should().Be(0, "a fence stop does not charge the attempt");

        // Fence F1: no further chunk of a Deleting workspace is claimed.
        await ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter())
            .HandleAsync(ChunkMessages.Payload(chunks[1]), ChunkMessages.Received(chunks[1]), Ct);
        executor.Executions.Should().ContainSingle();
        var info = await db.JobAsync(ws, job);
        info.Counters.ChunksCommitted.Should().Be(0);
        info.Counters.ItemsApplied.Should().Be(0);
    }
}
