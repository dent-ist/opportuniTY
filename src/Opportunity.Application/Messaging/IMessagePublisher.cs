using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;

namespace Opportunity.Application.Messaging;

/// <summary>
/// A message to publish. <see cref="MessageCorrelation.MessageId"/> may be preset (otherwise a fresh UUIDv7 is used);
/// <see cref="IdempotencyKey"/> must be stable across re-publish of the same work (ADR-010 §5.1).
/// </summary>
public sealed record OutgoingMessage<TPayload>(
    WorkQueue Destination,
    TPayload Payload,
    MessageCorrelation Correlation,
    string IdempotencyKey)
    where TPayload : class
{
    /// <summary>The work row's <c>AttemptCount</c> (ADR-010 §5.2).</summary>
    public int Attempt { get; init; }
}

/// <summary>
/// Publishes messages to work queues (ADR-019 port; the RabbitMQ adapter lives in <c>Opportunity.Messaging</c>).
/// A returned task completes only after the broker confirmed the message as routed and persisted; any failure
/// (nack, unroutable, connection loss, timeout) throws <see cref="MessagePublishException"/> and the caller keeps the
/// work row unpublished (at-least-once, ADR-001 §6.1).
/// </summary>
public interface IMessagePublisher
{
    Task<MessageEnvelope> PublishAsync<TPayload>(OutgoingMessage<TPayload> message, CancellationToken cancellationToken = default)
        where TPayload : class;
}

/// <summary>The broker did not confirm a publish; the message may or may not have been delivered.</summary>
public sealed class MessagePublishException : Exception
{
    public MessagePublishException()
    {
    }

    public MessagePublishException(string message)
        : base(message)
    {
    }

    public MessagePublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
