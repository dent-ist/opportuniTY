namespace Opportunity.Contracts.Messaging;

/// <summary>
/// Well-known keys of the message envelope's <c>headers</c> map (ADR-019 §3.1). Trace context is W3C Trace Context
/// (ADR-017 R4); W3C <c>baggage</c> is deliberately not propagated, so nothing a producer adds can leak into
/// downstream telemetry.
/// </summary>
public static class MessageHeaderNames
{
    /// <summary>W3C <c>traceparent</c>: <c>00-{trace-id}-{parent-id}-{flags}</c>.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>W3C <c>tracestate</c> (optional, vendor-specific).</summary>
    public const string TraceState = "tracestate";
}
