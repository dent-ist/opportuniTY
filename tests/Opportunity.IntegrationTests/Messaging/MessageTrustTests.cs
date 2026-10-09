using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Keys;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Messaging;
using Opportunity.Testing.RabbitMq;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.IntegrationTests.Messaging;

/// <summary>
/// E05-T07 acceptance against a real broker: HMAC envelope signing (missing and invalid signatures are dead-lettered and
/// audited, the handler never runs), broker permissions per component (the render worker's user can neither publish to
/// the export exchanges nor read any other queue), and per-area credentials inside one process (ADR-015 D9.5/D9.6).
/// </summary>
[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class MessageTrustTests(RabbitMqFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_signing_enabled_unsigned_and_forged_signatures_are_dead_lettered_and_audited_and_signed_ones_run()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        var secrets = new RandomSecrets("k1");
        var audit = new InMemoryAuditEventWriter();
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri, signer: Signer(secrets), audit: audit);
        var handled = new List<Guid>();
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (message, _) =>
        {
            lock (handled)
            {
                handled.Add(((IndexChunkTaskMessage)message.Payload).TaskId);
            }

            return Task.CompletedTask;
        });

        // 1. No signature: a structurally valid envelope published around the signing publisher.
        var unsigned = Guid.NewGuid();
        await harness.PublishRawAsync(WorkQueues.IndexBulk, MessageSerializer.Serialize(Envelope(unsigned)));
        var missing = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));
        Header(missing, TransportHeaders.FailureReason).Should().Be(FailureReasons.SignatureMissing);

        // 2. A signature made with another key under the accepted key id (a publisher that does not hold the key).
        await using (var impostor = new RabbitMqMessagePublisher(
            harness.Connections, harness.Options, harness.Serializer, TimeProvider.System, Signer(new RandomSecrets("k1"))))
        {
            await impostor.PublishAsync(MessagingHarness.ChunkTask(taskId: Guid.NewGuid()), Ct);
        }

        var invalid = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));
        Header(invalid, TransportHeaders.FailureReason).Should().Be(FailureReasons.SignatureInvalid);

        // 3. The genuine signer's message runs.
        var genuine = Guid.NewGuid();
        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(taskId: genuine), Ct);
        await WaitUntilAsync(() => { lock (handled) { return handled.Count > 0; } });

        lock (handled)
        {
            handled.Should().Equal([genuine], "rejected messages never reach the handler");
        }

        audit.Events.Should().HaveCount(2).And.OnlyContain(e =>
            e.Category == AuditTaxonomy.Integrity.Category && e.Action == AuditTaxonomy.Integrity.MessageRejected
            && e.WorkspaceId == null && e.Outcome == AuditOutcome.Denied && e.Details["queue"] == WorkQueues.IndexBulk.Name);
        audit.Events.Select(e => e.ReasonCode).Should().Equal(MessageRejectionReasons.SignatureMissing, MessageRejectionReasons.SignatureInvalid);
        (await harness.CountAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk))).Should().Be(0);
    }

    /// <summary>
    /// The render worker's broker user (<see cref="RabbitMqPermissions.ForArea"/>) checked against the broker itself: it
    /// consumes <c>render.chunks</c> and publishes to <c>render.retry</c> / <c>render.dlx</c>, and every attempt to publish
    /// to an export (or any other) exchange or to read another queue — including the diagnostic DLQ and parking queues —
    /// is refused with ACCESS_REFUSED.
    /// </summary>
    [Fact]
    public async Task Render_worker_credentials_cannot_publish_to_export_exchanges_nor_read_other_queues()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var render = RabbitMqPermissions.ForArea(WorkQueues.Rendering.Area);
        var renderUri = await vhost.CreateUserAsync(render.Configure, render.Write, render.Read, cancellationToken: Ct);
        await using var connection = await new ConnectionFactory { Uri = renderUri, AutomaticRecoveryEnabled = false }.CreateConnectionAsync(Ct);

        foreach (var exchange in new[] { RabbitMqTopology.RetryExchange("render"), RabbitMqTopology.DeadLetterExchange("render") })
        {
            (await TryPublishAsync(connection, exchange, "render.chunks")).Should().BeNull($"the render worker publishes its own retries to {exchange}");
        }

        (await TryConsumeAsync(connection, WorkQueues.Rendering.Name)).Should().BeNull("the render worker consumes its own queue");

        var forbiddenExchanges = new[]
        {
            RabbitMqTopology.RetryExchange("export"), RabbitMqTopology.DeadLetterExchange("export"), RabbitMqTopology.RetryReturnExchange("export"),
            RabbitMqTopology.WorkExchange, RabbitMqTopology.RetryExchange("production"), RabbitMqTopology.RetryReturnExchange("render"),
        };
        foreach (var exchange in forbiddenExchanges)
        {
            (await TryPublishAsync(connection, exchange, WorkQueues.Export.Name)).Should().Be(403, $"publishing to {exchange} is refused");
        }

        var forbiddenQueues = WorkQueues.All.Where(q => q != WorkQueues.Rendering).Select(q => q.Name)
            .Concat([RabbitMqTopology.DeadLetterQueue(WorkQueues.Rendering), RabbitMqTopology.ParkingQueue("render"),
                RabbitMqTopology.DeadLetterRecordQueue, RabbitMqTopology.RetryQueue("render", harness.Options.RetryDelays()[0])]);
        foreach (var queue in forbiddenQueues)
        {
            (await TryConsumeAsync(connection, queue)).Should().Be(403, $"reading {queue} is refused");
        }

        var declare = async () =>
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
            await channel.QueueDeclareAsync("render.rogue", durable: true, exclusive: false, autoDelete: false, cancellationToken: Ct);
        };
        (await declare.Should().ThrowAsync<OperationInterruptedException>()).Which.ShutdownReason!.ReplyCode.Should().Be(403, "workers configure nothing");
    }

    /// <summary>
    /// A combined worker holding several areas (the Lite profile, AR-04) keeps each area's traffic on its own user: the
    /// default credential is the dispatcher's (publish work, nothing else), the index area's user consumes index.bulk and
    /// publishes its retries and dead-letters. Neither user alone could do the whole round trip.
    /// </summary>
    [Fact]
    public async Task Each_area_uses_its_own_broker_user_inside_one_process()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        var dispatcher = RabbitMqPermissions.Dispatcher;
        var dispatcherUri = await vhost.CreateUserAsync(dispatcher.Configure, dispatcher.Write, dispatcher.Read, cancellationToken: Ct);
        var index = RabbitMqPermissions.ForArea("index");
        var indexUri = await vhost.CreateUserAsync(index.Configure, index.Write, index.Read, cancellationToken: Ct);
        await using var harness = await MessagingHarness.StartAsync(
            dispatcherUri, vhost.AmqpUri, options => options.AreaConnectionStrings["index"] = indexUri.ToString());
        var calls = 0;
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("always");
        });

        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);

        await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));
        calls.Should().Be(1 + harness.Options.MaxTransportRetries, "every retry went through index.retry with the index user");

        // The dispatcher's user still samples queue depths for metrics (a passive declare needs no queue permission).
        await using var connection = await new ConnectionFactory { Uri = dispatcherUri, AutomaticRecoveryEnabled = false }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        (await channel.QueueDeclarePassiveAsync(WorkQueues.IndexBulk.Name, Ct)).MessageCount.Should().Be(0);
    }

    /// <summary>
    /// The Compose profile's broker users (deploy/docker-compose/rabbitmq/users.sh, run by <c>./opportunity.sh up</c>)
    /// applied to a real broker: the render user may consume render.chunks only and the dispatcher may publish work but
    /// read no work queue; a re-run is idempotent. Users get a unique prefix and random passwords and are removed after.
    /// </summary>
    [Fact]
    public async Task The_compose_broker_user_script_grants_each_component_only_its_own_permissions()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var prefix = $"t{Guid.NewGuid():N}"[..12] + "-";
        var render = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var dispatcher = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var script = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "deploy", "docker-compose", "rabbitmq", "users.sh"), Ct);
        string[] run =
        [
            "env", $"OPPORTUNITY_RABBITMQ_VHOST={vhost.Name}", $"OPPORTUNITY_RABBITMQ_USER_PREFIX={prefix}",
            $"RABBITMQ_RENDER_PASSWORD={render}", $"RABBITMQ_DISPATCHER_PASSWORD={dispatcher}", "sh", "-c", script,
        ];
        try
        {
            foreach (var attempt in new[] { 1, 2 })
            {
                var result = await fixture.ExecAsync(run, Ct);
                result.ExitCode.Should().Be(0, "run {0}: {1}", attempt, result.Stderr);
            }

            var renderUri = new UriBuilder(vhost.AmqpUri) { UserName = prefix + "render", Password = render }.Uri;
            await using (var connection = await new ConnectionFactory { Uri = renderUri, AutomaticRecoveryEnabled = false }.CreateConnectionAsync(Ct))
            {
                (await TryConsumeAsync(connection, WorkQueues.Rendering.Name)).Should().BeNull();
                (await TryConsumeAsync(connection, WorkQueues.Export.Name)).Should().Be(403);
                (await TryPublishAsync(connection, RabbitMqTopology.RetryExchange("export"), WorkQueues.Export.Name)).Should().Be(403);
            }

            var dispatcherUri = new UriBuilder(vhost.AmqpUri) { UserName = prefix + "dispatcher", Password = dispatcher }.Uri;
            await using (var connection = await new ConnectionFactory { Uri = dispatcherUri, AutomaticRecoveryEnabled = false }.CreateConnectionAsync(Ct))
            {
                (await TryPublishAsync(connection, RabbitMqTopology.WorkExchange, WorkQueues.Rendering.Name)).Should().BeNull();
                (await TryConsumeAsync(connection, WorkQueues.Rendering.Name)).Should().Be(403);
                (await TryConsumeAsync(connection, RabbitMqTopology.DeadLetterRecordQueue)).Should().BeNull();
            }
        }
        finally
        {
            foreach (var user in new[] { "render", "dispatcher" })
            {
                await fixture.ExecAsync(["rabbitmqctl", "-q", "delete_user", prefix + user], CancellationToken.None);
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }

    private static EnvelopeSigner Signer(ISecretProvider secrets) =>
        new(new MessageSigningOptions { Enabled = true, KeyId = "k1" }, secrets);

    private static MessageEnvelope Envelope(Guid taskId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        MessageType = MessageTypes.IndexChunkTask,
        SchemaVersion = new SchemaVersion(1, 0),
        WorkspaceId = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        CorrelationId = "corr-unsigned",
        IdempotencyKey = taskId.ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { taskId }),
    };

    /// <summary>Null when the broker confirms the publish, else the reply code that closed the channel.</summary>
    private static async Task<int?> TryPublishAsync(IConnection connection, string exchange, string routingKey)
    {
        try
        {
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), Ct);
            await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, new BasicProperties(), "{}"u8.ToArray(), Ct);
            return null;
        }
        catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException or PublishException)
        {
            return (ex as OperationInterruptedException)?.ShutdownReason?.ReplyCode ?? (ex as AlreadyClosedException)?.ShutdownReason?.ReplyCode ?? -1;
        }
    }

    /// <summary>Null when the consumer is registered, else the reply code that closed the channel.</summary>
    private static async Task<int?> TryConsumeAsync(IConnection connection, string queue)
    {
        try
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
            var tag = await channel.BasicConsumeAsync(queue, autoAck: false, new AsyncEventingBasicConsumerStub(channel), Ct);
            await channel.BasicCancelAsync(tag, cancellationToken: Ct);
            return null;
        }
        catch (OperationInterruptedException ex)
        {
            return ex.ShutdownReason?.ReplyCode;
        }
    }

    private static string? Header(BasicGetResult result, string name) =>
        result.BasicProperties.Headers is { } headers && headers.TryGetValue(name, out var value)
            ? value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value?.ToString()
            : null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + MessagingHarness.Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50, Ct);
        }
    }

    private sealed class AsyncEventingBasicConsumerStub(IChannel channel) : AsyncDefaultBasicConsumer(channel);

    /// <summary>Random key material per key id, generated at run time (never a literal).</summary>
    private sealed class RandomSecrets(params string[] keyIds) : ISecretProvider
    {
        private readonly Dictionary<string, byte[]> _values = keyIds.ToDictionary(
            MessageSigningOptions.SecretName, _ => RandomNumberGenerator.GetBytes(32), StringComparer.Ordinal);

        public ValueTask<SecretValue?> GetAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.TryGetValue(name, out var value) ? new SecretValue(value) : null);
    }
}
