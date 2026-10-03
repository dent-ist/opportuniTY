using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Opportunity.IntegrationTests.Telemetry;

/// <summary>
/// In-memory exporters attached to a host's OpenTelemetry pipeline (after the scrubbing processor, like a real
/// exporter). Activity listeners are process-wide, so assertions filter by trace ID.
/// </summary>
public sealed class TelemetryCapture
{
    public LockedCollection<Activity> Spans { get; } = new();

    public LockedCollection<MetricSnapshot> Metrics { get; } = new();

    public ConcurrentQueue<CapturedLog> Logs { get; } = new();

    public void AddTo(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(Spans));
        services.ConfigureOpenTelemetryMeterProvider(b => b.AddInMemoryExporter(Metrics));
        services.ConfigureOpenTelemetryLoggerProvider(b => b.AddProcessor(new LogSnapshotProcessor(Logs)));
    }

    /// <summary>Waits until a span matching <paramref name="predicate"/> was exported (server spans end after the response).</summary>
    public async Task<Activity> WaitForSpanAsync(Func<Activity, bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (Spans.Snapshot().FirstOrDefault(predicate) is { } span)
            {
                return span;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Expected span was not exported.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    public IReadOnlyList<Activity> SpansOf(ActivityTraceId traceId) =>
        [.. Spans.Snapshot().Where(s => s.TraceId == traceId)];

    /// <summary>Every attribute value and name of the spans of <paramref name="traceId"/>, for leak assertions.</summary>
    public IEnumerable<string> TextOf(ActivityTraceId traceId) =>
        SpansOf(traceId).SelectMany(s => new[] { s.DisplayName, s.StatusDescription ?? string.Empty }
            .Concat(s.TagObjects.SelectMany(t => new[] { t.Key, Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty }))
            .Concat(s.Events.SelectMany(e => e.Tags.Select(t => Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)))
            .Concat(s.Baggage.Select(b => b.Value ?? string.Empty)));

    private sealed class LogSnapshotProcessor(ConcurrentQueue<CapturedLog> logs) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            var scopes = new List<KeyValuePair<string, object?>>();
            data.ForEachScope(
                static (scope, list) =>
                {
                    foreach (var item in scope)
                    {
                        list.Add(item);
                    }
                },
                scopes);
            logs.Enqueue(new CapturedLog(
                data.CategoryName ?? string.Empty,
                data.TraceId,
                data.FormattedMessage ?? data.Body ?? string.Empty,
                [.. data.Attributes ?? []],
                scopes));
        }
    }
}

public sealed record CapturedLog(
    string Category,
    ActivityTraceId TraceId,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Attributes,
    IReadOnlyList<KeyValuePair<string, object?>> Scopes)
{
    public object? Scope(string key) => Scopes.LastOrDefault(s => s.Key == key).Value;

    public IEnumerable<string> Text() =>
        new[] { Message }
            .Concat(Attributes.Concat(Scopes).Select(a => Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
}

internal static partial class TestLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Probe returned {ResultCount} results")]
    public static partial void ProbeReturned(ILogger logger, int resultCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chunk applied with {DocumentCount} documents")]
    public static partial void ChunkApplied(ILogger logger, int documentCount);
}

/// <summary>Thread-safe collection for exporters that append while the test reads.</summary>
public sealed class LockedCollection<T> : ICollection<T>
{
    private readonly List<T> _items = [];

    public int Count
    {
        get
        {
            lock (_items)
            {
                return _items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public IReadOnlyList<T> Snapshot()
    {
        lock (_items)
        {
            return [.. _items];
        }
    }

    public void Add(T item)
    {
        lock (_items)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_items)
        {
            _items.Clear();
        }
    }

    public bool Contains(T item)
    {
        lock (_items)
        {
            return _items.Contains(item);
        }
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        lock (_items)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public bool Remove(T item)
    {
        lock (_items)
        {
            return _items.Remove(item);
        }
    }

    public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
