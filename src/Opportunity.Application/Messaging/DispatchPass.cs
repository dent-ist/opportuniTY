using System.Diagnostics.Metrics;

using Opportunity.Application.Telemetry;

namespace Opportunity.Application.Messaging;

/// <summary>One dispatcher pass over one kind of work in one workspace.</summary>
/// <param name="Published">Claimed and confirmed by the broker, then marked Dispatched.</param>
/// <param name="Unconfirmed">Claimed but not confirmed; released for a later attempt.</param>
/// <param name="More">The pass claimed a full batch, so more work is probably due.</param>
public readonly record struct DispatchPassResult(int Published, int Unconfirmed, bool More)
{
    public static DispatchPassResult None => default;

    public int Claimed => Published + Unconfirmed;
}

/// <summary>
/// Records what the dispatcher publishes (ADR-017 catalog, <c>E06-T04</c>): every publish outcome per destination and,
/// for search work, the commit → broker-confirm latency per lane. With telemetry off nothing listens.
/// </summary>
public sealed class DispatchMetrics(OpportunityMetrics metrics, TimeProvider time)
{
    public const string ConfirmedOutcome = "confirmed";
    public const string UnconfirmedOutcome = "unconfirmed";

    private readonly Counter<long> _published = metrics.Counter(OpportunityMetricCatalog.DispatcherPublished);
    private readonly Histogram<double> _latency = metrics.Histogram(OpportunityMetricCatalog.OutboxPublishLatency);

    public TimeProvider Time { get; } = time;

    public void Published(WorkQueue destination, bool confirmed, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (count > 0)
        {
            _published.Add(count,
                new KeyValuePair<string, object?>(TelemetryAttributes.MessagingDestinationName, destination.Name),
                new KeyValuePair<string, object?>(TelemetryAttributes.Outcome, confirmed ? ConfirmedOutcome : UnconfirmedOutcome));
        }
    }

    /// <summary>Commit (<c>committed_at</c>) to broker confirm of one search work record.</summary>
    public void Latency(MessageLane lane, DateTimeOffset committedAt) =>
        _latency.Record(
            Math.Max(0, (Time.GetUtcNow() - committedAt).TotalSeconds),
            new KeyValuePair<string, object?>(TelemetryAttributes.Lane, LaneName(lane)));

    /// <summary>The <c>opportunity.lane</c> attribute value.</summary>
    public static string LaneName(MessageLane lane) => lane switch
    {
        MessageLane.Security => "security",
        MessageLane.Interactive => "interactive",
        MessageLane.SecurityBulk => "security-bulk",
        MessageLane.Bulk => "bulk",
        _ => "none",
    };
}
