using System.Diagnostics;

using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;

using RabbitMQ.Client;

namespace Opportunity.Messaging;

/// <summary>
/// <see cref="IMessagePublisher"/> over RabbitMQ: builds the envelope (UUIDv7 message id, W3C trace context from a
/// <c>send {queue}</c> producer span), publishes it persistent and mandatory to <see cref="RabbitMqTopology.WorkExchange"/>
/// and completes only after the broker confirm. With envelope signing on (E05-T07) the message carries an HMAC over its
/// body and destination (<see cref="EnvelopeSigner"/>).
/// </summary>
public sealed class RabbitMqMessagePublisher : IMessagePublisher, IAsyncDisposable, IDisposable
{
    private readonly ConfirmedChannel _channel;
    private readonly MessageSerializer _serializer;
    private readonly TimeProvider _time;
    private readonly EnvelopeSigner _signer;

    public RabbitMqMessagePublisher(
        RabbitMqConnections connections, RabbitMqOptions options, MessageSerializer serializer, TimeProvider time, EnvelopeSigner? signer = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(options);
        _channel = new ConfirmedChannel(connections, options);
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _signer = signer ?? (options.Signing.Enabled
            ? throw new ArgumentException("Envelope signing is enabled; pass the EnvelopeSigner.", nameof(signer))
            : EnvelopeSigner.Disabled);
    }

    internal ConfirmedChannel Channel => _channel;

    public async Task<MessageEnvelope> PublishAsync<TPayload>(
        OutgoingMessage<TPayload> message, CancellationToken cancellationToken = default)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.IdempotencyKey);

        var encoded = _serializer.EncodePayload(message.Payload);
        var correlation = message.Correlation with { MessageId = message.Correlation.MessageId ?? Guid.CreateVersion7() };
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        using var activity = MessageTracePropagation.StartPublish(encoded.MessageType, message.Destination.Name, correlation, headers);

        var envelope = new MessageEnvelope
        {
            MessageId = correlation.MessageId!.Value,
            MessageType = encoded.MessageType,
            SchemaVersion = encoded.SchemaVersion,
            WorkspaceId = correlation.WorkspaceId,
            JobId = correlation.JobId,
            CorrelationId = correlation.CorrelationId,
            CausationId = correlation.CausationId,
            IdempotencyKey = message.IdempotencyKey,
            CreatedAt = _time.GetUtcNow(),
            Attempt = message.Attempt,
            Headers = headers,
            Payload = encoded.Payload,
        };

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = MessageSerializer.ContentType,
            MessageId = envelope.MessageId.ToString(),
            Type = envelope.MessageType,
            Timestamp = new AmqpTimestamp(envelope.CreatedAt.ToUnixTimeSeconds()),
        };
        if (envelope.CorrelationId.Length <= 255)
        {
            properties.CorrelationId = envelope.CorrelationId;
        }

        var body = MessageSerializer.Serialize(envelope);
        if (_signer.Enabled)
        {
            var signature = new Dictionary<string, object?>(StringComparer.Ordinal);
            await _signer.SignAsync(message.Destination.Name, body, signature, cancellationToken).ConfigureAwait(false);
            properties.Headers = signature;
        }

        try
        {
            await _channel.PublishAsync(
                RabbitMqTopology.WorkExchange, message.Destination.Name, properties, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MessagePublishException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(TelemetryAttributes.ErrorType, nameof(MessagePublishException));
            throw;
        }

        return envelope;
    }

    public ValueTask DisposeAsync() => _channel.DisposeAsync();

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
