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
