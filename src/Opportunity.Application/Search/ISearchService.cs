using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;

namespace Opportunity.Application.Search;

/// <summary>
/// The single logical search path (ADR-006 R7–R10, ADR-015 D8, E07-T05). Every search, page, count, facet and
/// highlight goes through it. Implementations build the outer filter from server state only (workspace term and
/// routing, restriction classes and walls from the PDP), keep point-in-time readers and <c>search_after</c> values
/// server-side behind opaque handles bound to (user, session, workspace), and re-check every returned page against
/// PostgreSQL (Q-12) before anything leaves the service.
/// </summary>
public interface ISearchService
{
    /// <summary>Runs a new search and returns its first page. The full query text is audited (Q-16).</summary>
    Task<SearchOutcome> SearchAsync(SearchCaller caller, SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Another page of a search started by the same caller in the same session and workspace; any other caller,
    /// session or workspace, an unknown handle, a tampered cursor and an expired search all give
    /// <see cref="SearchStatus.NotFound"/>.
    /// </summary>
    Task<SearchOutcome> GetPageAsync(
        SearchCaller caller, string searchId, SearchPageRequest page, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates every document matching the query for snapshot materialization (ADR-002 §5.1): one point-in-time
    /// reader, sorted by <c>documentId</c>, IDs only (no source, snippets or facets), the same outer filter as every
    /// search, and the Q-12 PostgreSQL re-check of every page before it reaches <paramref name="onPage"/>. The exact hit
    /// count is taken first; nothing is delivered when it exceeds <see cref="SearchSelectionRequest.MaxHits"/>. No
    /// search handle is kept and no <c>Search.Executed</c> is written: the caller audits the selection together with
    /// what it froze (post-filter denials are audited here as one summary <c>AuthZ.Denied</c>, Q-59).
    /// </summary>
    Task<SearchSelectionOutcome> SelectAsync(
        SearchCaller caller,
        SearchSelectionRequest request,
        Func<IReadOnlyList<Guid>, CancellationToken, Task> onPage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of <paramref name="documentIds"/> match <paramref name="query"/> (search term reports, E07-T10): one search
    /// restricted to those IDs under the same outer filter as every search, IDs only, never partial (a timed-out or
    /// too-broad query is an <see cref="SearchSelectionStatus.InvalidQuery"/> with a positioned error), and the matches
    /// re-checked against PostgreSQL (Q-12). At most <see cref="MaxMatchDocuments"/> IDs per call. No search handle is
    /// kept and nothing is audited here: the caller audits the operation as a whole.
    /// </summary>
    Task<SearchMatchOutcome> MatchAsync(
        SearchCaller caller, string query, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>The most document IDs one <see cref="MatchAsync"/> call takes.</summary>
    public const int MaxMatchDocuments = 5_000;
}

/// <param name="Status">Ok, InvalidQuery (with <see cref="QueryErrors"/>), NotFound or Forbidden.</param>
/// <param name="Matches">The matching IDs the caller may view, in no particular order.</param>
public sealed record SearchMatchOutcome(SearchSelectionStatus Status, IReadOnlyList<Guid> Matches, IReadOnlyList<QueryValidationDiagnostic> QueryErrors)
{
    public static SearchMatchOutcome Of(SearchSelectionStatus status) => new(status, [], []);
}

/// <summary>
/// The hit set of one term of a completed search term report, for opening the term as a search (E07-T10). Implemented
/// by the report feature; the search service only filters by it. Null when the report or term does not exist or is not
/// visible to the caller.
/// </summary>
public interface ISearchTermHitSource
{
    Task<SearchTermHitSet?> GetAsync(SearchCaller caller, Guid reportId, Guid termId, CancellationToken cancellationToken = default);
}

public enum SearchTermHitSetStatus
{
    Ok,

    /// <summary>The report's current run has not completed.</summary>
    NotCompleted,

    /// <summary>The term has an error and no hits.</summary>
    TermError,

    /// <summary>More hits than <see cref="SearchTermHitSet.MaxDocuments"/>; open a narrower term.</summary>
    TooLarge,
}

/// <param name="Expression">The term's query-language expression (relevance and highlighting).</param>
/// <param name="DocumentIds">The hit documents as counted (empty unless <see cref="Status"/> is Ok).</param>
public sealed record SearchTermHitSet(SearchTermHitSetStatus Status, string Expression, IReadOnlyList<Guid> DocumentIds)
{
    /// <summary>The largest hit set that opens as a search: OpenSearch's default <c>index.max_terms_count</c>.</summary>
    public const int MaxDocuments = 65_536;
}

/// <param name="Query">Query-language text; empty selects every document the caller may see.</param>
/// <param name="MaxHits">Largest hit count to enumerate; above it the outcome is <see cref="SearchSelectionStatus.TooManyHits"/>.</param>
/// <param name="PageSize">IDs per OpenSearch page (and per <c>onPage</c> call, before the post-filter).</param>
/// <param name="RefreshFirst">
/// Refresh the index before the reader opens, so every change applied before the call is visible to the selection
/// (ADR-001 §7.3): the applied watermark read before the call becomes a true lower bound of what was selected.
/// </param>
public sealed record SearchSelectionRequest(string Query, long MaxHits, int PageSize = 5_000, bool RefreshFirst = true);

public enum SearchSelectionStatus
{
    Ok,

    /// <summary>The query does not parse or bind (positioned errors in <see cref="SearchSelectionOutcome.QueryErrors"/>).</summary>
    InvalidQuery,

    /// <summary>The caller is not a member of the workspace: 404.</summary>
    NotFound,

    /// <summary>The caller lacks <c>Search.Execute</c>: 403.</summary>
    Forbidden,

    /// <summary>More hits than <see cref="SearchSelectionRequest.MaxHits"/>; nothing was delivered.</summary>
    TooManyHits,

    /// <summary>
    /// The point-in-time reader expired or its index went away mid-selection (ADR-002 §5.3): what was delivered is
    /// incomplete and must be discarded; the caller may restart once with a new reader.
    /// </summary>
    ReaderLost,
}

public sealed record SearchSelectionOutcome
{
    public required SearchSelectionStatus Status { get; init; }

    /// <summary>The normalized interpretation of the query (e.g. <c>a AND b</c>).</summary>
    public string Normalized { get; init; } = string.Empty;

    /// <summary>Canonical AST JSON (ADR-008 R13) of the query, for the audit record.</summary>
    public string? AstJson { get; init; }

    /// <summary>Exact number of hits of the outer-filtered query (before the PostgreSQL re-check).</summary>
    public long Hits { get; init; }

    /// <summary>IDs delivered to <c>onPage</c> after the re-check.</summary>
    public long Selected { get; init; }

    /// <summary>Hits dropped by the re-check (or the integrity check), by reason.</summary>
    public IReadOnlyDictionary<string, long> Dropped { get; init; } = new Dictionary<string, long>();

    /// <summary>The projection generation of the index the selection read; null when the workspace has no index yet.</summary>
    public int? ProjectionGeneration { get; init; }

    /// <summary>The caller's visibility relied on an active break-glass activation (audit access path).</summary>
    public bool BreakGlass { get; init; }

    public IReadOnlyList<QueryValidationDiagnostic> QueryErrors { get; init; } = [];

    public long DroppedTotal => Dropped.Values.Sum();
}

/// <summary>
/// Who searches where. <see cref="WorkspaceId"/> comes from the authorized route (PEP-1), never from a client field;
/// <see cref="SessionId"/> is the server-side session row (null for callers without a browser session).
/// </summary>
public sealed record SearchCaller(SecurityPrincipal Principal, Guid WorkspaceId, Guid? SessionId);

/// <summary>
/// Which page to serve (exactly one): an opaque <see cref="Cursor"/> from a previous page (next or previous), a page
/// <see cref="Number"/> within the first result window (Q-49: exact page numbers near the top), or the
/// <see cref="Last"/> page (served by reversing the sort, Q-49).
/// </summary>
public sealed record SearchPageRequest(string? Cursor = null, int? Number = null, bool Last = false);

public enum SearchStatus
{
    Ok,

    /// <summary>The request is malformed (page size, sort field, facet, page number): 400 with field errors.</summary>
    InvalidRequest,

    /// <summary>The query does not parse or bind: 400 with positioned errors, like the validate endpoint.</summary>
    InvalidQuery,

    /// <summary>No such search for this caller (or not visible to them): 404.</summary>
    NotFound,

    /// <summary>The caller lacks <c>Search.Execute</c>: 403.</summary>
    Forbidden,
}

public sealed record SearchOutcome
{
    public required SearchStatus Status { get; init; }

    public SearchResultPage? Page { get; init; }

    public IReadOnlyList<QueryValidationDiagnostic> QueryErrors { get; init; } = [];

    public IReadOnlyDictionary<string, string[]> RequestErrors { get; init; } = new Dictionary<string, string[]>();

    public static SearchOutcome Ok(SearchResultPage page) => new() { Status = SearchStatus.Ok, Page = page };

    public static SearchOutcome NotFound { get; } = new() { Status = SearchStatus.NotFound };

    public static SearchOutcome Forbidden { get; } = new() { Status = SearchStatus.Forbidden };

    public static SearchOutcome InvalidQuery(IReadOnlyList<QueryValidationDiagnostic> errors) =>
        new() { Status = SearchStatus.InvalidQuery, QueryErrors = errors };

    public static SearchOutcome InvalidRequest(string field, string message) =>
        new() { Status = SearchStatus.InvalidRequest, RequestErrors = new Dictionary<string, string[]> { [field] = [message] } };
}
