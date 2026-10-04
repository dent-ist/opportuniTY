using Opportunity.Contracts.Api;

namespace Opportunity.Contracts.Search;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/searches</c>: query-language text only, never DSL (ADR-008). The
/// workspace comes from the route and the caller's membership, never from the body.
/// </summary>
/// <param name="Query">Query-language text; empty means every document the caller may see.</param>
/// <param name="Sort">
/// Sort keys in order. Default: relevance when the query has a keyword, else Control Number ascending (an empty query or
/// filters only). <c>documentId</c> is always appended as the tie-breaker.
/// </param>
/// <param name="PageSize">1–500, default 50.</param>
/// <param name="CountExact">Q-32 "count exactly": the total is exact instead of capped at 10,000 (audited).</param>
/// <param name="Highlight">Return bounded snippets of the matching text (default true).</param>
/// <param name="Facets">Facet fields for the first page (see <see cref="SearchFacetFields"/>).</param>
/// <param name="SavedSearchId">
/// Run a saved search instead of <see cref="Query"/> (give one or the other): its stored query is re-parsed now and the
/// results are filtered for the caller; its stored sort applies unless <see cref="Sort"/> is given. The run is recorded as
/// the search's last run.
/// </param>
public sealed record SearchRequest(
    string? Query,
    IReadOnlyList<SearchSortKey>? Sort = null,
    int? PageSize = null,
    bool? CountExact = null,
    bool? Highlight = null,
    IReadOnlyList<string>? Facets = null,
    Guid? SavedSearchId = null);

/// <param name="Field">A sortable field (<see cref="SearchSortFields"/>), case-insensitive.</param>
public sealed record SearchSortKey(string Field, SearchSortDirection Direction = SearchSortDirection.Asc);

public enum SearchSortDirection
{
    Asc,
    Desc,
}

/// <summary>Sortable fields of the first slice; the planner (E07-T07) adds workspace fields.</summary>
public static class SearchSortFields
{
    public const string Relevance = "relevance";

    public static IReadOnlyList<string> All { get; } =
        [Relevance, "controlNumber", "documentDate", "fileName", "fileType", "fileExtension", "fileSize", "pageCount", "dateSent", "dateReceived"];
}

/// <summary>Facetable (aggregatable keyword) fields of the first slice.</summary>
public static class SearchFacetFields
{
    public static IReadOnlyList<string> All { get; } = ["fileType", "fileExtension", "mimeType", "familyStatus"];
}

/// <summary>
/// One page of search results (ADR-015 D8, Q-12, Q-32/Q-33/Q-49). Every hit was re-checked against PostgreSQL before
/// the page was returned; hits the caller may not view are dropped without a trace, so <see cref="Items"/> can hold
/// fewer than <see cref="SearchPageInfo.Size"/> rows on a page that is not the last one.
/// </summary>
public sealed record SearchResultPage
{
    /// <summary>Opaque handle of this search, bound to the caller, their session and the workspace. Null when nothing is indexed yet.</summary>
    public string? SearchId { get; init; }

    /// <summary>The saved search this search ran (<c>savedSearchId</c> in the request); null for a typed query.</summary>
    public Guid? SavedSearchId { get; init; }

    /// <summary>The normalized interpretation of the query (e.g. <c>a AND b</c> for <c>a b</c>).</summary>
    public required string Normalized { get; init; }

    public required IReadOnlyList<SearchHit> Items { get; init; }

    public required SearchPageInfo Page { get; init; }

    /// <summary>Eq up to 10,000 hits (or with <c>countExact</c>), else Gte; shown with "≈" unless the freshness is current (Q-10).</summary>
    public required TotalCount Total { get; init; }

    public required SearchFreshness Freshness { get; init; }

    /// <summary>Approximate counts (Q-10, Q-12): not post-filtered. First page only.</summary>
    public IReadOnlyList<SearchFacet> Facets { get; init; } = [];

    /// <summary>Opaque cursor of the next page (<c>GET .../searches/{searchId}/pages?cursor=</c>); null on the last page.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Opaque cursor of the previous page; null on the first page.</summary>
    public string? PreviousCursor { get; init; }

    /// <summary>
    /// Q-33: the live point-in-time view expired and was reopened, so the results now reflect newer changes and pages
    /// already seen may have shifted. The UI shows a "results refreshed" notice.
    /// </summary>
    public bool ResultsRefreshed { get; init; }
}

/// <param name="Number">1-based page number when known: exact from the top, or from the end once the total is exact (Q-49).</param>
/// <param name="Size">The requested page size.</param>
/// <param name="PageCount">Total pages when the total is exact; null when the total is a lower bound.</param>
/// <param name="IsFirst">No previous page.</param>
/// <param name="IsLast">No next page.</param>
public sealed record SearchPageInfo(int? Number, int Size, long? PageCount, bool IsFirst, bool IsLast);

/// <summary>
/// Q-10 freshness. <see cref="ServedGeneration"/> is the applied search generation the page's reader was opened at;
/// <see cref="Current"/> is true when every change committed in the workspace was applied to the index by then and
/// nothing was committed since (interim, not yet refresh-aware: E07-T08). Null means unknown, in which case counts are
/// labelled approximate. Raw generations are shown to admins and support only; reviewers see "current as of
/// <see cref="AsOf"/>".
/// </summary>
public sealed record SearchFreshness(long? ServedGeneration, bool? Current, DateTimeOffset AsOf);

/// <summary>
/// A result row: grid fields and bounded snippets only, never full text (ADR-015 D8.5). Further columns come from the
/// projection mapping (E07-T02) and the field catalogue.
/// </summary>
/// <param name="ParentDocumentId">The immediate parent; set exactly for attachments (family members other than the top).</param>
/// <param name="IsFamilyParent">
/// The top-level document of a family with at least one other member the caller's search can see (the grid's parent
/// marker). False for standalone documents and attachments.
/// </param>
public sealed record SearchHit(
    Guid DocumentId,
    string ControlNumber,
    string? FileName,
    string? FileType,
    string? FileExtension,
    string? MimeType,
    DateTimeOffset? DocumentDate,
    string? FamilyId,
    string? ParentDocumentId,
    int? FamilySequence,
    long? FileSize,
    int? PageCount,
    IReadOnlyList<SearchSnippet> Snippets,
    bool IsFamilyParent = false);

/// <summary>A snippet as plain text with the matched ranges (UTF-16 offsets), so clients never render markup from the index.</summary>
public sealed record SearchSnippet(string Text, IReadOnlyList<TextSpan> Highlights);

public sealed record SearchFacet(string Field, IReadOnlyList<SearchFacetBucket> Buckets);

public sealed record SearchFacetBucket(string Value, long Count);
