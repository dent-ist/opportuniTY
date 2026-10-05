using Opportunity.Contracts.Api;

namespace Opportunity.Contracts.Search;

/// <summary>A folder of the workspace's saved-search tree (the Searches section, E07-T09, Q-65). Folders are shared by the workspace.</summary>
/// <param name="ParentFolderId">The parent folder; null for a top-level folder.</param>
/// <param name="Version">Optimistic-concurrency version; also the ETag (<c>If-Match</c> on rename and move).</param>
public sealed record SavedSearchFolderResource(Guid FolderId, string Name, Guid? ParentFolderId, long Version);

/// <summary><c>GET …/saved-search-folders</c>: every folder of the workspace, ordered by name.</summary>
public sealed record SavedSearchFolderList(IReadOnlyList<SavedSearchFolderResource> Items);

/// <summary>Body of <c>POST</c> (create) and <c>PUT</c> (rename and move) <c>…/saved-search-folders</c>.</summary>
/// <param name="Name">1–200 characters, unique among the folder's siblings (case-insensitive).</param>
/// <param name="ParentFolderId">The parent folder; null or absent for top level. A folder cannot move below itself.</param>
public sealed record SavedSearchFolderRequest(string? Name, Guid? ParentFolderId = null);

/// <summary>Who owns a saved search.</summary>
public sealed record SavedSearchOwner(Guid UserId, string DisplayName);

public enum SavedSearchScope
{
    /// <summary>Only the owner (and Workspace Admins) can see it.</summary>
    Private,

    /// <summary>Shared with at least one user or IdP group.</summary>
    Shared,
}

public enum SavedSearchShareKind
{
    User,
    Group,
}

/// <summary>A user or IdP group a saved search is shared with. For a group, <see cref="Id"/> and <see cref="DisplayName"/> are the group name.</summary>
public sealed record SavedSearchShareResource(SavedSearchShareKind Kind, string Id, string DisplayName);

public enum SavedSearchFreshnessState
{
    /// <summary>Every change committed before the run was searchable (Q-10).</summary>
    Current,

    /// <summary>The index was still catching up when the search ran: the hit count may be low.</summary>
    CatchingUp,
}

/// <summary>Q-10 freshness of the last run: <see cref="AsOf"/> is when it ran.</summary>
public sealed record SavedSearchFreshness(SavedSearchFreshnessState State, DateTimeOffset AsOf);

/// <summary>
/// A saved search as the Searches list shows it. The last-run fields are the most recent run by anyone and are a count
/// for that runner only (results always follow the permissions of whoever runs the search).
/// </summary>
/// <param name="LastHitRelation">Whether <see cref="LastHitCount"/> is exact (<c>eq</c>) or a lower bound (<c>gte</c>).</param>
/// <param name="Version">Optimistic-concurrency version; also the ETag.</param>
public sealed record SavedSearchSummary(
    Guid SavedSearchId,
    string Name,
    Guid? FolderId,
    SavedSearchOwner Owner,
    SavedSearchScope Scope,
    IReadOnlyList<SavedSearchShareResource> SharedWith,
    DateTimeOffset? LastRunAt,
    long? LastHitCount,
    TotalRelation? LastHitRelation,
    SavedSearchFreshness? LastRunFreshness,
    DateTimeOffset ModifiedAt,
    long Version);

/// <summary>
/// A saved search with its criteria (ADR-008 query text, re-parsed at every run), the grid columns and sort it opens with,
/// and the version of the AST schema the text was validated against. Nested saved searches are written in the query as
/// <c>savedsearch:&lt;savedSearchId&gt;</c>.
/// </summary>
/// <param name="Columns">Field query names of the grid columns, in order.</param>
/// <param name="IncludeFamily">Run with the hits' families (E09-T03 expansion; a run's own <c>expand</c> wins).</param>
/// <param name="IncludeDuplicates">Run with the hits' duplicates (E09-T03).</param>
/// <param name="IncludeThread">Run with the hits' email threads (E09-T03).</param>
public sealed record SavedSearchResource(
    Guid SavedSearchId,
    string Name,
    Guid? FolderId,
    SavedSearchOwner Owner,
    SavedSearchScope Scope,
    IReadOnlyList<SavedSearchShareResource> SharedWith,
    DateTimeOffset? LastRunAt,
    long? LastHitCount,
    TotalRelation? LastHitRelation,
    SavedSearchFreshness? LastRunFreshness,
    DateTimeOffset ModifiedAt,
    long Version,
    string Query,
    IReadOnlyList<string> Columns,
    IReadOnlyList<SearchSortKey> Sort,
    bool IncludeFamily,
    int AstVersion,
    bool IncludeDuplicates = false,
    bool IncludeThread = false);

/// <summary>Body of <c>POST …/saved-searches</c> (create) and <c>PUT …/saved-searches/{id}</c> (replace, with <c>If-Match</c>).</summary>
/// <param name="Name">1–200 characters.</param>
/// <param name="FolderId">The folder; null or absent for the top level.</param>
/// <param name="Query">Query-language text (empty: every document the runner may see). It must parse and bind now.</param>
/// <param name="Columns">Field query names of the grid columns (at most 100, distinct).</param>
/// <param name="Sort">At most 5 distinct sortable fields (see <see cref="SearchSortFields"/>).</param>
/// <param name="IncludeFamily">Run with the hits' families (default false).</param>
/// <param name="IncludeDuplicates">Run with the hits' duplicates (default false).</param>
/// <param name="IncludeThread">Run with the hits' email threads (default false).</param>
public sealed record SavedSearchRequest(
    string? Name,
    Guid? FolderId = null,
    string? Query = null,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<SearchSortKey>? Sort = null,
    bool? IncludeFamily = null,
    bool? IncludeDuplicates = null,
    bool? IncludeThread = null);

/// <summary>Body of <c>POST …/saved-searches/{id}/clone</c>: the copy is private to the caller.</summary>
/// <param name="Name">Default: "Copy of &lt;name&gt;".</param>
/// <param name="FolderId">Default: the source's folder.</param>
public sealed record SavedSearchCloneRequest(string? Name = null, Guid? FolderId = null);

/// <summary>A user (by user ID) or IdP group (by group name) to share with.</summary>
public sealed record SavedSearchShareTarget(SavedSearchShareKind Kind, string Id);

/// <summary>Body of <c>PUT …/saved-searches/{id}/sharing</c>: the complete list (an empty list makes the search private).</summary>
public sealed record SavedSearchSharingRequest(IReadOnlyList<SavedSearchShareTarget>? SharedWith);

/// <summary>Saved-search reference syntax in the query language (ADR-008 amendment, E07-T09).</summary>
public static class SavedSearchQuerySyntax
{
    /// <summary>
    /// The reserved field name: <c>savedsearch:&lt;savedSearchId&gt;</c> (D or N GUID format, optionally quoted) stands for
    /// the referenced search's criteria, re-parsed at run time. It may be combined with AND, OR, NOT and grouping, not
    /// used inside <c>W/n</c> or another field.
    /// </summary>
    public const string FieldName = "savedsearch";

    /// <summary>The longest chain of nested saved searches a query may reach.</summary>
    public const int MaxDepth = 8;
}

/// <summary>Positioned error codes of saved-search references (stable, add-only; same channel as the planner's codes).</summary>
public static class SavedSearchErrorCodes
{
    /// <summary>The referenced saved search does not exist or is not visible to the caller.</summary>
    public const string NotFound = "SAVED_SEARCH_NOT_FOUND";

    /// <summary>The reference is not a <c>savedsearch:&lt;id&gt;</c> term at Boolean level (e.g. inside <c>W/n</c>, a range or a wildcard).</summary>
    public const string InvalidReference = "SAVED_SEARCH_INVALID_REFERENCE";

    /// <summary>Saved searches that reference each other in a loop.</summary>
    public const string Cycle = "SAVED_SEARCH_CYCLE";

    /// <summary>More than <see cref="SavedSearchQuerySyntax.MaxDepth"/> levels of nesting.</summary>
    public const string TooDeep = "SAVED_SEARCH_TOO_DEEP";

    /// <summary>A referenced saved search no longer parses (its text is reported in the message).</summary>
    public const string Invalid = "SAVED_SEARCH_INVALID";
}

/// <summary>
/// <c>GET …/saved-searches/share-candidates</c>: the workspace's members (users with a role, directly or through one of
/// their groups as last seen at sign-in) and the IdP groups that hold a role, that the caller may share a search with.
/// </summary>
public sealed record SavedSearchShareCandidateList(IReadOnlyList<SavedSearchShareResource> Items);
