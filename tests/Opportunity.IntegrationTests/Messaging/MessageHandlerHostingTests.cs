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

    /// <summary>
    /// E05-T07 wiring: with <c>Messaging:RabbitMq:Signing</c> on, the registered publisher signs with the key from the
    /// host's <see cref="Opportunity.Application.Keys.ISecretProvider"/>, the hosted consumer verifies, and an unsigned
    /// message is dead-lettered and audited through the host's <see cref="Opportunity.Application.Audit.IAuditEventWriter"/>.
    /// </summary>
    [Fact]
    public async Task A_host_with_signing_enabled_signs_verifies_and_audits_through_its_registered_services()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var sink = new HandledMessages();
        var audit = new Opportunity.Application.Audit.InMemoryAuditEventWriter();
        var options = MessagingHarness.FastOptions(vhost.AmqpUri);
        options.Signing.Enabled = true;
        options.Signing.KeyId = "k7";

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(sink);
        services.AddSingleton<Opportunity.Application.Keys.ISecretProvider>(new OneRandomSecret(MessageSigningOptions.SecretName("k7")));
        services.AddSingleton<Opportunity.Application.Audit.IAuditEventWriter>(audit);
        services.AddRabbitMqMessaging(options).AddMessageHandler<IndexChunkTaskMessage, RecordingHandler>(WorkQueues.IndexBulk);
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single();
        await hosted.StartAsync(Ct);
        try
        {
            await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
            var dead = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));
            dead.BasicProperties.Headers![TransportHeaders.FailureReason].Should().BeEquivalentTo("signature-missing"u8.ToArray());
            audit.Events.Should().ContainSingle().Which.ReasonCode.Should().Be(MessageRejectionReasons.SignatureMissing);

            var signed = MessagingHarness.ChunkTask();
            await provider.GetRequiredService<IMessagePublisher>().PublishAsync(signed, Ct);
            (await sink.First.Task.WaitAsync(MessagingHarness.Patience, Ct)).Should().Be(signed.Payload);
        }
        finally
        {
            await hosted.StopAsync(Ct);
        }
    }

    private sealed class OneRandomSecret(string name) : Opportunity.Application.Keys.ISecretProvider
    {
        private readonly byte[] _value = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        public ValueTask<Opportunity.Application.Keys.SecretValue?> GetAsync(string secretName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(secretName == name ? new Opportunity.Application.Keys.SecretValue(_value) : null);
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
