using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;

namespace Opportunity.Application.Messaging;

/// <summary>A delivered, decoded message. <see cref="Payload"/> is already upgraded to the current contract.</summary>
public sealed record ReceivedMessage(
    MessageEnvelope Envelope,
    object Payload,
    WorkQueue Queue,
    bool Redelivered,
    int DeliveryCount,
    int TransportRetry)
{
    /// <summary>Correlation of this message; use <see cref="MessageCorrelation.CausedBy"/> for messages it causes.</summary>
    public MessageCorrelation Correlation => new(
        Envelope.CorrelationId, Envelope.CausationId, Envelope.WorkspaceId, Envelope.JobId, Envelope.MessageId);
}

public delegate Task ReceivedMessageHandler(ReceivedMessage message, CancellationToken cancellationToken);

/// <summary>
/// Consumes a work queue (ADR-019 port). Delivery policy implemented by the adapter:
/// <list type="bullet">
/// <item>handler completes → ack;</item>
/// <item>handler throws <see cref="PermanentMessageException"/>, or the message is malformed / its payload invalid →
/// dead-letter immediately with the error;</item>
/// <item>handler throws anything else → delayed transport retry with exponential backoff, then dead-letter with the
/// error once retries are exhausted (used only when the work row cannot be loaded; normal retries are PG state,
/// ADR-010 §7.1);</item>
/// <item>unknown message type or unsupported schema major → parking queue, never acked silently;</item>
/// <item>shutdown while handling → requeue.</item>
/// </list>
/// The envelope's <c>workspaceId</c> is only a hint (ADR-015 D9): handlers take authority from PostgreSQL.
/// </summary>
public interface IMessageConsumer
{
    /// <summary>Starts consuming <paramref name="queue"/>; dispose the result to stop (in-flight handlers finish or are requeued).</summary>
    Task<IAsyncDisposable> SubscribeAsync(WorkQueue queue, ReceivedMessageHandler handler, CancellationToken cancellationToken = default);
}

/// <summary>Handles one payload type; resolved per message from a fresh DI scope.</summary>
public interface IMessageHandler<in TPayload>
    where TPayload : class
{
    Task HandleAsync(TPayload payload, ReceivedMessage message, CancellationToken cancellationToken);
}

/// <summary>A failure retrying cannot fix (validation, envelope/PG mismatch): the message is dead-lettered at once.</summary>
public sealed class PermanentMessageException : Exception
{
    public PermanentMessageException()
    {
    }

    public PermanentMessageException(string message)
        : base(message)
    {
    }

    public PermanentMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
