using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Opportunity.Messaging;

/// <summary>
/// <see cref="IMessageConsumer"/> over RabbitMQ with manual acknowledgement, per-queue prefetch and the delivery policy
/// documented on the port. Retries, dead-letters and parking are confirmed publishes made before the original
/// delivery is acked, so a crash in between yields a duplicate, never a loss; if such a publish fails the delivery is
/// requeued and the queue's <c>x-delivery-limit</c> bounds the loop. Consumers declare nothing.
/// </summary>
public sealed partial class RabbitMqMessageConsumer : IMessageConsumer, IAsyncDisposable, IDisposable
{
    private readonly RabbitMqConnections _connections;
    private readonly RabbitMqOptions _options;
    private readonly MessageSerializer _serializer;
    private readonly TimeProvider _time;
    private readonly ILogger<RabbitMqMessageConsumer> _logger;
    private readonly ConfirmedChannel _republish;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public RabbitMqMessageConsumer(
        RabbitMqConnections connections,
        RabbitMqOptions options,
        MessageSerializer serializer,
        TimeProvider time,
        ILogger<RabbitMqMessageConsumer> logger)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _republish = new ConfirmedChannel(connections, options);
        _retryDelays = options.RetryDelays();
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        WorkQueue queue, ReceivedMessageHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(handler);

        var prefetch = _options.PrefetchFor(queue);
        var connection = await _connections.GetAsync(ConnectionPurpose.Consume, cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: prefetch),
            cancellationToken).ConfigureAwait(false);

        var subscription = new Subscription(this, queue, handler, channel);
        try
        {
            await channel.BasicQosAsync(0, prefetch, global: false, cancellationToken).ConfigureAwait(false);
            await subscription.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        LogSubscribed(_logger, queue.Name, prefetch);
        return subscription;
    }

    public ValueTask DisposeAsync() => _republish.DisposeAsync();

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task ProcessAsync(
        Subscription subscription, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        var queue = subscription.Queue;
        var headers = delivery.BasicProperties.Headers;
        var transportRetry = TransportHeaders.ReadInt(headers, TransportHeaders.TransportRetry);
        var deliveryCount = TransportHeaders.ReadInt(headers, TransportHeaders.DeliveryCount);

        var read = _serializer.Read(delivery.Body);
        switch (read.Status)
        {
            case MessageReadStatus.Malformed:
            case MessageReadStatus.InvalidPayload:
                LogRejected(_logger, queue.Name, read.Status, read.Error);
                await FailAsync(subscription, delivery, DeadLetter(queue), Reason(read.Status), "Envelope", read.Error, transportRetry)
                    .ConfigureAwait(false);
                return;
            case MessageReadStatus.UnknownMessageType:
            case MessageReadStatus.UnsupportedSchemaVersion:
                LogParked(_logger, queue.Name, read.Envelope?.MessageType, read.Envelope?.SchemaVersion.ToString(), read.Error);
                await FailAsync(subscription, delivery, Parking(queue), Reason(read.Status), "Envelope", read.Error, transportRetry)
                    .ConfigureAwait(false);
                return;
        }

        var envelope = read.Envelope!;
        var message = new ReceivedMessage(envelope, read.Payload!, queue, delivery.Redelivered, deliveryCount, transportRetry);
        var correlation = message.Correlation;
        using var activity = MessageTracePropagation.StartProcess(envelope.MessageType, queue.Name, correlation, envelope.Headers);
        using var scope = _logger.BeginScope(correlation.ToLogScope());

        try
        {
            await subscription.Handler(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            await subscription.SettleAsync(delivery, ack: false, requeue: true).ConfigureAwait(false);
            return;
        }
        catch (PermanentMessageException ex)
        {
            MarkFailed(activity, ex);
            LogPermanentFailure(_logger, queue.Name, envelope.MessageType, ex);
            await FailAsync(subscription, delivery, DeadLetter(queue), FailureReasons.Permanent, ex.GetType().FullName, ex.Message, transportRetry)
                .ConfigureAwait(false);
            return;
        }
#pragma warning disable CA1031 // Any handler failure is turned into a retry or dead-letter; nothing may escape to the client.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MarkFailed(activity, ex);
            if (transportRetry < _retryDelays.Count)
            {
                var delay = _retryDelays[transportRetry];
                LogRetrying(_logger, queue.Name, envelope.MessageType, transportRetry + 1, delay, ex);
                var target = new Target(
                    RabbitMqTopology.RetryExchange(queue.Area), RabbitMqTopology.RetryTier(delay));
                await FailAsync(subscription, delivery, target, reason: null, ex.GetType().FullName, ex.Message, transportRetry + 1)
                    .ConfigureAwait(false);
            }
            else
            {
                LogRetriesExhausted(_logger, queue.Name, envelope.MessageType, transportRetry, ex);
                await FailAsync(subscription, delivery, DeadLetter(queue), FailureReasons.RetriesExhausted, ex.GetType().FullName, ex.Message, transportRetry)
                    .ConfigureAwait(false);
            }

            return;
        }

        await subscription.SettleAsync(delivery, ack: true, requeue: false).ConfigureAwait(false);
    }

    /// <summary>Re-publishes the delivery unchanged (plus failure headers) to <paramref name="target"/>, then acks it.</summary>
    private async Task FailAsync(
        Subscription subscription,
        BasicDeliverEventArgs delivery,
        Target target,
        string? reason,
        string? errorType,
        string? error,
        int transportRetry)
    {
        var properties = new BasicProperties(delivery.BasicProperties);
        var headers = delivery.BasicProperties.Headers is { } original
            ? new Dictionary<string, object?>(original, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        headers[TransportHeaders.Queue] = subscription.Queue.Name;
        headers[TransportHeaders.TransportRetry] = transportRetry;
        headers[TransportHeaders.FailedAt] = MessageSerializer.FormatTimestamp(_time.GetUtcNow());
        if (reason is not null)
        {
            headers[TransportHeaders.FailureReason] = reason;
        }

        if (errorType is not null)
        {
            headers[TransportHeaders.ErrorType] = errorType;
        }

        if (error is not null)
        {
            headers[TransportHeaders.Error] = error.Length > TransportHeaders.MaxErrorLength ? error[..TransportHeaders.MaxErrorLength] : error;
        }

        properties.Headers = headers;
        properties.Persistent = true;

        try
        {
            // Not the delivery's token: once handling is over, a shutdown must not abandon a half-done hand-off.
            await _republish.PublishAsync(target.Exchange, target.RoutingKey, properties, delivery.Body, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (MessagePublishException ex)
        {
            LogHandOffFailed(_logger, subscription.Queue.Name, target.Exchange, ex);
            await subscription.SettleAsync(delivery, ack: false, requeue: true).ConfigureAwait(false);
            return;
        }

        await subscription.SettleAsync(delivery, ack: true, requeue: false).ConfigureAwait(false);
    }

    private static Target DeadLetter(WorkQueue queue) => new(RabbitMqTopology.DeadLetterExchange(queue.Area), queue.Name);

    private static Target Parking(WorkQueue queue) =>
        new(RabbitMqTopology.DeadLetterExchange(queue.Area), RabbitMqTopology.ParkingRoutingKey);

    private static string Reason(MessageReadStatus status) => status switch
    {
        MessageReadStatus.Malformed => FailureReasons.Malformed,
        MessageReadStatus.InvalidPayload => FailureReasons.InvalidPayload,
        MessageReadStatus.UnknownMessageType => FailureReasons.UnknownMessageType,
        MessageReadStatus.UnsupportedSchemaVersion => FailureReasons.UnsupportedSchemaVersion,
        _ => status.ToString(),
    };

    private static void MarkFailed(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity?.SetTag(TelemetryAttributes.ErrorType, exception.GetType().Name);
    }

    private readonly record struct Target(string Exchange, string RoutingKey);

    private sealed class Subscription(
        RabbitMqMessageConsumer owner, WorkQueue queue, ReceivedMessageHandler handler, IChannel channel) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly object _sync = new();
        private TaskCompletionSource? _drained;
        private int _inFlight;
        private string? _consumerTag;
        private int _disposed;

        public WorkQueue Queue { get; } = queue;

        public ReceivedMessageHandler Handler { get; } = handler;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnReceivedAsync;
            _consumerTag = await channel.BasicConsumeAsync(
                Queue.Name,
                autoAck: false,
                consumerTag: string.Create(CultureInfo.InvariantCulture, $"{owner._options.ClientName}:{Queue.Name}:{Guid.NewGuid():N}"),
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer: consumer,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task SettleAsync(BasicDeliverEventArgs delivery, bool ack, bool requeue)
        {
            try
            {
                if (ack)
                {
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
                }
                else
                {
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: requeue).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException or ObjectDisposedException)
            {
                // The channel died with the delivery unsettled: the broker redelivers it (at-least-once).
                LogSettleFailed(owner._logger, Queue.Name, ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stopping.CancelAsync().ConfigureAwait(false);
            if (_consumerTag is not null && channel.IsOpen)
            {
                try
                {
                    await channel.BasicCancelAsync(_consumerTag).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException)
                {
                    // Channel already gone; nothing to cancel.
                }
            }

            Task drained;
            lock (_sync)
            {
                _drained = _inFlight == 0 ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained?.Task ?? Task.CompletedTask;
            }

            await drained.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            try
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or IOException)
            {
                // Already closed.
            }

            await channel.DisposeAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }

        private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
        {
            lock (_sync)
            {
                _inFlight++;
            }

            try
            {
                if (_stopping.IsCancellationRequested)
                {
                    await SettleAsync(delivery, ack: false, requeue: true).ConfigureAwait(false);
                    return;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, delivery.CancellationToken);
                await owner.ProcessAsync(this, delivery, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (--_inFlight == 0)
                    {
                        _drained?.TrySetResult();
                    }
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue} with prefetch {Prefetch}")]
    private static partial void LogSubscribed(ILogger logger, string queue, ushort prefetch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettering a message from {Queue}: {Status} {Error}")]
    private static partial void LogRejected(ILogger logger, string queue, MessageReadStatus status, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parking a message from {Queue}: {MessageType} {SchemaVersion} is not supported ({Error})")]
    private static partial void LogParked(ILogger logger, string queue, string? messageType, string? schemaVersion, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permanent failure handling {MessageType} from {Queue}; dead-lettering")]
    private static partial void LogPermanentFailure(ILogger logger, string queue, string messageType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling {MessageType} from {Queue} failed; transport retry {Retry} in {Delay}")]
    private static partial void LogRetrying(ILogger logger, string queue, string messageType, int retry, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling {MessageType} from {Queue} failed after {Retries} transport retries; dead-lettering")]
    private static partial void LogRetriesExhausted(ILogger logger, string queue, string messageType, int retries, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not hand a message from {Queue} off to {Exchange}; requeueing it")]
    private static partial void LogHandOffFailed(ILogger logger, string queue, string exchange, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not settle a delivery from {Queue}; the broker will redeliver it")]
    private static partial void LogSettleFailed(ILogger logger, string queue, Exception exception);
}
