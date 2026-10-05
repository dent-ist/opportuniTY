namespace Opportunity.Application.Search;

/// <summary>
/// Server-side state of running searches (ADR-006 R9, ADR-015 D8.6): the point-in-time reader and the
/// <c>search_after</c> positions never leave the API; clients hold only the opaque search and cursor IDs. Rows are
/// workspace-owned under forced RLS, so a handle used in another workspace cannot be found. Implemented by
/// <c>Opportunity.Data</c>.
/// </summary>
public interface ISearchSessionStore
{
    /// <summary>Stores a new search with the cursors of its first page; also removes the workspace's expired searches.</summary>
    Task CreateAsync(SearchSessionRecord search, IReadOnlyList<SearchCursorRecord> cursors, CancellationToken cancellationToken = default);

    /// <summary>The search, or null when it does not exist in this workspace or has expired.</summary>
    Task<SearchSessionRecord?> GetAsync(Guid workspaceId, Guid searchId, CancellationToken cancellationToken = default);

    /// <summary>The cursor of <paramref name="searchId"/>, or null when unknown or expired.</summary>
    Task<SearchCursorRecord?> GetCursorAsync(Guid workspaceId, Guid searchId, Guid cursorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the search, records a re-established point-in-time reader (<paramref name="reader"/>, null when the page
    /// ran on the existing one), and adds the cursors of a served page.
    /// </summary>
    Task TouchAsync(
        Guid workspaceId,
        Guid searchId,
        SearchReaderUpdate? reader,
        DateTimeOffset expiresAt,
        IReadOnlyList<SearchCursorRecord> cursors,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ADR-002 §8 per-user cap: detaches the readers of <paramref name="userId"/>'s live searches in the workspace
    /// beyond the <paramref name="keep"/> most recently opened, and returns their point-in-time IDs for the caller to
    /// close. A detached search keeps its handle and cursors; its next page re-establishes a reader.
    /// </summary>
    Task<IReadOnlyList<string>> DetachReadersAsync(Guid workspaceId, Guid userId, int keep, CancellationToken cancellationToken = default);
}

/// <summary>A point-in-time reader (re-)established for a search.</summary>
/// <param name="ServedGeneration">The applied watermark read before the reader opened (Q-10), when known.</param>
public sealed record SearchReaderUpdate(string PointInTimeId, DateTimeOffset OpenedAt, long? ServedGeneration);

/// <param name="QueryText">The submitted query-language text, re-planned for every page.</param>
/// <param name="SortJson">The resolved sort keys as JSON (owned by the search module).</param>
/// <param name="PointInTimeId">The search engine's point-in-time reader; a bearer token that never leaves the API.</param>
/// <param name="TotalExact">Whether <see cref="TotalValue"/> is exact (Q-32).</param>
public sealed record SearchSessionRecord(
    Guid WorkspaceId,
    Guid SearchId,
    Guid UserId,
    Guid? SessionId,
    string QueryText,
    string SortJson,
    int PageSize,
    bool CountExact,
    bool Highlight,
    string? PointInTimeId,
    long TotalValue,
    bool TotalExact,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// The applied search watermark read before the point-in-time reader opened (Q-10). A page is current while the
    /// workspace's generation counter still equals it. Null when unknown (older rows).
    /// </summary>
    public long? ServedGeneration { get; init; }

    /// <summary>When <see cref="PointInTimeId"/> was opened (ADR-002 §8 maximum age); null on older rows (use <see cref="CreatedAt"/>).</summary>
    public DateTimeOffset? PointInTimeOpenedAt { get; init; }
}

public enum SearchCursorDirection
{
    /// <summary>The page after <see cref="SearchCursorRecord.SortValuesJson"/> in sort order (next).</summary>
    After = 1,

    /// <summary>The page before it (previous), fetched with the sort reversed.</summary>
    Before = 2,
}

/// <param name="SortValuesJson">The <c>search_after</c> position (sort values of the boundary hit) as JSON.</param>
/// <param name="PageNumber">The 1-based number of the page this cursor leads to, when known.</param>
public sealed record SearchCursorRecord(
    Guid WorkspaceId,
    Guid SearchId,
    Guid CursorId,
    SearchCursorDirection Direction,
    string SortValuesJson,
    int? PageNumber,
    DateTimeOffset ExpiresAt);
