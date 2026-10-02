using System.Diagnostics;

namespace Opportunity.IntegrationTests.Containers;

internal static class Timing
{
    /// <summary>Injected latency; large enough to stand out from container round trips on a busy CI runner.</summary>
    public static readonly TimeSpan InjectedLatency = TimeSpan.FromMilliseconds(800);

    /// <summary>Lower bound accepted for an operation that crosses the latency toxic at least once.</summary>
    public static readonly TimeSpan MinimumObservedLatency = InjectedLatency * 0.9;

    public static async Task<TimeSpan> MeasureAsync(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        return stopwatch.Elapsed;
    }
}
