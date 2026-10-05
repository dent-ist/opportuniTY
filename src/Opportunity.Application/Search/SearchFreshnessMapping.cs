using Opportunity.Contracts.Search;

namespace Opportunity.Application.Search;

/// <summary>Maps a <see cref="SearchFreshnessReading"/> onto the search freshness resources (Q-10, ADR-001 §7).</summary>
public static class SearchFreshnessMapping
{
    /// <summary>
    /// Freshness of a page whose reader was opened at <paramref name="servedGeneration"/> (the refresh-aware watermark
    /// read before it opened): current while nothing committed since is missing from it. The other fields describe the
    /// workspace at serve time.
    /// </summary>
    public static SearchFreshness ForPage(long? servedGeneration, SearchFreshnessReading reading, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return new SearchFreshness(
            servedGeneration,
            servedGeneration is { } g && g >= reading.LatestGeneration,
            asOf,
            State(reading.Level),
            reading.IndexedThroughGeneration,
            reading.PendingChanges,
            LagSeconds(reading));
    }

    public static SearchFreshnessStatus ToStatus(SearchFreshnessReading reading, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return new SearchFreshnessStatus(
            State(reading.Level), reading.IndexedThroughGeneration, reading.LatestGeneration, reading.PendingChanges, LagSeconds(reading), asOf);
    }

    public static SearchFreshnessState State(SearchFreshnessLevel level) => level switch
    {
        SearchFreshnessLevel.Current => SearchFreshnessState.Current,
        SearchFreshnessLevel.Updating => SearchFreshnessState.Updating,
        _ => SearchFreshnessState.Delayed,
    };

    private static double LagSeconds(SearchFreshnessReading reading) => Math.Round(reading.Lag.TotalSeconds, 1);
}
