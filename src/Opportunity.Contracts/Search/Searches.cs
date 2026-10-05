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
/// <param name="Expand">
/// E09-T03 "Include: Family / Duplicates / Email thread": the results also hold the families, duplicates and email
/// threads of the hits, each such row flagged with <see cref="SearchHit.ExpandedBy"/>. Members the caller may not see
/// are never added (Q-52). Null: no expansion, or a saved search's stored choice. Without a sort, an expanded search is
/// sorted by <c>familyDate</c> so families stay together.
/// </param>
public sealed record SearchRequest(
    string? Query,
    IReadOnlyList<SearchSortKey>? Sort = null,
    int? PageSize = null,
    bool? CountExact = null,
    bool? Highlight = null,
    IReadOnlyList<string>? Facets = null,
    Guid? SavedSearchId = null,
    SearchExpand? Expand = null);

/// <summary>
/// Which related documents to add to a document set (E09-T03): used by searches, snapshots ("frozen sets", the targets of
/// Mass Edit and exports) and saved searches. Absent or false members mean no expansion of that kind. Duplicates and
/// email threads are taken from the base documents; family is taken from the base documents and from what the duplicate
/// and thread expansion added (so expanded emails bring their attachments).
/// </summary>
public sealed record SearchExpand(bool? Family = null, bool? Duplicates = null, bool? Thread = null);

/// <summary>
/// The documents an expanded search added, by why each was added (first reason wins: family, then duplicate, then thread).
/// Exact counts at the page's point in time, excluding documents the caller may not see; shown approximate (≈) under
/// the same Q-10 rule as <see cref="SearchResultPage.Total"/>.
/// </summary>
/// <param name="Family">Family members of the hits (and of added duplicates or thread members) that are not hits.</param>
/// <param name="Duplicates">Members of the hits' duplicate groups that are neither hits nor family.</param>
/// <param name="Thread">Members of the hits' email threads added by no earlier kind.</param>
/// <param name="Total">Base hits plus every added document: the size of the expanded result list.</param>
public sealed record SearchExpandedCounts(long Family, long Duplicates, long Thread, long Total);

/// <summary>Why a result row is in an expanded search although it does not match the query (E09-T03).</summary>
public enum SearchExpandedBy
{
    Family,
    Duplicate,
    Thread,
}

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

    /// <summary>
    /// "Date (Family)" (ADR-009 R25, E09-T03): the family date, then the family and its family sequence, so families stay
    /// contiguous and the parent comes first.
    /// </summary>
    public const string FamilyDate = "familyDate";

    public static IReadOnlyList<string> All { get; } =
        [Relevance, "controlNumber", "documentDate", "fileName", "fileType", "fileExtension", "fileSize", "pageCount", "dateSent", "dateReceived", FamilyDate];
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

    /// <summary>
    /// The base hits (documents matching the query): Eq up to 10,000 hits (or with <c>countExact</c>, or when expanded),
    /// else Gte; shown with "≈" unless the freshness is current (Q-10). Expanded members are counted in <see cref="Expanded"/>.
    /// </summary>
    public required TotalCount Total { get; init; }

    /// <summary>The expansion applied (the request's, or a saved search's stored choice); null when none.</summary>
    public SearchExpand? Expand { get; init; }

    /// <summary>
    /// With <see cref="Expand"/>: the added documents by kind and the expanded list size (paging and page numbers follow
    /// the expanded list). Null for a search without expansion.
    /// </summary>
    public SearchExpandedCounts? Expanded { get; init; }

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
/// Q-10 freshness of a result page (ADR-001 §7, baseline §28). <see cref="ServedGeneration"/> and <see cref="Current"/>
/// describe the page: the refresh-aware search watermark its point-in-time reader was opened at, and whether nothing
/// committed in the workspace is missing from it. The other fields describe the workspace's index at serve time, as
/// <c>GET …/search-freshness</c> does: a page can be not current while the index already is (run the search again).
/// Null <see cref="Current"/> means unknown; counts are then labelled approximate. Raw generations are shown to admins
/// and support only; reviewers see "current as of <see cref="AsOf"/>" or "search index updating".
/// </summary>
/// <param name="State">The index at serve time: current, updating, or delayed (unreflected work older than 2 minutes).</param>
/// <param name="IndexedThroughGeneration">The refresh-aware watermark at serve time: every change up to it is searchable.</param>
/// <param name="PendingChanges">Committed changes (interactive saves, job chunks) not yet searchable at serve time; approximate.</param>
/// <param name="LagSeconds">Now − commit time of the oldest change not yet searchable; 0 when current.</param>
public sealed record SearchFreshness(
    long? ServedGeneration,
    bool? Current,
    DateTimeOffset AsOf,
    SearchFreshnessState State,
    long IndexedThroughGeneration,
    long PendingChanges,
    double LagSeconds);

/// <summary>Whether search reflects every committed change (Q-10).</summary>
public enum SearchFreshnessState
{
    /// <summary>Every committed change is searchable.</summary>
    Current,

    /// <summary>Recent changes are still being indexed.</summary>
    Updating,

    /// <summary>The oldest change search does not reflect yet was committed more than 2 minutes ago.</summary>
    Delayed,
}

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/search-freshness</c>: the workspace's search index freshness, cheap enough
/// to poll every few seconds.
/// </summary>
/// <param name="State">Current, updating, or delayed (unreflected work older than 2 minutes).</param>
/// <param name="IndexedThroughGeneration">Refresh-aware watermark: every change with a generation at or below it is searchable.</param>
/// <param name="LatestGeneration">The last committed search generation of the workspace.</param>
/// <param name="PendingChanges">Committed changes not yet searchable (<c>latestGeneration − indexedThroughGeneration</c>).</param>
/// <param name="LagSeconds">Now − commit time of the oldest change not yet searchable; 0 when current.</param>
/// <param name="AsOf">When this was read.</param>
public sealed record SearchFreshnessStatus(
    SearchFreshnessState State,
    long IndexedThroughGeneration,
    long LatestGeneration,
    long PendingChanges,
    double LagSeconds,
    DateTimeOffset AsOf);

/// <summary>
/// A result row: grid fields and bounded snippets only, never full text (ADR-015 D8.5). Further columns come from the
/// projection mapping (E07-T02) and the field catalogue.
/// </summary>
/// <param name="ParentDocumentId">The immediate parent; set exactly for attachments (family members other than the top).</param>
/// <param name="IsFamilyParent">
/// The top-level document of a family with at least one other member the caller's search can see (the grid's parent
/// marker). False for standalone documents and attachments.
/// </param>
/// <param name="ExpandedBy">
/// In an expanded search: why the row was added (family, duplicate or thread); null for a row that matches the query.
/// </param>
/// <param name="FamilyDate">"Date (Family)": the family's date (ADR-009 R25), shared by every member.</param>
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
    bool IsFamilyParent = false,
    SearchExpandedBy? ExpandedBy = null,
    DateTimeOffset? FamilyDate = null);

/// <summary>A snippet as plain text with the matched ranges (UTF-16 offsets), so clients never render markup from the index.</summary>
public sealed record SearchSnippet(string Text, IReadOnlyList<TextSpan> Highlights);

public sealed record SearchFacet(string Field, IReadOnlyList<SearchFacetBucket> Buckets);

public sealed record SearchFacetBucket(string Value, long Count);
