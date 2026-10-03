using System.Diagnostics;

namespace Opportunity.Hosting.Telemetry;

/// <summary>
/// W3C <c>traceparent</c>/<c>tracestate</c> in and out, but never <c>baggage</c> (ADR-017 R4): caller-supplied baggage
/// would otherwise ride on every activity and be forwarded to downstream services.
/// </summary>
internal sealed class TraceContextOnlyPropagator(DistributedContextPropagator inner) : DistributedContextPropagator
{
    private static readonly string[] BaggageFields = ["baggage", "Correlation-Context"];

    public override IReadOnlyCollection<string> Fields { get; } =
        [.. inner.Fields.Where(f => !BaggageFields.Contains(f, StringComparer.OrdinalIgnoreCase))];

    public static void Install()
    {
        if (Current is not TraceContextOnlyPropagator)
        {
            Current = new TraceContextOnlyPropagator(Current);
        }
    }

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (setter is null)
        {
            return;
        }

        inner.Inject(activity, carrier, (c, name, value) =>
        {
            if (!BaggageFields.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                setter(c, name, value);
            }
        });
    }

    public override void ExtractTraceIdAndState(
        object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
