namespace Opportunity.Search;

/// <summary>Search service settings (section <c>OpenSearch:Search</c>), validated with <see cref="OpenSearchOptions"/>.</summary>
public sealed class SearchServiceOptions
{
    public int DefaultPageSize { get; set; } = 50;

    public int MaxPageSize { get; set; } = 500;

    /// <summary>Q-32: totals are exact up to this many hits, a lower bound (≈) above it unless "count exactly" is asked.</summary>
    public int TrackTotalHitsUpTo { get; set; } = 10_000;

    /// <summary>How long OpenSearch keeps a point-in-time reader between pages; every page extends it (Q-33).</summary>
    public TimeSpan PointInTimeKeepAlive { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an idle search handle and its cursors stay usable.</summary>
    public TimeSpan SearchIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Server-side timeout of one search request (partial results are reported as such, never silently used).</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Snippet bounds (ADR-015 D8.5): characters per fragment and fragments per hit.</summary>
    public int SnippetFragmentSize { get; set; } = 150;

    public int SnippetsPerHit { get; set; } = 3;

    public int FacetBuckets { get; set; } = 25;

    /// <summary>How long the planner reuses a workspace's field catalogue; field changes reach searches within this time.</summary>
    public TimeSpan FieldCatalogCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    internal void Validate(string section)
    {
        Require(DefaultPageSize >= 1 && DefaultPageSize <= MaxPageSize, section, nameof(DefaultPageSize));
        Require(MaxPageSize is >= 1 and <= 500, section, nameof(MaxPageSize));
        Require(TrackTotalHitsUpTo >= 1, section, nameof(TrackTotalHitsUpTo));
        Require(PointInTimeKeepAlive >= TimeSpan.FromSeconds(10) && PointInTimeKeepAlive <= TimeSpan.FromHours(1), section, nameof(PointInTimeKeepAlive));
        Require(SearchIdleTimeout >= PointInTimeKeepAlive && SearchIdleTimeout <= TimeSpan.FromHours(24), section, nameof(SearchIdleTimeout));
        Require(QueryTimeout > TimeSpan.Zero, section, nameof(QueryTimeout));
        Require(SnippetFragmentSize is >= 20 and <= 1000, section, nameof(SnippetFragmentSize));
        Require(SnippetsPerHit is >= 0 and <= 10, section, nameof(SnippetsPerHit));
        Require(FacetBuckets is >= 1 and <= 200, section, nameof(FacetBuckets));
        Require(FieldCatalogCacheTtl >= TimeSpan.Zero && FieldCatalogCacheTtl <= TimeSpan.FromMinutes(5), section, nameof(FieldCatalogCacheTtl));
    }

    private static void Require(bool condition, string section, string setting)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{section}:{setting} is out of range.");
        }
    }
}
