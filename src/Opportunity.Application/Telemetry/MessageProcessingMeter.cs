namespace Opportunity.Application.Telemetry;

/// <summary>
/// The metrics half of worker message handling: <c>messaging.process.duration</c>,
/// <c>messaging.client.consumed.messages</c> and the worker's last-consumed timestamp, without a span or log scope. For
/// consumers that run under the transport's process span (E06-T01), so the span is not created twice. Implemented by
/// <c>Opportunity.Hosting.Workers.WorkerTelemetry</c> in worker hosts.
/// </summary>
public interface IMessageProcessingMeter
{
    /// <summary>Starts measuring one message; dispose the result when handling ends.</summary>
    IMessageProcessingMeasurement Measure(string workerType, string messageType, string destination);
}

public interface IMessageProcessingMeasurement : IDisposable
{
    /// <summary>
    /// Marks the handling failed; <paramref name="errorType"/> is a low-cardinality class (exception type name or
    /// ADR-010 <c>ErrorClass</c>), never an exception message.
    /// </summary>
    void Fail(string errorType);
}

/// <summary>Records nothing; the default outside worker hosts.</summary>
public sealed class NullMessageProcessingMeter : IMessageProcessingMeter, IMessageProcessingMeasurement
{
    public static NullMessageProcessingMeter Instance { get; } = new();

    public IMessageProcessingMeasurement Measure(string workerType, string messageType, string destination) => this;

    public void Fail(string errorType)
    {
    }

    public void Dispose()
    {
    }
}
