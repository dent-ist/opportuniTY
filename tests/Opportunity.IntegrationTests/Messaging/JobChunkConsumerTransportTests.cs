using System.Collections.Concurrent;
using System.Text;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Jobs;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Jobs.Faults;
#endif
using Opportunity.Messaging;
using Opportunity.Testing.RabbitMq;

using RabbitMQ.Client;

namespace Opportunity.IntegrationTests.Messaging;

/// <summary>
/// E06-T05 end to end: the job chunk consumer composed on the real RabbitMQ transport (E06-T01) and the PostgreSQL job
/// engine (E06-T02) — duplicates, redelivery, poison messages and a worker crashing mid-handler.
/// </summary>
[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class JobChunkConsumerTransportTests(RabbitMqFixture fixture, MigrationPostgresFixture postgres)
    : IClassFixture<MigrationPostgresFixture>
{
    private static readonly WorkQueue Queue = WorkQueues.BulkCoding;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Duplicate_messages_commit_every_chunk_exactly_once_and_are_all_acked()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(3);
        var executor = new StateBasedExecutor();
        var audit = new InMemoryAuditEventWriter();
        await using var worker = await ChunkWorker.StartAsync(vhost.AmqpUri, db.Chunks, executor, audit, "worker-dup");

        foreach (var chunk in await db.ChunksAsync(ws, job))
        {
            for (var copy = 0; copy < 3; copy++)
            {
                await harness.Publisher.PublishAsync(ChunkMessages.Outgoing(chunk), Ct);
            }

            await db.Chunks.MarkDispatchedAsync(ws, chunk.ChunkId, Ct);
        }

        await WaitUntilAsync(async () => (await db.JobAsync(ws, job)).Status == JobStatus.Completed);
        await WaitUntilAsync(async () => await harness.CountAsync(Queue.Name) == 0);

        executor.Executions.Values.Should().Equal(1, 1, 1);
        (await db.JobAsync(ws, job)).Counters.ItemsApplied.Should().Be(3 * 99);
        (await db.ChunksAsync(ws, job)).Should().OnlyContain(c => c.AttemptCount == 1);
        (await harness.CountAsync(RabbitMqTopology.DeadLetterQueue(Queue))).Should().Be(0);
        audit.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_delivery_requeued_by_shutdown_is_redelivered_and_commits_once_without_charging_the_attempt()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(1);
        var chunk = (await db.ChunksAsync(ws, job))[0];
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = new ConcurrentQueue<bool>();
        var executor = new StateBasedExecutor
        {
            Before = async (context, ct) =>
            {
                deliveries.Enqueue(context.Message.Redelivered);
                if (deliveries.Count == 1)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
            },
        };

        var first = await ChunkWorker.StartAsync(vhost.AmqpUri, db.Chunks, executor, new InMemoryAuditEventWriter(), "worker-stopping");
        await harness.Publisher.PublishAsync(ChunkMessages.Outgoing(chunk), Ct);
        await started.Task.WaitAsync(MessagingHarness.Patience, Ct);
        await first.DisposeAsync();
        (await db.ChunksAsync(ws, job))[0].Status.Should().Be(JobChunkStatus.Pending, "shutdown releases the claim");

        await using var second = await ChunkWorker.StartAsync(vhost.AmqpUri, db.Chunks, executor, new InMemoryAuditEventWriter(), "worker-next");
        await WaitUntilAsync(async () => (await db.JobAsync(ws, job)).Status == JobStatus.Completed);

        deliveries.Should().Equal(false, true);
        var committed = (await db.ChunksAsync(ws, job))[0];
        committed.Status.Should().Be(JobChunkStatus.Committed);
        committed.AttemptCount.Should().Be(1, "the released attempt was not charged");
        (await db.JobAsync(ws, job)).Counters.ItemsApplied.Should().Be(99);
    }

    [Fact]
    public async Task A_forged_message_is_dead_lettered_and_audited_and_the_genuine_one_still_runs()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(1);
        var otherWorkspace = await db.Core.CreateWorkspaceAsync();
        var chunk = (await db.ChunksAsync(ws, job))[0];
        var executor = new StateBasedExecutor();
        var audit = new InMemoryAuditEventWriter();
        await using var worker = await ChunkWorker.StartAsync(vhost.AmqpUri, db.Chunks, executor, audit, "worker-forged");

        await harness.Publisher.PublishAsync(ChunkMessages.Outgoing(chunk, workspaceId: otherWorkspace), Ct);

        var dead = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(Queue));
        Header(dead, TransportHeaders.FailureReason).Should().Be(FailureReasons.Permanent);
        Header(dead, TransportHeaders.Error).Should().StartWith(JobChunkConsumer.RejectionReason);
        audit.Events.Should().ContainSingle().Which.Action.Should().Be(AuditTaxonomy.Integrity.EnvelopeMismatch);
        executor.Executions.Should().BeEmpty();
        var untouched = (await db.ChunksAsync(ws, job))[0];
        untouched.AttemptCount.Should().Be(0);
        untouched.LeaseToken.Should().Be(0);

        await harness.Publisher.PublishAsync(ChunkMessages.Outgoing(chunk), Ct);
        await WaitUntilAsync(async () => (await db.JobAsync(ws, job)).Status == JobStatus.Completed);
        audit.Events.Should().ContainSingle();
    }

#if OPPORTUNITY_FAILPOINTS
    [Fact]
    public async Task A_poison_message_that_crashes_every_attempt_ends_with_the_chunk_failed_not_an_endless_loop()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(1, maxAttempts: 3);
        var chunk = (await db.ChunksAsync(ws, job))[0];
        var lease = new JobLeaseOptions { LeaseDuration = TimeSpan.FromMilliseconds(500) };

        // Each attempt claims, then "crashes" once its lease has run out; the transport redelivers the message.
        var crashes = new CrashAt(Failpoints.AfterClaim, before: () => Task.Delay(TimeSpan.FromMilliseconds(700), Ct));
        await using var worker = await ChunkWorker.StartAsync(
            vhost.AmqpUri, db.Chunks, new StateBasedExecutor(), new InMemoryAuditEventWriter(), "worker-poison", lease, crashes);
        await harness.Publisher.PublishAsync(ChunkMessages.Outgoing(chunk), Ct);

        await WaitUntilAsync(async () => (await db.ChunksAsync(ws, job))[0].Status == JobChunkStatus.Failed);
        await WaitUntilAsync(async () => await harness.CountAsync(Queue.Name) == 0);

        var failed = (await db.ChunksAsync(ws, job))[0];
        failed.ErrorClass.Should().Be(ChunkErrorClass.AttemptsExhausted);
        failed.AttemptCount.Should().Be(3);
        crashes.Count.Should().Be(3);
        (await db.JobAsync(ws, job)).Status.Should().Be(JobStatus.CompletedWithErrors, "its only chunk failed");
    }

    [Fact]
    public async Task A_worker_crashing_mid_handler_loses_nothing_and_its_late_commit_is_fenced_off()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(1);
        var chunk = (await db.ChunksAsync(ws, job))[0];
        var executor = new StateBasedExecutor();

        // Worker A reaches the commit, then its process "dies": the link to the broker is cut and the handler hangs.
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var crashA = new Hook(async (failpoint, _) =>
        {
            if (failpoint == Failpoints.BeforeCommit)
            {
                await proxy.CutAsync(CancellationToken.None);
                hung.TrySetResult();
                await resume.Task;
            }
            else if (failpoint == Failpoints.AfterCommit)
            {
                lateCommit.TrySetResult();
            }
        });
        await using var workerA = await ChunkWorker.StartAsync(
            vhost.AmqpUriVia(proxy), db.Chunks, executor, new InMemoryAuditEventWriter(), "worker-a", faults: crashA);
        await harness.Publisher.PublishAsync(ChunkMessages.Outgoing(chunk), Ct);
        await hung.Task.WaitAsync(MessagingHarness.Patience, Ct);

        // The broker redelivers A's unacked message to worker B, whose claim finds A's live lease and drops it.
        var redelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observeB = new Hook((failpoint, context) =>
        {
            if (failpoint == Failpoints.BeforeClaim && context.Message.Redelivered)
            {
                redelivered.TrySetResult();
            }

            return Task.CompletedTask;
        });
        await using var workerB = await ChunkWorker.StartAsync(
            vhost.AmqpUri, db.Chunks, executor, new InMemoryAuditEventWriter(), "worker-b", faults: observeB);
        await redelivered.Task.WaitAsync(MessagingHarness.Patience, Ct);
        await WaitUntilAsync(async () => await harness.CountAsync(Queue.Name) == 0);
        (await db.ChunksAsync(ws, job))[0].Status.Should().Be(JobChunkStatus.Running, "A still holds the lease");

        // A's lease expires; the sweeper returns the chunk and the dispatcher publishes it again; B commits it.
        await db.ExpireAsync(chunk.ChunkId, TimeSpan.FromMinutes(1));
        (await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).ReturnedToPending.Should().Be(1);
        await harness.Publisher.PublishAsync(ChunkMessages.Outgoing((await db.ChunksAsync(ws, job))[0]), Ct);
        await WaitUntilAsync(async () => (await db.JobAsync(ws, job)).Status == JobStatus.Completed);

        // A wakes up and tries to commit with its stale fencing token: nothing of it is recorded.
        resume.TrySetResult();
        await lateCommit.Task.WaitAsync(MessagingHarness.Patience, Ct);

        executor.Executions[chunk.ChunkId].Should().Be(2, "A and B both did the work");
        var committed = (await db.ChunksAsync(ws, job))[0];
        committed.Status.Should().Be(JobChunkStatus.Committed);
        committed.AttemptCount.Should().Be(2);
        var counters = (await db.JobAsync(ws, job)).Counters;
        counters.ChunksCommitted.Should().Be(1);
        counters.ItemsApplied.Should().Be(99, "only one commit counts");
        counters.ItemsSkippedConcurrentEdit.Should().Be(1);
        (await db.Jobs.GetItemResultsAsync(new JobItemResultQuery(ws, job), Ct)).Should().ContainSingle();
    }

    private sealed class CrashAt(string failpoint, Func<Task> before) : IFaultInjector
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public async ValueTask HitAsync(string name, FailpointContext context, CancellationToken cancellationToken)
        {
            if (name == failpoint)
            {
                await before();
                Interlocked.Increment(ref _count);
                throw new SimulatedCrashException();
            }
        }
    }

    private sealed class Hook(Func<string, FailpointContext, Task> hit) : IFaultInjector
    {
        public async ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken) =>
            await hit(failpoint, context);
    }
#endif

    private static string? Header(BasicGetResult result, string name) =>
        result.BasicProperties.Headers is { } headers && headers.TryGetValue(name, out var value)
            ? value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value?.ToString()
            : null;

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + MessagingHarness.Patience;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(100, Ct);
        }
    }

    /// <summary>
    /// One worker process: the RabbitMQ transport, the job chunk consumer bound to the bulk-coding queue and its
    /// hosted subscription, composed exactly as a worker module does.
    /// </summary>
    private sealed class ChunkWorker : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly List<IHostedService> _hosted;
        private int _disposed;

        private ChunkWorker(ServiceProvider provider, List<IHostedService> hosted)
        {
            _provider = provider;
            _hosted = hosted;
        }

        public static async Task<ChunkWorker> StartAsync(
            Uri amqp,
            IJobChunkRepository chunks,
            IJobChunkExecutor executor,
            IAuditEventWriter audit,
            string workerId,
            JobLeaseOptions? lease = null
#if OPPORTUNITY_FAILPOINTS
            , IFaultInjector? faults = null
#endif
            )
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRabbitMqMessaging(MessagingHarness.FastOptions(amqp));
            services.AddSingleton(chunks);
            services.AddSingleton(audit);
            services.AddSingleton(executor);
#if OPPORTUNITY_FAILPOINTS
            if (faults is not null)
            {
                services.AddSingleton(faults);
            }
#endif
            services.AddJobChunkConsumer(new JobChunkConsumerOptions { WorkerId = workerId }, lease);
            services.AddMessageHandler<JobChunkMessage, JobChunkConsumer>(Queue);

            var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted)
            {
                await service.StartAsync(Ct);
            }

            return new ChunkWorker(provider, hosted);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var service in _hosted)
            {
                await service.StopAsync(CancellationToken.None);
            }

            await _provider.DisposeAsync();
        }
    }
}
