using System.Diagnostics;

namespace Opportunity.Application.Telemetry;

/// <summary>
/// Names of the application's own telemetry sources (ADR-017). Without a listener (telemetry off, the default)
/// <see cref="Source"/> creates no activities, so instrumented code costs a null check.
/// </summary>
public static class OpportunityTelemetry
{
    /// <summary><see cref="ActivitySource"/> for application spans (message publish/process, jobs, chunks).</summary>
    public const string ActivitySourceName = "Opportunity";

    /// <summary>Meter for the application metric catalog (<see cref="OpportunityMetrics"/>).</summary>
    public const string MeterName = "Opportunity";

    public static ActivitySource Source { get; } = new(ActivitySourceName);
}

/// <summary>
/// Span and metric attribute keys. Values are IDs, enums and counts only, never content (ADR-013 §7, ADR-015 D10.5).
/// Every key here is on the exporter allow-list in <c>Opportunity.Hosting.Telemetry.TelemetryAttributePolicy</c>.
/// </summary>
public static class TelemetryAttributes
{
    public const string WorkspaceId = "opportunity.workspace_id";
    public const string JobId = "opportunity.job_id";
    public const string JobType = "opportunity.job.type";
    public const string CorrelationId = "opportunity.correlation_id";
    public const string CausationId = "opportunity.causation_id";
    public const string MessageType = "opportunity.message.type";
    public const string WorkerType = "worker.type";
    public const string Lane = "opportunity.lane";
    public const string Outcome = "opportunity.outcome";
    public const string Status = "opportunity.status";
    public const string QueueState = "opportunity.queue.state";

    // OpenTelemetry messaging semantic conventions.
    public const string MessagingSystem = "messaging.system";
    public const string MessagingOperationName = "messaging.operation.name";
    public const string MessagingOperationType = "messaging.operation.type";
    public const string MessagingDestinationName = "messaging.destination.name";
    public const string MessagingMessageId = "messaging.message.id";
    public const string ErrorType = "error.type";
}

/// <summary>Logging-scope keys; every log record carries them where known (structured JSON and OTLP logs).</summary>
public static class LogScopeKeys
{
    public const string WorkspaceId = "WorkspaceId";
    public const string JobId = "JobId";
    public const string CorrelationId = "CorrelationId";
    public const string CausationId = "CausationId";
    public const string MessageId = "MessageId";
    public const string MessageType = "MessageType";
}
