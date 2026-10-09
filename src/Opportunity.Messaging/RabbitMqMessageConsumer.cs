using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Logging;

#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Application.Audit;
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
/// <para>
/// E05-T07: each queue is consumed, and its retries and dead-letters published, with its area's own broker user when
/// one is configured (<see cref="RabbitMqOptions.AreaConnectionStrings"/>). With envelope signing on, a delivery whose
/// HMAC is missing, invalid or under an unknown key never reaches the handler: it is audited as
/// <c>Integrity.MessageRejected</c> and dead-lettered.
/// </para>
/// </summary>
public sealed partial class RabbitMqMessageConsumer : IMessageConsumer, IAsyncDisposable, IDisposable
{
    private readonly RabbitMqConnections _connections;
    private readonly RabbitMqOptions _options;
    private readonly MessageSerializer _serializer;
    private readonly TimeProvider _time;
    private readonly ILogger<RabbitMqMessageConsumer> _logger;
    private readonly ConcurrentDictionary<string, ConfirmedChannel> _republish = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly EnvelopeSigner _signer;
    private readonly IAuditEventWriter? _audit;

    /// <param name="signer">Verifies envelope signatures; required when <see cref="MessageSigningOptions.Enabled"/>.</param>
    /// <param name="audit">Records signature rejections (<c>Integrity.MessageRejected</c>); without it they are only logged.</param>
    public RabbitMqMessageConsumer(
        RabbitMqConnections connections,
        RabbitMqOptions options,
        MessageSerializer serializer,
        TimeProvider time,
        ILogger<RabbitMqMessageConsumer> logger,
        EnvelopeSigner? signer = null,
        IAuditEventWriter? audit = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryDelays = options.RetryDelays();
        _signer = signer ?? (options.Signing.Enabled
            ? throw new ArgumentException("Envelope signing is enabled; pass the EnvelopeSigner.", nameof(signer))
            : EnvelopeSigner.Disabled);
        _audit = audit;
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        WorkQueue queue, ReceivedMessageHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(handler);

        var prefetch = _options.PrefetchFor(queue);
        var connection = await _connections.GetAsync(ConnectionPurpose.Consume, _options.CredentialFor(queue.Area), cancellationToken)
            .ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _republish.Values)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _republish.Clear();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task ProcessAsync(
        Subscription subscription, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        var queue = subscription.Queue;
        var headers = delivery.BasicProperties.Headers;
        var transportRetry = TransportHeaders.ReadInt(headers, TransportHeaders.TransportRetry);
        var deliveryCount = TransportHeaders.ReadInt(headers, TransportHeaders.DeliveryCount);

        if (_signer.Enabled && !await VerifySignatureAsync(subscription, delivery, transportRetry, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

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
#if OPPORTUNITY_FAILPOINTS
        catch (SimulatedCrashException) when (cancellationToken.IsCancellationRequested)
        {
            // Test builds (E18-T01): the worker process "died" at a failpoint while its host was being killed. Like a real
            // kill, the delivery stays unsettled and the broker redelivers it once the channel closes.
            activity?.SetStatus(ActivityStatusCode.Error, "simulated crash");
            LogLeftForRedelivery(_logger, queue.Name, envelope.MessageType);
            return;
        }
#endif
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: leave the delivery unsettled; closing the channel returns it (see Subscription.DisposeAsync).
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            LogLeftForRedelivery(_logger, queue.Name, envelope.MessageType);
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
            await RetryOrDeadLetterAsync(subscription, delivery, envelope.MessageType, ex, transportRetry).ConfigureAwait(false);
            return;
        }

        await subscription.SettleAsync(delivery, ack: true, requeue: false).ConfigureAwait(false);
    }

    /// <summary>
    /// E05-T07: true when the delivery's envelope signature is valid. Otherwise the rejection is audited and the delivery
    /// dead-lettered (false); if the audit cannot be written the delivery takes the transport retry path, so the evidence
    /// is never dropped.
    /// </summary>
    private async Task<bool> VerifySignatureAsync(
        Subscription subscription, BasicDeliverEventArgs delivery, int transportRetry, CancellationToken cancellationToken)
    {
        var queue = subscription.Queue;
        SignatureCheck check;
        try
        {
            check = await _signer.VerifyAsync(queue.Name, delivery.Body, delivery.BasicProperties.Headers, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
#pragma warning disable CA1031 // A key that cannot be loaded is a configuration error: retry, then dead-letter.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await RetryOrDeadLetterAsync(subscription, delivery, "signature", ex, transportRetry).ConfigureAwait(false);
            return false;
        }

        if (check == SignatureCheck.Valid)
        {
            return true;
        }

        var (reason, failure) = check switch
        {
            SignatureCheck.Missing => (MessageRejectionReasons.SignatureMissing, FailureReasons.SignatureMissing),
            SignatureCheck.UnknownKey => (MessageRejectionReasons.SignatureKeyUnknown, FailureReasons.SignatureKeyUnknown),
            _ => (MessageRejectionReasons.SignatureInvalid, FailureReasons.SignatureInvalid),
        };

        // The claims are read for the audit details only; nothing in an unauthenticated envelope is acted on.
        var claimed = _serializer.Read(delivery.Body).Envelope;
        LogSignatureRejected(_logger, queue.Name, reason, claimed?.MessageType);
        if (_audit is not null)
        {
            try
            {
                await _audit.WriteAsync(
                    MessageRejection.AuditEvent(
                        claimed,
                        queue,
                        reason,
                        _options.ClientName,
                        "Message",
                        claimed?.MessageId.ToString() ?? delivery.BasicProperties.MessageId,
                        _time.GetUtcNow(),
                        new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["keyId"] = TransportHeaders.ReadString(delivery.BasicProperties.Headers, TransportHeaders.SignatureKeyId),
                        }),
                    CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Retried through the transport: the rejection must be recorded before the message goes.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await RetryOrDeadLetterAsync(subscription, delivery, claimed?.MessageType ?? "unknown", ex, transportRetry).ConfigureAwait(false);
                return false;
            }
        }
        else
        {
            LogRejectionNotAudited(_logger, queue.Name, reason);
        }

        await FailAsync(subscription, delivery, DeadLetter(queue), failure, "Signature", reason, transportRetry).ConfigureAwait(false);
        return false;
    }

    private async Task RetryOrDeadLetterAsync(
        Subscription subscription, BasicDeliverEventArgs delivery, string messageType, Exception ex, int transportRetry)
    {
        var queue = subscription.Queue;
        if (transportRetry < _retryDelays.Count)
        {
            var delay = _retryDelays[transportRetry];
            LogRetrying(_logger, queue.Name, messageType, transportRetry + 1, delay, ex);
            var target = new Target(RabbitMqTopology.RetryExchange(queue.Area), RabbitMqTopology.RetryTier(delay));
            await FailAsync(subscription, delivery, target, reason: null, ex.GetType().FullName, ex.Message, transportRetry + 1).ConfigureAwait(false);
        }
        else
        {
            LogRetriesExhausted(_logger, queue.Name, messageType, transportRetry, ex);
            await FailAsync(subscription, delivery, DeadLetter(queue), FailureReasons.RetriesExhausted, ex.GetType().FullName, ex.Message, transportRetry)
                .ConfigureAwait(false);
        }
    }

    private ConfirmedChannel Republish(WorkQueue queue)
    {
        var credential = _options.CredentialFor(queue.Area);
        return _republish.GetOrAdd(credential ?? string.Empty, _ => new ConfirmedChannel(_connections, _options, credential));
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
            await Republish(subscription.Queue).PublishAsync(target.Exchange, target.RoutingKey, properties, delivery.Body, CancellationToken.None)
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

        /// <summary>
        /// Stops consuming. In-flight handlers are cancelled and their deliveries, like any prefetched but not yet
        /// started, are deliberately left unsettled until the channel closes. A <c>basic.nack(requeue)</c> here would
        /// count as a failed delivery against the quorum queue's <c>x-delivery-limit</c>, and the broker may hand the
        /// message straight back to this still-registered consumer, which would nack it again: a stopping worker would
        /// dead-letter healthy work. Deliveries returned by the channel close are not counted as failed.
        /// </summary>
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
                    // Prefetched after the stop began: not started, not settled; the channel close returns it.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejecting a message from {Queue}: {Reason} ({MessageType}); dead-lettering")]
    private static partial void LogSignatureRejected(ILogger logger, string queue, string reason, string? messageType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No audit writer in this process: the {Reason} rejection on {Queue} is not audited")]
    private static partial void LogRejectionNotAudited(ILogger logger, string queue, string reason);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handling {MessageType} from {Queue} was cancelled by shutdown; the delivery returns to the queue when the channel closes")]
    private static partial void LogLeftForRedelivery(ILogger logger, string queue, string messageType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not settle a delivery from {Queue}; the broker will redeliver it")]
    private static partial void LogSettleFailed(ILogger logger, string queue, Exception exception);
}
