namespace Opportunity.Contracts.Search;

/// <summary>Who sees a grid view.</summary>
public enum GridViewVisibility
{
    /// <summary>Only its owner (and Workspace Admins) see it.</summary>
    Personal,

    /// <summary>Everyone in the workspace sees it; creating and changing it needs <c>View.ManageShared</c>.</summary>
    Shared,
}

/// <summary>One column of a view: a field query name with its width and whether it is pinned to the left.</summary>
/// <param name="Field">Field query name (<c>GET …/fields</c>), 1–200 characters. Control Number is always the first, pinned column and is not listed.</param>
/// <param name="Width">Width in CSS pixels (40–1200); null for the column's default width.</param>
/// <param name="Pinned">Kept in view when the list scrolls sideways (pinned columns come first, after Control Number).</param>
public sealed record GridViewColumn(string Field, int? Width = null, bool Pinned = false);

/// <summary>Who created a view.</summary>
public sealed record GridViewOwner(Guid UserId, string DisplayName);

/// <summary>
/// A saved grid view (E16-T09, familiarity guide "View"): the document list's columns and sort, personal or shared with
/// the workspace. The workspace's built-in Default view is not stored.
/// </summary>
/// <param name="CanEdit">Whether the caller may change or delete it (owner or Workspace Admin for a personal view; <c>View.ManageShared</c> for a shared one).</param>
/// <param name="Version">Optimistic-concurrency version; also the ETag (<c>If-Match</c> on change and delete).</param>
public sealed record GridViewResource(
    Guid ViewId,
    string Name,
    GridViewVisibility Visibility,
    GridViewOwner Owner,
    IReadOnlyList<GridViewColumn> Columns,
    IReadOnlyList<SearchSortKey> Sort,
    DateTimeOffset ModifiedAt,
    long Version,
    bool CanEdit);

/// <summary><c>GET …/grid-views</c>: the views the caller can see (their own, every shared view; all as Workspace Admin), shared first, by name.</summary>
public sealed record GridViewList(IReadOnlyList<GridViewResource> Items);

/// <summary>Body of <c>POST …/grid-views</c> (create) and <c>PUT …/grid-views/{viewId}</c> (replace, with <c>If-Match</c>).</summary>
/// <param name="Name">1–200 characters, unique (case-insensitive) among the shared views, or among the owner's personal views.</param>
/// <param name="Visibility">Default personal. Shared needs <c>View.ManageShared</c>.</param>
/// <param name="Columns">At most 100 distinct columns.</param>
/// <param name="Sort">At most 3 distinct sortable fields (Control Number then breaks ties).</param>
public sealed record GridViewRequest(
    string? Name,
    GridViewVisibility? Visibility = null,
    IReadOnlyList<GridViewColumn>? Columns = null,
    IReadOnlyList<SearchSortKey>? Sort = null);

/// <summary>
/// The caller's document-list layout in a workspace (<c>GET/PUT …/grid-views/layout</c>): the view they last used and
/// their unsaved adjustments to it, so the list opens the way they left it.
/// </summary>
/// <param name="ViewId">The view last used; null for the workspace's Default view (also when that view was deleted or is no longer visible).</param>
/// <param name="Columns">Adjusted columns; null when the view's own columns apply.</param>
/// <param name="Sort">Adjusted sort; null when the view's own sort applies.</param>
/// <param name="ModifiedAt">When it was last saved; null when it never was.</param>
public sealed record GridLayoutResource(
    Guid? ViewId,
    IReadOnlyList<GridViewColumn>? Columns,
    IReadOnlyList<SearchSortKey>? Sort,
    DateTimeOffset? ModifiedAt);

/// <summary>Body of <c>PUT …/grid-views/layout</c> (same limits as <see cref="GridViewRequest"/>).</summary>
public sealed record GridLayoutRequest(
    Guid? ViewId = null,
    IReadOnlyList<GridViewColumn>? Columns = null,
    IReadOnlyList<SearchSortKey>? Sort = null);

/// <summary>Limits of views and layouts.</summary>
public static class GridViewLimits
{
    public const int MaxNameLength = 200;
    public const int MaxColumns = 100;
    public const int MaxSortFields = 3;
    public const int MinWidth = 40;
    public const int MaxWidth = 1200;

    /// <summary>Views one user may own, and shared views one workspace may hold.</summary>
    public const int MaxViews = 200;
}
