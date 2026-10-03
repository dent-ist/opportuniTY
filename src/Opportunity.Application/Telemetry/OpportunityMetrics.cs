using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Opportunity.Application.Telemetry;

/// <summary>
/// Creates the instruments of <see cref="OpportunityMetricCatalog"/> on the <c>Opportunity</c> meter, once per name.
/// Recording components ask for their instrument by definition, so a name, unit or bucket layout cannot drift from the
/// catalog. Singleton; with telemetry off nothing listens and recording is a no-op.
/// </summary>
public sealed class OpportunityMetrics
{
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new(StringComparer.Ordinal);

    public OpportunityMetrics(IMeterFactory meterFactory, int maxWorkspaceAttributeValues = BoundedAttributeValues.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(OpportunityTelemetry.MeterName);
        Workspaces = new BoundedAttributeValues(maxWorkspaceAttributeValues);
    }

    /// <summary>Bounds the <c>opportunity.workspace_id</c> metric attribute (top workspaces + <c>other</c>).</summary>
    public BoundedAttributeValues Workspaces { get; }

    public KeyValuePair<string, object?> WorkspaceAttribute(Guid workspaceId) =>
        new(TelemetryAttributes.WorkspaceId, Workspaces.Map(workspaceId.ToString()));

    public Counter<long> Counter(MetricDefinition definition) =>
        Get(definition, MetricKind.Counter, d => _meter.CreateCounter<long>(d.Name, d.Unit, d.Description));

    public UpDownCounter<long> UpDownCounter(MetricDefinition definition) =>
        Get(definition, MetricKind.UpDownCounter, d => _meter.CreateUpDownCounter<long>(d.Name, d.Unit, d.Description));

    public Histogram<double> Histogram(MetricDefinition definition) =>
        Get(definition, MetricKind.Histogram, d => _meter.CreateHistogram(
            d.Name,
            d.Unit,
            d.Description,
            tags: null,
            advice: d.Buckets is null ? null : new InstrumentAdvice<double> { HistogramBucketBoundaries = d.Buckets }));

    /// <summary>Registers the observable gauge of <paramref name="definition"/>; the owner supplies the callback.</summary>
    public ObservableGauge<T> Gauge<T>(MetricDefinition definition, Func<IEnumerable<Measurement<T>>> observe)
        where T : struct =>
        Get(definition, MetricKind.Gauge, d => _meter.CreateObservableGauge(d.Name, observe, d.Unit, d.Description));

    private TInstrument Get<TInstrument>(MetricDefinition definition, MetricKind kind, Func<MetricDefinition, TInstrument> create)
        where TInstrument : Instrument
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Kind != kind || definition.Meter != OpportunityTelemetry.MeterName)
        {
            throw new ArgumentException(
                $"Metric '{definition.Name}' is a {definition.Kind} on meter '{definition.Meter}', not a {kind} on '{OpportunityTelemetry.MeterName}'.",
                nameof(definition));
        }

        return (TInstrument)_instruments.GetOrAdd(definition.Name, _ => create(definition));
    }
}

/// <summary>
/// Bounds a high-cardinality attribute value (workspace IDs) for metrics: the first <c>capacity</c> distinct values seen
/// by this process are kept, every later one is reported as <see cref="Overflow"/>. Spans and logs carry the real
/// value; only metric series are bounded (ADR-017 §5.3).
/// </summary>
public sealed class BoundedAttributeValues(int capacity = BoundedAttributeValues.DefaultCapacity)
{
    public const int DefaultCapacity = 20;
    public const string Overflow = "other";

    private readonly ConcurrentDictionary<string, byte> _admitted = new(StringComparer.Ordinal);
    private int _count;

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public string Map(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_admitted.ContainsKey(value))
        {
            return value;
        }

        if (Interlocked.Increment(ref _count) <= Capacity)
        {
            _admitted.TryAdd(value, 0);
            return value;
        }

        Interlocked.Decrement(ref _count);
        return Overflow;
    }
}
