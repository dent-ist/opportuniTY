using System.Diagnostics;
using Opportunity.Contracts.Messaging;

namespace Opportunity.Application.Telemetry;

/// <summary>
/// The correlation fields of a message envelope (baseline §11, ADR-019 §3.1). <see cref="CorrelationId"/> is copied
/// unchanged from the originating request (<c>X-Correlation-Id</c>) through every message it causes;
/// <see cref="CausationId"/> is the <c>messageId</c> of the message being handled when this one was published.
/// </summary>
public sealed record MessageCorrelation(
    string CorrelationId,
    string? CausationId = null,
    Guid? WorkspaceId = null,
    Guid? JobId = null,
    Guid? MessageId = null)
{
    /// <summary>Correlation for a message published while handling the message <paramref name="causingMessageId"/>.</summary>
    public MessageCorrelation CausedBy(Guid causingMessageId, Guid? newMessageId = null) =>
        this with { CausationId = causingMessageId.ToString(), MessageId = newMessageId };

    /// <summary>Logging-scope state (<see cref="LogScopeKeys"/>), for <c>ILogger.BeginScope</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> ToLogScope()
    {
        var scope = new List<KeyValuePair<string, object?>>(5) { new(LogScopeKeys.CorrelationId, CorrelationId) };
        Add(scope, LogScopeKeys.CausationId, CausationId);
        Add(scope, LogScopeKeys.WorkspaceId, WorkspaceId?.ToString());
        Add(scope, LogScopeKeys.JobId, JobId?.ToString());
        Add(scope, LogScopeKeys.MessageId, MessageId?.ToString());
        return scope;
    }

    internal void Tag(Activity activity)
    {
        activity.SetTag(TelemetryAttributes.CorrelationId, CorrelationId);
        SetIfPresent(activity, TelemetryAttributes.CausationId, CausationId);
        SetIfPresent(activity, TelemetryAttributes.WorkspaceId, WorkspaceId?.ToString());
        SetIfPresent(activity, TelemetryAttributes.JobId, JobId?.ToString());
        SetIfPresent(activity, TelemetryAttributes.MessagingMessageId, MessageId?.ToString());
    }

    private static void Add(List<KeyValuePair<string, object?>> scope, string key, string? value)
    {
        if (value is not null)
        {
            scope.Add(new(key, value));
        }
    }

    private static void SetIfPresent(Activity activity, string key, string? value)
    {
        if (value is not null)
        {
            activity.SetTag(key, value);
        }
    }
}

/// <summary>
/// W3C Trace Context propagation through the envelope <c>headers</c> map, independent of the transport (ADR-017 R4).
/// The messaging infrastructure calls <see cref="StartPublish"/> before sending and <see cref="StartProcess"/> when it
/// hands a message to a consumer; the process span continues the producer's trace, so one trace spans
/// API → outbox dispatcher → broker → worker.
/// </summary>
public static class MessageTracePropagation
{
    /// <summary>Writes <c>traceparent</c>/<c>tracestate</c> for <paramref name="activity"/>; removes stale ones when it is null.</summary>
    public static void Inject(Activity? activity, IDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        headers.Remove(MessageHeaderNames.TraceParent);
        headers.Remove(MessageHeaderNames.TraceState);
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C)
        {
            return;
        }

        var flags = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0 ? "01" : "00";
        headers[MessageHeaderNames.TraceParent] = $"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-{flags}";
        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            headers[MessageHeaderNames.TraceState] = activity.TraceStateString;
        }
    }

    /// <summary>The remote parent carried in <paramref name="headers"/>, or <c>default</c> when absent or malformed.</summary>
    public static ActivityContext Extract(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || !headers.TryGetValue(MessageHeaderNames.TraceParent, out var traceParent))
        {
            return default;
        }

        headers.TryGetValue(MessageHeaderNames.TraceState, out var traceState);
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context) ? context : default;
    }

    /// <summary>Starts a producer span <c>send {destination}</c> and injects its context into <paramref name="headers"/>.</summary>
    public static Activity? StartPublish(
        string messageType,
        string destination,
        MessageCorrelation correlation,
        IDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        var activity = OpportunityTelemetry.Source.StartActivity($"send {destination}", ActivityKind.Producer);
        if (activity is not null)
        {
            TagMessage(activity, "send", messageType, destination, correlation);
        }

        Inject(activity ?? Activity.Current, headers);
        return activity;
    }

    /// <summary>
    /// Starts a consumer span <c>process {destination}</c> whose parent is the producer context in
    /// <paramref name="headers"/> (a new trace when there is none).
    /// </summary>
    public static Activity? StartProcess(
        string messageType,
        string destination,
        MessageCorrelation correlation,
        IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        var activity = OpportunityTelemetry.Source.StartActivity(
            $"process {destination}", ActivityKind.Consumer, Extract(headers));
        if (activity is not null)
        {
            TagMessage(activity, "process", messageType, destination, correlation);
        }

        return activity;
    }

    private static void TagMessage(
        Activity activity, string operation, string messageType, string destination, MessageCorrelation correlation)
    {
        if (!activity.IsAllDataRequested)
        {
            return;
        }

        activity.SetTag(TelemetryAttributes.MessagingOperationName, operation);
        activity.SetTag(TelemetryAttributes.MessagingOperationType, operation);
        activity.SetTag(TelemetryAttributes.MessagingDestinationName, destination);
        activity.SetTag(TelemetryAttributes.MessageType, messageType);
        correlation.Tag(activity);
    }
}
