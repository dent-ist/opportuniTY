using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Opportunity.Application.Telemetry;

namespace Opportunity.Hosting.Workers;

/// <summary>
/// What a worker consumer wraps around handling one message: the consumer span continuing the producer's trace
/// (W3C context from the envelope headers), a logging scope with WorkspaceId/JobId/CorrelationId/CausationId, the
/// <c>messaging.process.duration</c> and <c>messaging.client.consumed.messages</c> metrics and the worker's
/// last-consumed timestamp.
/// <code>
/// using var processing = telemetry.BeginProcessing(WorkerTypes.Indexing, envelope.MessageType, queue, correlation, envelope.Headers);
/// try { await handler.HandleAsync(...); } catch (Exception ex) { processing.Fail(ex.GetType().Name); throw; }
/// </code>
/// </summary>
public sealed class WorkerTelemetry(
    WorkerStatus status,
    OpportunityMetrics metrics,
    ILogger<WorkerTelemetry> logger,
    TimeProvider time)
{
    public MessageProcessingScope BeginProcessing(
        string workerType,
        string messageType,
        string destination,
        MessageCorrelation correlation,
        IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerType);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(correlation);

        var activity = MessageTracePropagation.StartProcess(messageType, destination, correlation, headers);
        activity?.SetTag(TelemetryAttributes.WorkerType, workerType);
        var scope = logger.BeginScope(correlation.ToLogScope());
        return new MessageProcessingScope(this, activity, scope, workerType, messageType, destination, time.GetTimestamp());
    }

    internal void Complete(MessageProcessingScope processing, string? errorType)
    {
        var tags = new TagList
        {
            { TelemetryAttributes.MessagingDestinationName, processing.Destination },
            { TelemetryAttributes.MessageType, processing.MessageType },
            { TelemetryAttributes.WorkerType, processing.WorkerType },
        };
        if (errorType is not null)
        {
            tags.Add(TelemetryAttributes.ErrorType, errorType);
        }

        metrics.Histogram(OpportunityMetricCatalog.MessagingProcessDuration)
            .Record(time.GetElapsedTime(processing.StartedAt).TotalSeconds, tags);
        metrics.Counter(OpportunityMetricCatalog.MessagingConsumedMessages).Add(1, tags);
        status.RecordConsumed(processing.WorkerType);
    }
}

public sealed class MessageProcessingScope : IDisposable
{
    private readonly WorkerTelemetry _owner;
    private readonly IDisposable? _logScope;
    private string? _errorType;
    private bool _disposed;

    internal MessageProcessingScope(
        WorkerTelemetry owner,
        Activity? activity,
        IDisposable? logScope,
        string workerType,
        string messageType,
        string destination,
        long startedAt)
    {
        _owner = owner;
        Activity = activity;
        _logScope = logScope;
        WorkerType = workerType;
        MessageType = messageType;
        Destination = destination;
        StartedAt = startedAt;
    }

    /// <summary>The consumer span (null when tracing is off or the trace is not sampled).</summary>
    public Activity? Activity { get; }

    internal string WorkerType { get; }

    internal string MessageType { get; }

    internal string Destination { get; }

    internal long StartedAt { get; }

    /// <summary>
    /// Marks the handling failed. <paramref name="errorType"/> is a low-cardinality class (exception type name or
    /// ADR-010 <c>ErrorClass</c>), never an exception message.
    /// </summary>
    public void Fail(string errorType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        _errorType = errorType;
        Activity?.SetStatus(ActivityStatusCode.Error);
        Activity?.SetTag(TelemetryAttributes.ErrorType, errorType);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.Complete(this, _errorType);
        Activity?.Dispose();
        _logScope?.Dispose();
    }
}
