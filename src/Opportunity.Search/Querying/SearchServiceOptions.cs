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

    /// <summary>
    /// ADR-002 §8: the oldest a point-in-time reader of an interactive search may get. A page asked for after that runs
    /// on a new reader (re-established from the cursor position) and says "results refreshed", so a long review
    /// session does not pin old segments indefinitely.
    /// </summary>
    public TimeSpan PointInTimeMaxAge { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// ADR-002 §8: open interactive readers per user and workspace; a new search closes the user's oldest beyond this
    /// (their next page re-establishes a reader and says "results refreshed").
    /// </summary>
    public int MaxOpenPointInTimesPerUser { get; set; } = 3;

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

    /// <summary>Keep-alive of the reader a snapshot selection pages under, renewed by every page (ADR-002 §2 Materialization).</summary>
    public TimeSpan SelectionKeepAlive { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Largest selection page (IDs per OpenSearch request; bounded by the index's max result window).</summary>
    public int MaxSelectionPageSize { get; set; } = 10_000;

    /// <summary>
    /// E09-T03: most distinct families, duplicate groups and email threads (each kind counted separately) an interactive
    /// search may expand; above it the search asks to narrow the query or to freeze the set (a snapshot expands in
    /// PostgreSQL without this bound). Every page sends the keys to OpenSearch, so this bounds the request size.
    /// </summary>
    public int MaxExpansionKeys { get; set; } = 250_000;

    /// <summary>Relationship keys read per OpenSearch aggregation page while an expanded search collects them.</summary>
    public int ExpansionKeyPageSize { get; set; } = 10_000;

    internal void Validate(string section)
    {
        Require(DefaultPageSize >= 1 && DefaultPageSize <= MaxPageSize, section, nameof(DefaultPageSize));
        Require(MaxPageSize is >= 1 and <= 500, section, nameof(MaxPageSize));
        Require(TrackTotalHitsUpTo >= 1, section, nameof(TrackTotalHitsUpTo));
        Require(PointInTimeKeepAlive >= TimeSpan.FromSeconds(1) && PointInTimeKeepAlive <= TimeSpan.FromHours(1), section, nameof(PointInTimeKeepAlive));
        Require(PointInTimeMaxAge >= TimeSpan.FromSeconds(1) && PointInTimeMaxAge <= TimeSpan.FromHours(24), section, nameof(PointInTimeMaxAge));
        Require(MaxOpenPointInTimesPerUser is >= 1 and <= 50, section, nameof(MaxOpenPointInTimesPerUser));
        Require(SearchIdleTimeout >= PointInTimeKeepAlive && SearchIdleTimeout <= TimeSpan.FromHours(24), section, nameof(SearchIdleTimeout));
        Require(QueryTimeout > TimeSpan.Zero, section, nameof(QueryTimeout));
        Require(SnippetFragmentSize is >= 20 and <= 1000, section, nameof(SnippetFragmentSize));
        Require(SnippetsPerHit is >= 0 and <= 10, section, nameof(SnippetsPerHit));
        Require(FacetBuckets is >= 1 and <= 200, section, nameof(FacetBuckets));
        Require(FieldCatalogCacheTtl >= TimeSpan.Zero && FieldCatalogCacheTtl <= TimeSpan.FromMinutes(5), section, nameof(FieldCatalogCacheTtl));
        Require(SelectionKeepAlive >= TimeSpan.FromSeconds(10) && SelectionKeepAlive <= TimeSpan.FromHours(1), section, nameof(SelectionKeepAlive));
        Require(MaxSelectionPageSize is >= 1 and <= 10_000, section, nameof(MaxSelectionPageSize));
        Require(MaxExpansionKeys is >= 1 and <= 1_000_000, section, nameof(MaxExpansionKeys));
        Require(ExpansionKeyPageSize is >= 1 and <= 10_000, section, nameof(ExpansionKeyPageSize));
    }

    private static void Require(bool condition, string section, string setting)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{section}:{setting} is out of range.");
        }
    }
}
