using System.Diagnostics.Metrics;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Data.Jobs;
using Opportunity.Data.Messaging;
using Opportunity.Hosting.Operations;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Jobs;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Messaging;
using Opportunity.Testing.RabbitMq;

using RabbitMQ.Client;

namespace Opportunity.IntegrationTests.Messaging;

/// <summary>
/// ADR-010 §7.3 end to end on real RabbitMQ and PostgreSQL: every message that reaches a dead-letter (or parking)
/// queue is copied into PostgreSQL by the dispatcher's recorder — workspace rows under RLS when the envelope names an
/// existing workspace, installation-level rows otherwise — once per message id, while the DLQ keeps its diagnostic copy.
/// </summary>
[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class DeadLetterRecorderTests(RabbitMqFixture fixture, MigrationPostgresFixture postgres)
    : IClassFixture<MigrationPostgresFixture>
{
    private static readonly WorkQueue Queue = WorkQueues.IndexBulk;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_permanently_failed_job_message_is_recorded_once_in_its_workspace_and_stays_in_its_DLQ()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, job) = await db.CreateRunningJobAsync(1);
        var chunk = (await db.ChunksAsync(ws, job))[0].ChunkId;
        await harness.SubscribeAsync(Queue, (_, _) => throw new PermanentMessageException("Envelope workspace differs from the task's."));
        var store = new DeadLetterStore(db.Core.AppDataSource);
        await using var recorder = await Recorder.StartAsync(vhost.AmqpUri, store);

        var sent = await harness.Publisher.PublishAsync(Message(ws, job, chunk), Ct);

        var record = await WaitForAsync(() => store.GetAsync(ws, sent.MessageId.ToString(), Ct));
        record.Queue.Should().Be(Queue.Name);
        record.Exchange.Should().Be("index.dlx");
        record.RoutingKey.Should().Be(Queue.Name);
        record.JobId.Should().Be(job);
        record.SubjectId.Should().Be(chunk);
        record.MessageType.Should().Be(sent.MessageType);
        record.CorrelationId.Should().Be(sent.CorrelationId);
        record.DeathReason.Should().Be(FailureReasons.Permanent);
        record.DeathCount.Should().Be(1);
        record.Error.Should().Be("Envelope workspace differs from the task's.");
        record.ErrorType.Should().EndWith(nameof(PermanentMessageException));
        record.HeadersJson.Should().Contain("\"idempotencyKey\"").And.Contain(TransportHeaders.FailureReason);
        record.BodySize.Should().Be(record.Body.Length).And.BeGreaterThan(0);
        (await store.ListAsync(null, null, 10, Ct)).Should().BeEmpty("the workspace exists, so nothing is installation-level");

        // The diagnostic copy stays in the DLQ; the recorder's copy is consumed.
        await WaitUntilAsync(async () => await harness.CountAsync(RabbitMqTopology.DeadLetterRecordQueue) == 0);
        (await harness.CountAsync(RabbitMqTopology.DeadLetterQueue(Queue))).Should().Be(1);

        // A duplicate of the same dead-letter (at-least-once hand-off) changes nothing.
        var copy = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(Queue));
        await using (var channel = await harness.Admin.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), Ct))
        {
            await channel.BasicPublishAsync("index.dlx", Queue.Name, mandatory: true, new BasicProperties(copy.BasicProperties), copy.Body, Ct);
        }

        await WaitUntilAsync(async () => await harness.CountAsync(RabbitMqTopology.DeadLetterRecordQueue) == 0);
        (await store.ListAsync(ws, job, 10, Ct)).Should().ContainSingle();
        recorder.Recorded.Should().Be(1, "opportunity.dlq.messages counts new records only");

        // The job's failures listing shows the record while its chunk is failed, and no longer once it is replayed.
        var operations = new JobOperationsStore(db.Core.AppDataSource);
        (await operations.ListFailuresAsync(ws, job, null, 10, Ct)).Should().BeEmpty("the chunk is not failed (yet)");
        var failing = (await db.Chunks.ClaimNextAsync(ws, job, "worker-1", JobDatabase.Lease, Ct)).Chunk!;
        failing.Lease.ChunkId.Should().Be(chunk);
        await db.Chunks.FailAsync(failing.Lease, ChunkError.Permanent("Poison", "poison"), Ct);
        (await operations.ListFailuresAsync(ws, job, null, 10, Ct)).Select(f => (f.Source, f.Id)).Should().BeEquivalentTo(
            [(JobFailureSource.Chunk, chunk.ToString()), (JobFailureSource.DeadLetter, sent.MessageId.ToString())]);
        (await operations.ListFailuresAsync(ws, job, null, 10, Ct)).Single(f => f.Source == JobFailureSource.DeadLetter).Error
            .Should().Be($"{Queue.Name}: permanent — Envelope workspace differs from the task's.");
        await operations.ReplayFailedAsync(ws, job, OperationsActor.Cli("test operator"), Ct);
        (await operations.ListFailuresAsync(ws, job, null, 10, Ct)).Should().BeEmpty("replay resets PostgreSQL; the record stays for diagnostics");
        (await store.ListAsync(ws, job, 10, Ct)).Should().ContainSingle();

        // The operations CLI lists and shows it.
        var (code, output, _) = await CliAsync(db, "dlq", "list", "--workspace", ws.ToString(), "--job", job.ToString());
        code.Should().Be(JobOperationsCli.ExitSuccess);
        output.Should().Contain(sent.MessageId.ToString()).And.Contain(Queue.Name).And.Contain("permanent");
        (code, output, _) = await CliAsync(db, "dlq", "show", "--workspace", ws.ToString(), "--message", sent.MessageId.ToString());
        code.Should().Be(JobOperationsCli.ExitSuccess);
        output.Should().Contain("Envelope workspace differs").And.Contain("idempotencyKey").And.Contain(chunk.ToString());
        (await CliAsync(db, "dlq", "show", "--workspace", ws.ToString(), "--message", "nope")).Code.Should().Be(JobOperationsCli.ExitNotFound);
    }

    [Fact]
    public async Task Malformed_unknown_workspace_and_broker_dead_letters_are_recorded_at_installation_level()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await using var db = await JobDatabase.CreateAsync(postgres);
        var store = new DeadLetterStore(db.Core.AppDataSource);
        await using var recorder = await Recorder.StartAsync(vhost.AmqpUri, store);

        // A message that keeps crashing its consumer: the broker dead-letters it at the delivery limit (x-death).
        var unknownWorkspace = Guid.CreateVersion7();
        var crashing = await harness.Publisher.PublishAsync(Message(unknownWorkspace, Guid.CreateVersion7(), Guid.CreateVersion7()), Ct);
        await using (var channel = await harness.Admin.CreateChannelAsync(cancellationToken: Ct))
        {
            for (var delivery = 0; delivery <= harness.Options.DeliveryLimit; delivery++)
            {
                if (await channel.BasicGetAsync(Queue.Name, autoAck: false, Ct) is not { } result)
                {
                    break;
                }

                await channel.BasicNackAsync(result.DeliveryTag, multiple: false, requeue: true, Ct);
            }
        }

        var brokerDead = await WaitForAsync(() => store.GetAsync(null, crashing.MessageId.ToString(), Ct));
        brokerDead.DeathReason.Should().Be("delivery_limit");
        brokerDead.Queue.Should().Be(Queue.Name);
        brokerDead.WorkspaceId.Should().BeNull();
        brokerDead.ClaimedWorkspaceId.Should().Be(unknownWorkspace, "a workspace that does not exist gets no tenant row");
        brokerDead.HeadersJson.Should().Contain("x-death");

        // A body that is no envelope: dead-lettered by the consumer as malformed, recorded under a hash of its body.
        await harness.SubscribeAsync(Queue, (_, _) => Task.CompletedTask);
        await harness.PublishRawAsync(Queue, "not an envelope"u8.ToArray());
        var malformed = await WaitForAsync(async () =>
            (await store.ListAsync(null, null, 10, Ct)).SingleOrDefault(r => r.DeathReason == FailureReasons.Malformed));
        malformed.MessageId.Should().StartWith("body-");
        malformed.Body.Should().Equal("not an envelope"u8.ToArray());
        malformed.ClaimedWorkspaceId.Should().BeNull();

        var (code, output, _) = await CliAsync(db, "dlq", "list");
        code.Should().Be(JobOperationsCli.ExitSuccess, "without --workspace the CLI lists installation-level records");
        output.Should().Contain(crashing.MessageId.ToString()).And.Contain(malformed.MessageId);
        (await CliAsync(db, "dlq", "list", "--workspace", "not-a-guid")).Code.Should().Be(JobOperationsCli.ExitUsage);
    }

    [Fact]
    public async Task Records_past_their_retention_are_deleted_in_every_workspace_and_at_installation_level()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var store = new DeadLetterStore(db.Core.AppDataSource);
        var ws = await db.Core.CreateWorkspaceAsync();
        var other = await db.Core.CreateWorkspaceAsync();
        foreach (var (workspace, id) in new[] { ((Guid?)ws, "old-1"), (ws, "new-1"), (other, "old-2"), (null, "old-3"), (null, "new-3") })
        {
            (await store.RecordAsync(Record(workspace, id), Ct)).Should().Be(
                workspace is null ? DeadLetterWriteOutcome.Installation : DeadLetterWriteOutcome.Workspace);
        }

        (await store.RecordAsync(Record(ws, "new-1"), Ct)).Should().Be(DeadLetterWriteOutcome.Duplicate);
        await db.Core.ExecuteAsync("UPDATE opportunity.dead_letter SET recorded_at = now() - interval '31 days' WHERE message_id LIKE 'old-%'");
        await db.Core.ExecuteAsync(
            "UPDATE opportunity.dead_letter_installation SET recorded_at = now() - interval '31 days' WHERE message_id LIKE 'old-%'");

        (await store.DeleteRecordedBeforeAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(30), Ct)).Should().Be(3);

        (await store.ListAsync(ws, null, 10, Ct)).Select(r => r.MessageId).Should().Equal("new-1");
        (await store.ListAsync(other, null, 10, Ct)).Should().BeEmpty();
        (await store.ListAsync(null, null, 10, Ct)).Select(r => r.MessageId).Should().Equal("new-3");
    }

    [Fact]
    public async Task Workspace_records_are_isolated_by_row_level_security()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var store = new DeadLetterStore(db.Core.AppDataSource);
        var ws = await db.Core.CreateWorkspaceAsync();
        var other = await db.Core.CreateWorkspaceAsync();
        await store.RecordAsync(Record(ws, "mine") with { HeadersJson = "{\"x\":\"\\u0000\"}" }, Ct);

        (await store.GetAsync(other, "mine", Ct)).Should().BeNull();
        (await store.GetAsync(null, "mine", Ct)).Should().BeNull();
        (await store.GetAsync(ws, "mine", Ct))!.HeadersJson.Should().Be("{\"unstorable\": true}",
            "headers PostgreSQL rejects are replaced instead of blocking the recorder");
    }

    private static OutgoingMessage<IndexChunkTaskMessage> Message(Guid workspaceId, Guid jobId, Guid taskId) => new(
        Queue,
        new IndexChunkTaskMessage { TaskId = taskId },
        new MessageCorrelation($"corr-{Guid.NewGuid():N}", WorkspaceId: workspaceId, JobId: jobId),
        IdempotencyKey: Guid.NewGuid().ToString("N"));

    private static DeadLetterMessage Record(Guid? workspaceId, string messageId) => new(
        messageId, Queue.Name, "index.dlx", Queue.Name, workspaceId, null, null, "indexing.chunkTask", "corr-1", FailureReasons.Permanent, 1,
        null, "boom", null, "{}", "{}"u8.ToArray(), 2);

    private static async Task<(int Code, string Output, string Error)> CliAsync(JobDatabase db, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await JobOperationsCli.RunAsync(args, output, error, db.Core.AppConnectionString, Ct);
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> read)
        where T : class
    {
        var deadline = DateTime.UtcNow + MessagingHarness.Patience;
        while (true)
        {
            if (await read() is { } value)
            {
                return value;
            }

            DateTime.UtcNow.Should().BeBefore(deadline, "the recorder should store the message within the patience window");
            await Task.Delay(100, Ct);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + MessagingHarness.Patience;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>The recorder as the dispatcher composes it, with a counter on <c>opportunity.dlq.messages</c>.</summary>
    private sealed class Recorder : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IHostedService _service;
        private readonly MeterListener _listener = new();
        private long _recorded;

        private Recorder(ServiceProvider provider, IHostedService service)
        {
            _provider = provider;
            _service = service;
        }

        public long Recorded => Interlocked.Read(ref _recorded);

        public static async Task<Recorder> StartAsync(Uri amqp, IDeadLetterStore store)
        {
            var services = new ServiceCollection().AddLogging().AddMetrics();
            services.AddSingleton(sp => new OpportunityMetrics(sp.GetRequiredService<IMeterFactory>()));
            services.AddSingleton(store);
            services.AddRabbitMqMessaging(MessagingHarness.FastOptions(amqp))
                .AddDeadLetterRecorder(new DeadLetterRecorderOptions { MaxRetryDelay = TimeSpan.FromSeconds(1) });
            var provider = services.BuildServiceProvider();
            var recorder = new Recorder(provider, provider.GetServices<IHostedService>().Single());
            recorder._listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpportunityTelemetry.MeterName && instrument.Name == "opportunity.dlq.messages")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            recorder._listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref recorder._recorded, value));
            recorder._listener.Start();
            await recorder._service.StartAsync(Ct);
            return recorder;
        }

        public async ValueTask DisposeAsync()
        {
            await _service.StopAsync(CancellationToken.None);
            _listener.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
