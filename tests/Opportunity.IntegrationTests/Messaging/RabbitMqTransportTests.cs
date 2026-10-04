using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Messaging;
using Opportunity.Testing.RabbitMq;

using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.IntegrationTests.Messaging;

/// <summary>E06-T01 acceptance against a real broker: confirms, ack/retry/DLQ/parking policy, recovery, trace context.</summary>
[Collection(RabbitMqCollectionDefinition.Name)]
public sealed class RabbitMqTransportTests(RabbitMqFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bootstrap_declares_quorum_topology_idempotently()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var topology = RabbitMqTopology.Build(harness.Options);

        // Second run (every migrator start) must succeed against the existing definitions.
        await new RabbitMqTopologyBootstrapStep(
            harness.Connections, harness.Options, NullLogger<RabbitMqTopologyBootstrapStep>.Instance, null)
            .RunAsync(Ct);

        foreach (var queue in topology.Queues)
        {
            (await harness.CountAsync(queue.Name)).Should().Be(0);
        }

        await using var channel = await harness.Admin.CreateChannelAsync(cancellationToken: Ct);
        var classic = () => channel.QueueDeclareAsync(
            WorkQueues.IndexSecurity.Name, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" }, cancellationToken: Ct);
        await classic.Should().ThrowAsync<OperationInterruptedException>("the lane queue already exists as a quorum queue");
    }

    [Fact]
    public async Task Published_message_is_confirmed_consumed_and_acked()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var received = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (message, _) =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        });

        var outgoing = MessagingHarness.ChunkTask() with { Attempt = 2 };
        var envelope = await harness.Publisher.PublishAsync(outgoing, Ct);
        var message = await received.Task.WaitAsync(MessagingHarness.Patience, Ct);

        message.Payload.Should().Be(outgoing.Payload);
        message.Envelope.MessageId.Should().Be(envelope.MessageId);
        envelope.MessageId.Version.Should().Be(7);
        message.Envelope.IdempotencyKey.Should().Be(outgoing.IdempotencyKey);
        message.Envelope.WorkspaceId.Should().Be(outgoing.Correlation.WorkspaceId);
        message.Envelope.JobId.Should().Be(outgoing.Correlation.JobId);
        message.Envelope.Attempt.Should().Be(2);
        message.Envelope.SchemaVersion.Should().Be(new SchemaVersion(1, 0));
        message.Redelivered.Should().BeFalse();
        message.TransportRetry.Should().Be(0);
        await WaitUntilAsync(async () => await harness.CountAsync(WorkQueues.IndexBulk.Name) == 0);
    }

    [Fact]
    public async Task Publishing_to_an_unbound_queue_is_not_reported_as_success()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri, queues: [WorkQueues.IndexBulk]);

        var publish = () => harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(WorkQueues.Import), Ct);

        await publish.Should().ThrowAsync<MessagePublishException>().WithMessage("*could not route*");
        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
    }

    [Fact]
    public async Task Publish_is_successful_only_after_a_broker_confirm_across_a_broker_restart()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(taskId: Guid.Empty), Ct);
        var admin = harness.Admin;

        try
        {
            await fixture.StopBrokerAsync(Ct);
            var whileDown = () => harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
            await whileDown.Should().ThrowAsync<MessagePublishException>();
        }
        finally
        {
            await fixture.StartBrokerAsync(Ct);
        }

        // The client recovers on its own; publishing succeeds again once a confirm arrives.
        await WaitUntilAsync(async () =>
        {
            try
            {
                await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(taskId: Guid.AllBitsSet), Ct);
                return true;
            }
            catch (MessagePublishException)
            {
                return false;
            }
        });

        // The message confirmed before the restart survived it (durable quorum queue); none from the outage exists.
        await using var factoryConnection = await new ConnectionFactory { Uri = vhost.AmqpUri }.CreateConnectionAsync(Ct);
        await using var channel = await factoryConnection.CreateChannelAsync(cancellationToken: Ct);
        var taskIds = new List<Guid>();
        while (await channel.BasicGetAsync(WorkQueues.IndexBulk.Name, autoAck: true, Ct) is { } result)
        {
            var read = harness.Serializer.Read(result.Body);
            taskIds.Add(((IndexChunkTaskMessage)read.Payload!).TaskId);
        }

        taskIds.Should().Contain(Guid.Empty).And.Contain(Guid.AllBitsSet);
        taskIds.Should().OnlyContain(id => id == Guid.Empty || id == Guid.AllBitsSet);
        admin.IsOpen.Should().BeFalse("the broker restart closed every connection");
    }

    [Fact]
    public async Task Failing_handler_retries_with_backoff_then_lands_in_the_dlq_with_original_headers_and_error()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var attempts = new ConcurrentQueue<(DateTime At, int TransportRetry)>();
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (message, _) =>
        {
            attempts.Enqueue((DateTime.UtcNow, message.TransportRetry));
            throw new InvalidOperationException("index row not loadable");
        });

        var published = await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
        var dead = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));

        attempts.Select(a => a.TransportRetry).Should().Equal(0, 1, 2, 3);
        var times = attempts.Select(a => a.At).ToList();
        var delays = harness.Options.RetryDelays();
        for (var i = 1; i < times.Count; i++)
        {
            (times[i] - times[i - 1]).Should().BeGreaterThanOrEqualTo(delays[i - 1] * 0.9, "retry {0} backs off", i);
        }

        // Original envelope (incl. its trace headers) unchanged, transport metadata and error added.
        var deadEnvelope = harness.Serializer.Read(dead.Body).Envelope!;
        deadEnvelope.MessageId.Should().Be(published.MessageId);
        deadEnvelope.Headers.Should().BeEquivalentTo(published.Headers);
        Encoding.UTF8.GetString(dead.Body.Span).Should().Be(Encoding.UTF8.GetString(MessageSerializer.Serialize(published)));
        dead.BasicProperties.MessageId.Should().Be(published.MessageId.ToString());
        dead.BasicProperties.Type.Should().Be(MessageTypes.IndexChunkTask);
        Header(dead, TransportHeaders.FailureReason).Should().Be(FailureReasons.RetriesExhausted);
        Header(dead, TransportHeaders.Error).Should().Be("index row not loadable");
        Header(dead, TransportHeaders.ErrorType).Should().Be(typeof(InvalidOperationException).FullName);
        Header(dead, TransportHeaders.Queue).Should().Be(WorkQueues.IndexBulk.Name);
        (await harness.CountAsync(WorkQueues.IndexBulk.Name)).Should().Be(0);
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_then_acked()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var done = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (message, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new TimeoutException("transient");
            }

            done.TrySetResult(message);
            return Task.CompletedTask;
        });

        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
        var second = await done.Task.WaitAsync(MessagingHarness.Patience, Ct);

        second.TransportRetry.Should().Be(1);
        await Task.Delay(500, Ct);
        calls.Should().Be(2);
        (await harness.CountAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk))).Should().Be(0);
    }

    [Fact]
    public async Task Permanent_failure_is_dead_lettered_without_retries()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var calls = 0;
        await harness.SubscribeAsync(WorkQueues.Import, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new PermanentMessageException("envelope workspace does not match the job row");
        });

        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(WorkQueues.Import), Ct);
        var dead = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.Import));

        calls.Should().Be(1);
        Header(dead, TransportHeaders.FailureReason).Should().Be(FailureReasons.Permanent);
        Header(dead, TransportHeaders.Error).Should().Contain("does not match");
    }

    [Fact]
    public async Task Poison_message_goes_to_the_dlq_without_reaching_the_handler()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var calls = 0;
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        await harness.PublishRawAsync(WorkQueues.IndexBulk, Encoding.UTF8.GetBytes("{\"not\":\"an envelope\"}"));
        var dead = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));

        Encoding.UTF8.GetString(dead.Body.Span).Should().Be("{\"not\":\"an envelope\"}");
        Header(dead, TransportHeaders.FailureReason).Should().Be(FailureReasons.Malformed);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Message_that_keeps_crashing_consumers_is_dead_lettered_by_the_delivery_limit()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);

        // A consumer that dies before acking: the broker requeues and counts the failed delivery.
        await using var channel = await harness.Admin.CreateChannelAsync(cancellationToken: Ct);
        for (var delivery = 0; delivery <= harness.Options.DeliveryLimit; delivery++)
        {
            var result = await channel.BasicGetAsync(WorkQueues.IndexBulk.Name, autoAck: false, Ct);
            if (result is null)
            {
                break;
            }

            await channel.BasicNackAsync(result.DeliveryTag, multiple: false, requeue: true, Ct);
        }

        var dead = await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));
        dead.BasicProperties.Headers.Should().ContainKey("x-death");
        (await harness.CountAsync(WorkQueues.IndexBulk.Name)).Should().Be(0);
    }

    [Fact]
    public async Task Unsupported_schema_major_and_unknown_type_are_parked_not_acked()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var calls = 0;
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        var future = Envelope(MessageTypes.IndexChunkTask, new SchemaVersion(2, 0));
        await harness.PublishRawAsync(WorkQueues.IndexBulk, MessageSerializer.Serialize(future));
        var parkedMajor = await harness.GetAsync(RabbitMqTopology.ParkingQueue("index"));
        await harness.PublishRawAsync(WorkQueues.IndexBulk, MessageSerializer.Serialize(Envelope("indexing.somethingNew", new SchemaVersion(1, 0))));
        var parkedType = await harness.GetAsync(RabbitMqTopology.ParkingQueue("index"));

        Header(parkedMajor, TransportHeaders.FailureReason).Should().Be(FailureReasons.UnsupportedSchemaVersion);
        harness.Serializer.Read(parkedMajor.Body).Envelope!.MessageId.Should().Be(future.MessageId);
        Header(parkedType, TransportHeaders.FailureReason).Should().Be(FailureReasons.UnknownMessageType);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Previous_minor_and_newer_minor_with_extra_fields_are_consumed()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var received = new ConcurrentQueue<ReceivedMessage>();
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (message, _) =>
        {
            received.Enqueue(message);
            return Task.CompletedTask;
        });

        var newer = Envelope(MessageTypes.IndexChunkTask, new SchemaVersion(1, 9)) with
        {
            Payload = JsonSerializer.SerializeToElement(new JsonObject { ["taskId"] = Guid.NewGuid(), ["addedInMinor9"] = true }),
        };
        await harness.PublishRawAsync(WorkQueues.IndexBulk, MessageSerializer.Serialize(newer));

        await WaitUntilAsync(() => Task.FromResult(received.Count == 1));
        received.Single().Envelope.SchemaVersion.Should().Be(new SchemaVersion(1, 9));
    }

    [Fact]
    public async Task Consumer_survives_a_connection_cut_and_gets_the_unacked_message_redelivered()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUriVia(proxy), vhost.AmqpUri);
        var deliveries = new ConcurrentQueue<ReceivedMessage>();
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redelivered = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.SubscribeAsync(WorkQueues.IndexBulk, async (message, _) =>
        {
            deliveries.Enqueue(message);
            if (deliveries.Count == 1)
            {
                firstArrived.TrySetResult();
                await releaseFirst.Task;
            }
            else
            {
                redelivered.TrySetResult(message);
            }
        });

        var published = await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
        await firstArrived.Task.WaitAsync(MessagingHarness.Patience, Ct);

        await proxy.CutAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await proxy.RestoreAsync(Ct);
        releaseFirst.TrySetResult(); // Its ack goes to a dead channel and is dropped.

        var again = await redelivered.Task.WaitAsync(MessagingHarness.Patience, Ct);
        again.Envelope.MessageId.Should().Be(published.MessageId);
        again.Redelivered.Should().BeTrue();
        await WaitUntilAsync(async () => await harness.CountAsync(WorkQueues.IndexBulk.Name) == 0);

        // The recovered consumer keeps working. The publisher's connection recovers on its own schedule and fails fast
        // until then (see the test below), so publish once it is back.
        MessageEnvelope? next = null;
        await WaitUntilAsync(async () =>
        {
            try
            {
                next = await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
                return true;
            }
            catch (MessagePublishException)
            {
                return false;
            }
        });
        await WaitUntilAsync(() => Task.FromResult(deliveries.Any(d => d.Envelope.MessageId == next!.MessageId)));
    }

    [Fact]
    public async Task Publisher_fails_while_the_link_is_cut_and_recovers_after()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var proxy = await fixture.CreateFaultProxyAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUriVia(proxy), vhost.AmqpUri);
        await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);

        await proxy.CutAsync(Ct);
        var whileCut = () => harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
        await whileCut.Should().ThrowAsync<MessagePublishException>();

        await proxy.RestoreAsync(Ct);
        await WaitUntilAsync(async () =>
        {
            try
            {
                await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
                return true;
            }
            catch (MessagePublishException)
            {
                return false;
            }
        });

        (await harness.CountAsync(WorkQueues.IndexBulk.Name)).Should().Be(2, "only confirmed publishes exist");
    }

    [Fact]
    public async Task Trace_context_flows_from_publisher_to_handler()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpportunityTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var seen = new TaskCompletionSource<(ActivityTraceId Trace, ActivitySpanId Parent, string? Name, MessageCorrelation Correlation)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.SubscribeAsync(WorkQueues.IndexSecurity, (message, _) =>
        {
            var current = Activity.Current!;
            seen.TrySetResult((current.TraceId, current.ParentSpanId, current.OperationName, message.Correlation));
            return Task.CompletedTask;
        });

        using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var outgoing = MessagingHarness.ChunkTask(WorkQueues.IndexSecurity);
        var envelope = await harness.Publisher.PublishAsync(outgoing, Ct);
        request.Stop();

        var (trace, parent, name, correlation) = await seen.Task.WaitAsync(MessagingHarness.Patience, Ct);
        trace.Should().Be(request.TraceId);
        envelope.Headers[MessageHeaderNames.TraceParent].Should().Contain(request.TraceId.ToHexString());
        envelope.Headers[MessageHeaderNames.TraceParent].Should().Contain(parent.ToHexString(), "the process span's parent is the send span");
        name.Should().Be($"process {WorkQueues.IndexSecurity.Name}");
        correlation.CorrelationId.Should().Be(outgoing.Correlation.CorrelationId);
        correlation.MessageId.Should().Be(envelope.MessageId);
    }

    [Fact]
    public async Task Prefetch_bounds_concurrent_deliveries_per_consumer()
    {
        var queue = new WorkQueue("test.prefetch", "indexing", MessageLane.Bulk, Prefetch: 2);
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri, queues: [queue]);
        var inFlight = 0;
        var maxInFlight = 0;
        var handled = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.SubscribeAsync(queue, async (_, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            await gate.Task;
            Interlocked.Decrement(ref inFlight);
            Interlocked.Increment(ref handled);
        });

        for (var i = 0; i < 6; i++)
        {
            await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(queue), Ct);
        }

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref inFlight) == 2));
        await Task.Delay(300, Ct);
        Volatile.Read(ref maxInFlight).Should().Be(2);
        (await harness.CountAsync(queue.Name)).Should().Be(4, "ready messages wait while two are unacked");

        gate.TrySetResult();
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref handled) == 6));
        maxInFlight.Should().Be(2);
    }

    /// <summary>
    /// A stopping consumer returns its in-flight deliveries charged at most one delivery attempt each. It used to
    /// nack-requeue them while still registered, so the broker handed them (and the ready backlog) straight back to be
    /// nacked again; one stop could exhaust <c>x-delivery-limit</c> and dead-letter healthy work.
    /// </summary>
    [Fact]
    public async Task Stopping_a_consumer_mid_batch_charges_each_delivery_at_most_once_and_dead_letters_nothing()
    {
        var queue = new WorkQueue("test.shutdown", "indexing", MessageLane.Bulk, Prefetch: 4);
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri, queues: [queue]);
        const int Messages = 12;
        for (var i = 0; i < Messages; i++)
        {
            await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(queue), Ct);
        }

        var hanging = 0;
        var subscription = await harness.Consumer.SubscribeAsync(queue, async (_, ct) =>
        {
            Interlocked.Increment(ref hanging);
            await Task.Delay(Timeout.Infinite, ct);
        }, Ct);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref hanging) == queue.Prefetch));
        await subscription.DisposeAsync();

        var deliveryCounts = new ConcurrentDictionary<Guid, int>();
        await harness.SubscribeAsync(queue, (message, _) =>
        {
            deliveryCounts[message.Envelope.MessageId] = message.DeliveryCount;
            return Task.CompletedTask;
        });
        await WaitUntilAsync(async () => deliveryCounts.Count + await harness.CountAsync(RabbitMqTopology.DeadLetterQueue(queue)) >= Messages);

        (await harness.CountAsync(RabbitMqTopology.DeadLetterQueue(queue))).Should().Be(0, "a stop is not a crash loop");
        deliveryCounts.Should().HaveCount(Messages);
        deliveryCounts.Values.Should().OnlyContain(count => count <= 1, "one stop returns each delivery once");
        deliveryCounts.Values.Count(count => count == 1).Should().Be(queue.Prefetch, "only the in-flight deliveries were returned");
    }

    [Fact]
    public async Task Worker_credentials_can_consume_retry_and_dead_letter_but_not_publish_work()
    {
        await using var vhost = await fixture.CreateVirtualHostAsync(Ct);
        var permissions = RabbitMqPermissions.Worker("index");
        var workerUri = await vhost.CreateUserAsync(permissions.Configure, permissions.Write, permissions.Read, cancellationToken: Ct);
        await using var harness = await MessagingHarness.StartAsync(workerUri, vhost.AmqpUri);
        var calls = 0;
        await harness.SubscribeAsync(WorkQueues.IndexBulk, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("always");
        });

        // Work arrives from the dispatcher (admin here); the worker retries through index.retry and dead-letters via index.dlx.
        var dispatcherOptions = MessagingHarness.FastOptions(vhost.AmqpUri);
        await using (var dispatcherConnections = new RabbitMqConnections(dispatcherOptions, NullLogger<RabbitMqConnections>.Instance))
        await using (var dispatcher = new RabbitMqMessagePublisher(dispatcherConnections, dispatcherOptions, harness.Serializer, TimeProvider.System))
        {
            await dispatcher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
        }

        await harness.GetAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexBulk));
        calls.Should().Be(1 + harness.Options.MaxTransportRetries);

        var publishWork = () => harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(), Ct);
        await publishWork.Should().ThrowAsync<MessagePublishException>("only the dispatcher may publish work (ADR-015 D9.5)");
    }

    private static MessageEnvelope Envelope(string messageType, SchemaVersion version) => new()
    {
        MessageId = Guid.CreateVersion7(),
        MessageType = messageType,
        SchemaVersion = version,
        CorrelationId = "corr",
        IdempotencyKey = "idem",
        CreatedAt = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(new JsonObject { ["taskId"] = Guid.NewGuid() }),
    };

    private static string? Header(BasicGetResult result, string name)
    {
        var headers = result.BasicProperties.Headers;
        return headers is not null && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => value?.ToString(),
            }
            : null;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

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
}
