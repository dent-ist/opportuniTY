using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Messaging;

using RabbitMQ.Client;

namespace Opportunity.IntegrationTests.Messaging;

/// <summary>
/// The real RabbitMQ transport against one test virtual host: topology declared by the bootstrap step, a publisher and
/// a consumer, plus a raw admin connection for inspecting queues. Retry delays are shortened for test speed.
/// </summary>
internal sealed class MessagingHarness : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly List<IAsyncDisposable> _owned = [];

    private MessagingHarness(RabbitMqOptions options, IConnection admin, EnvelopeSigner? signer, IAuditEventWriter? audit)
    {
        Options = options;
        Admin = admin;
        Connections = new RabbitMqConnections(options, NullLogger<RabbitMqConnections>.Instance);
        Serializer = new MessageSerializer(MessageContracts.CreateRegistry());
        Publisher = new RabbitMqMessagePublisher(Connections, options, Serializer, TimeProvider.System, signer);
        Consumer = new RabbitMqMessageConsumer(
            Connections, options, Serializer, TimeProvider.System, NullLogger<RabbitMqMessageConsumer>.Instance, signer, audit);
    }

    public RabbitMqOptions Options { get; }

    public IConnection Admin { get; }

    public RabbitMqConnections Connections { get; }

    public MessageSerializer Serializer { get; }

    public RabbitMqMessagePublisher Publisher { get; }

    public RabbitMqMessageConsumer Consumer { get; }

    public static RabbitMqOptions FastOptions(Uri uri) => new()
    {
        ConnectionString = uri.ToString(),
        ClientName = "integration-test",
        PublishTimeout = TimeSpan.FromSeconds(5),
        NetworkRecoveryInterval = TimeSpan.FromMilliseconds(500),
        MaxConnectRetryDelay = TimeSpan.FromSeconds(1),
        RetryBaseDelay = TimeSpan.FromMilliseconds(300),
        MaxTransportRetries = 3,
        DeliveryLimit = 3,
    };

    /// <param name="uri">Where the transport connects (possibly a fault proxy or a restricted user).</param>
    /// <param name="adminUri">Direct full-permission URI used to declare the topology and inspect queues.</param>
    /// <param name="signer">E05-T07 envelope signing for publisher and consumer (null: off).</param>
    /// <param name="audit">Receives the consumer's signature rejections.</param>
    public static async Task<MessagingHarness> StartAsync(
        Uri uri, Uri adminUri, Action<RabbitMqOptions>? configure = null, IReadOnlyList<WorkQueue>? queues = null,
        EnvelopeSigner? signer = null, IAuditEventWriter? audit = null)
    {
        var options = FastOptions(uri);
        configure?.Invoke(options);

        var adminOptions = FastOptions(adminUri);
        configure?.Invoke(adminOptions);
        adminOptions.ConnectionString = adminUri.ToString();
        await using (var adminConnections = new RabbitMqConnections(adminOptions, NullLogger<RabbitMqConnections>.Instance))
        {
            await new RabbitMqTopologyBootstrapStep(
                adminConnections, adminOptions, NullLogger<RabbitMqTopologyBootstrapStep>.Instance, queues).RunAsync(Ct);
        }

        var admin = await new ConnectionFactory { Uri = adminUri, AutomaticRecoveryEnabled = false }.CreateConnectionAsync(Ct);
        return new MessagingHarness(options, admin, signer, audit);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static OutgoingMessage<IndexChunkTaskMessage> ChunkTask(WorkQueue? queue = null, Guid? taskId = null) => new(
        queue ?? WorkQueues.IndexBulk,
        new IndexChunkTaskMessage { TaskId = taskId ?? Guid.NewGuid() },
        new MessageCorrelation($"corr-{Guid.NewGuid():N}", WorkspaceId: Guid.NewGuid(), JobId: Guid.NewGuid()),
        IdempotencyKey: Guid.NewGuid().ToString("N"));

    public async Task<IAsyncDisposable> SubscribeAsync(WorkQueue queue, ReceivedMessageHandler handler)
    {
        var subscription = await Consumer.SubscribeAsync(queue, handler, Ct);
        _owned.Add(subscription);
        return subscription;
    }

    /// <summary>Takes one message from <paramref name="queue"/> (auto-ack), waiting up to <see cref="Patience"/>.</summary>
    public async Task<BasicGetResult> GetAsync(string queue, TimeSpan? timeout = null)
    {
        await using var channel = await Admin.CreateChannelAsync(cancellationToken: Ct);
        var deadline = DateTime.UtcNow + (timeout ?? Patience);
        while (true)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true, Ct);
            if (result is not null)
            {
                return result;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No message arrived in '{queue}'.");
            }

            await Task.Delay(50, Ct);
        }
    }

    public async Task<uint> CountAsync(string queue)
    {
        await using var channel = await Admin.CreateChannelAsync(cancellationToken: Ct);
        return (await channel.QueueDeclarePassiveAsync(queue, Ct)).MessageCount;
    }

    /// <summary>Publishes raw bytes to the work exchange, bypassing the envelope writer.</summary>
    public async Task PublishRawAsync(WorkQueue queue, byte[] body)
    {
        await using var channel = await Admin.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), Ct);
        await channel.BasicPublishAsync(
            RabbitMqTopology.WorkExchange, queue.Name, mandatory: true, new BasicProperties { Persistent = true }, body, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }

        await Consumer.DisposeAsync();
        await Publisher.DisposeAsync();
        await Connections.DisposeAsync();
        await Admin.DisposeAsync();
    }
}
