using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Messaging;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.Messaging;

[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class MessageHandlerHostingTests(RabbitMqFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registered_handler_receives_its_payload_type_from_a_scope_and_others_are_dead_lettered()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var sink = new HandledMessages();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(sink);
        services.AddRabbitMqMessaging(MessagingHarness.FastOptions(vhost.AmqpUri))
            .AddMessageHandler<IndexChunkTaskMessage, RecordingHandler>(WorkQueues.IndexBulk);
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single();
        await hosted.StartAsync(Ct);
        try
        {
            var outgoing = MessagingHarness.ChunkTask();
            await harness.Publisher.PublishAsync(outgoing, Ct);
            await harness.Publisher.PublishAsync(
                new OutgoingMessage<JobChunkMessage>(
                    WorkQueues.IndexBulk,
                    new JobChunkMessage { ChunkId = Guid.NewGuid(), Sequence = 1, Operation = JobChunkOperation.IndexChunk },
                    new MessageCorrelation("corr"),
                    "idem"),
                Ct);

            var handled = await sink.First.Task.WaitAsync(MessagingHarness.Patience, Ct);
            handled.Should().Be(outgoing.Payload);
            var dead = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));
            harness.Serializer.Read(dead.Body).Payload.Should().BeOfType<JobChunkMessage>();
        }
        finally
        {
            await hosted.StopAsync(Ct);
        }
    }

    internal sealed class HandledMessages
    {
        public TaskCompletionSource<IndexChunkTaskMessage> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class RecordingHandler(HandledMessages sink) : IMessageHandler<IndexChunkTaskMessage>
    {
        public Task HandleAsync(IndexChunkTaskMessage payload, ReceivedMessage message, CancellationToken cancellationToken)
        {
            sink.First.TrySetResult(payload);
            return Task.CompletedTask;
        }
    }
}
