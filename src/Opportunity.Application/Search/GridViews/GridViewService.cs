using System.Diagnostics;
using System.Globalization;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.Security;

namespace Opportunity.Application.Search.GridViews;

public enum GridViewOutcomeStatus
{
    Ok,
    Created,

    /// <summary>Does not exist or is not visible to the caller: 404.</summary>
    NotFound,

    /// <summary>Visible but not changeable by the caller (a shared view without <c>View.ManageShared</c>, another user's personal view): 403.</summary>
    Forbidden,

    /// <summary>The <c>If-Match</c> version is stale: 412.</summary>
    VersionConflict,

    InvalidRequest,

    /// <summary>The name is taken, or the view limit is reached: 409.</summary>
    Conflict,
}

/// <summary>A view as the caller sees it.</summary>
public sealed record GridView(GridViewRecord Record, bool CanEdit);

public sealed record GridViewOutcome
{
    public required GridViewOutcomeStatus Status { get; init; }

    public GridView? View { get; init; }

    public GridLayoutRecord? Layout { get; init; }

    public string? ConflictDetail { get; init; }

    public IReadOnlyDictionary<string, string[]> RequestErrors { get; init; } = new Dictionary<string, string[]>();

    internal static GridViewOutcome Of(GridViewOutcomeStatus status) => new() { Status = status };

    internal static GridViewOutcome Invalid(string field, string message) =>
        new() { Status = GridViewOutcomeStatus.InvalidRequest, RequestErrors = new Dictionary<string, string[]> { [field] = [message] } };
}

/// <summary>
/// Saved document-list views and each user's list layout (E16-T09, familiarity guide "View"). Callers see their own
/// personal views and every shared view; Workspace Admins see all. Creating, changing or deleting a shared view (and
/// making a view shared or personal) needs <c>View.ManageShared</c> and is audited in the transaction of the change;
/// personal views are changed by their owner or a Workspace Admin and are not audited. Sort fields must be sortable
/// (<see cref="ISearchSortFields"/>), at most <see cref="GridViewLimits.MaxSortFields"/>; Control Number breaks ties.
/// </summary>
public sealed class GridViewService(
    IGridViewStore store,
    IAuthorizationService authorization,
    TimeProvider time,
    ISearchSortFields? sortFields = null)
{
    private const string ResourceType = "GridView";
    private const string CorrelationTag = "opportunity.correlation_id";

    public async Task<IReadOnlyList<GridView>> ListAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var access = await AccessAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var views = await store.ListAsync(workspaceId, access.Viewer, cancellationToken).ConfigureAwait(false);
        return [.. views.Select(v => new GridView(v, access.CanEdit(v)))];
    }

    public async Task<GridViewOutcome> GetAsync(SecurityPrincipal principal, Guid workspaceId, Guid viewId, CancellationToken cancellationToken = default)
    {
        var access = await AccessAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        return await store.GetAsync(workspaceId, viewId, access.Viewer, cancellationToken).ConfigureAwait(false) is { } view
            ? new GridViewOutcome { Status = GridViewOutcomeStatus.Ok, View = new GridView(view, access.CanEdit(view)) }
            : GridViewOutcome.Of(GridViewOutcomeStatus.NotFound);
    }

    public async Task<GridViewOutcome> CreateAsync(
        SecurityPrincipal principal, Guid workspaceId, GridViewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var access = await AccessAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var (definition, problem) = await ValidateAsync(workspaceId, request, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (definition!.Shared && !access.ManagesShared)
        {
            return GridViewOutcome.Of(GridViewOutcomeStatus.Forbidden);
        }

        var id = Guid.CreateVersion7();
        var audit = definition.Shared
            ? Event(principal, workspaceId, id, AuditTaxonomy.GridView.Created, Details(id, null, definition, 1))
            : null;
        var result = await store.CreateAsync(workspaceId, id, principal.UserId, definition, audit, cancellationToken).ConfigureAwait(false);
        return Write(result, GridViewOutcomeStatus.Created, access);
    }

    public async Task<GridViewOutcome> UpdateAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid viewId, long? expectedVersion, GridViewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var (access, current, denied) = await ForChangeAsync(principal, workspaceId, viewId, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        var (definition, problem) = await ValidateAsync(workspaceId, request with { Visibility = request.Visibility ?? Visibility(current!) },
            cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (definition!.Shared != current!.Shared && !access!.ManagesShared)
        {
            return GridViewOutcome.Of(GridViewOutcomeStatus.Forbidden);
        }

        AuditEvent? audit = null;
        if (current.Shared || definition.Shared)
        {
            var details = Details(viewId, current, definition, current.Version + 1);
            details["changed"] = string.Join(',', Changed(current, definition));
            audit = Event(principal, workspaceId, viewId, AuditTaxonomy.GridView.Modified, details);
        }

        var result = await store.UpdateAsync(workspaceId, viewId, expectedVersion, principal.UserId, definition, audit, cancellationToken)
            .ConfigureAwait(false);
        return Write(result, GridViewOutcomeStatus.Ok, access!);
    }

    public async Task<GridViewOutcome> DeleteAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid viewId, long? expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var (access, current, denied) = await ForChangeAsync(principal, workspaceId, viewId, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        var audit = current!.Shared
            ? Event(principal, workspaceId, viewId, AuditTaxonomy.GridView.Deleted, new Dictionary<string, string?>
            {
                ["viewId"] = viewId.ToString(),
                ["visibility"] = "shared",
                ["ownerId"] = current.OwnerId.ToString(),
                ["version"] = Invariant(current.Version),
            })
            : null;
        var result = await store.DeleteAsync(workspaceId, viewId, expectedVersion, audit, cancellationToken).ConfigureAwait(false);
        return Write(result, GridViewOutcomeStatus.Ok, access!);
    }

    /// <summary>The caller's layout; a view that was deleted or is no longer visible reads as the Default view (null).</summary>
    public async Task<GridLayoutRecord?> GetLayoutAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (await store.GetLayoutAsync(workspaceId, principal.UserId, cancellationToken).ConfigureAwait(false) is not { } layout)
        {
            return null;
        }

        if (layout.ViewId is { } viewId)
        {
            var access = await AccessAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
            if (await store.GetAsync(workspaceId, viewId, access.Viewer, cancellationToken).ConfigureAwait(false) is null)
            {
                return layout with { ViewId = null };
            }
        }

        return layout;
    }

    public async Task<GridViewOutcome> SaveLayoutAsync(
        SecurityPrincipal principal, Guid workspaceId, GridLayoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ViewId is { } viewId)
        {
            var access = await AccessAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
            if (await store.GetAsync(workspaceId, viewId, access.Viewer, cancellationToken).ConfigureAwait(false) is null)
            {
                return GridViewOutcome.Invalid("viewId", "No such view.");
            }
        }

        IReadOnlyList<GridViewColumn>? columns = null;
        if (request.Columns is not null)
        {
            (columns, var columnProblem) = Columns(request.Columns);
            if (columnProblem is not null)
            {
                return columnProblem;
            }
        }

        IReadOnlyList<SearchSortKey>? sort = null;
        if (request.Sort is not null)
        {
            (sort, var sortProblem) = await SortAsync(workspaceId, request.Sort, cancellationToken).ConfigureAwait(false);
            if (sortProblem is not null)
            {
                return sortProblem;
            }
        }

        var saved = await store.SaveLayoutAsync(workspaceId, principal.UserId, request.ViewId, columns, sort, cancellationToken).ConfigureAwait(false);
        return new GridViewOutcome { Status = GridViewOutcomeStatus.Ok, Layout = saved };
    }

    private async Task<(Access? Access, GridViewRecord? Current, GridViewOutcome? Denied)> ForChangeAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid viewId, CancellationToken cancellationToken)
    {
        var access = await AccessAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        if (await store.GetAsync(workspaceId, viewId, access.Viewer, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return (null, null, GridViewOutcome.Of(GridViewOutcomeStatus.NotFound));
        }

        return access.CanEdit(current) ? (access, current, null) : (access, current, GridViewOutcome.Of(GridViewOutcomeStatus.Forbidden));
    }

    private async Task<Access> AccessAsync(SecurityPrincipal principal, Guid workspaceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var effective = await authorization.GetEffectivePermissionsAsync(principal, workspaceId, cancellationToken).ConfigureAwait(false);
        var allowed = effective.Decision.IsAllowed;
        return new Access(
            new GridViewViewer(principal.UserId, allowed && effective.Permissions.Contains(Permission.WorkspaceManageUsers)),
            allowed && effective.Permissions.Contains(Permission.ViewManageShared));
    }

    private async Task<(GridViewDefinition? Definition, GridViewOutcome? Problem)> ValidateAsync(
        Guid workspaceId, GridViewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > GridViewLimits.MaxNameLength)
        {
            return (null, GridViewOutcome.Invalid("name", $"The name must be 1–{GridViewLimits.MaxNameLength} characters."));
        }

        if (request.Visibility is { } visibility && !Enum.IsDefined(visibility))
        {
            return (null, GridViewOutcome.Invalid("visibility", "Visibility is personal or shared."));
        }

        var (columns, columnProblem) = Columns(request.Columns ?? []);
        if (columnProblem is not null)
        {
            return (null, columnProblem);
        }

        var (sort, sortProblem) = await SortAsync(workspaceId, request.Sort ?? [], cancellationToken).ConfigureAwait(false);
        if (sortProblem is not null)
        {
            return (null, sortProblem);
        }

        return (new GridViewDefinition(name, request.Visibility == GridViewVisibility.Shared, columns!, sort!), null);
    }

    private static (IReadOnlyList<GridViewColumn>? Columns, GridViewOutcome? Problem) Columns(IReadOnlyList<GridViewColumn> requested)
    {
        var columns = new List<GridViewColumn>();
        foreach (var column in requested)
        {
            var field = column?.Field?.Trim();
            if (string.IsNullOrEmpty(field) || field.Length > GridViewLimits.MaxNameLength)
            {
                return (null, GridViewOutcome.Invalid("columns", $"Columns are field query names of 1–{GridViewLimits.MaxNameLength} characters."));
            }

            if (column!.Width is { } width && (width < GridViewLimits.MinWidth || width > GridViewLimits.MaxWidth))
            {
                return (null, GridViewOutcome.Invalid("columns", $"Column widths are {GridViewLimits.MinWidth}–{GridViewLimits.MaxWidth} pixels."));
            }

            columns.Add(column with { Field = field });
        }

        if (columns.Count > GridViewLimits.MaxColumns
            || columns.Select(c => c.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count)
        {
            return (null, GridViewOutcome.Invalid("columns", $"At most {GridViewLimits.MaxColumns} distinct columns."));
        }

        return (columns, null);
    }

    private async Task<(IReadOnlyList<SearchSortKey>? Sort, GridViewOutcome? Problem)> SortAsync(
        Guid workspaceId, IReadOnlyList<SearchSortKey> requested, CancellationToken cancellationToken)
    {
        var sort = new List<SearchSortKey>();
        foreach (var key in requested)
        {
            if (key is null || !Enum.IsDefined(key.Direction) || string.IsNullOrWhiteSpace(key.Field) || key.Field.Length > GridViewLimits.MaxNameLength)
            {
                return (null, GridViewOutcome.Invalid("sort", "Sort keys are a field and a direction (asc or desc)."));
            }

            var field = sortFields is not null
                ? await sortFields.ResolveAsync(workspaceId, key.Field, cancellationToken).ConfigureAwait(false)
                : SearchSortFields.All.FirstOrDefault(f => string.Equals(f, key.Field, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                return (null, GridViewOutcome.Invalid("sort", $"“{key.Field}” cannot be sorted."));
            }

            sort.Add(new SearchSortKey(field, key.Direction));
        }

        if (sort.Count > GridViewLimits.MaxSortFields
            || sort.Select(s => s.Field).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sort.Count)
        {
            return (null, GridViewOutcome.Invalid("sort", $"Sort by at most {GridViewLimits.MaxSortFields} distinct fields."));
        }

        return (sort, null);
    }

    private static GridViewOutcome Write(GridViewWriteResult result, GridViewOutcomeStatus success, Access access) => result.Status switch
    {
        GridViewWriteStatus.Ok => new GridViewOutcome
        {
            Status = success,
            View = result.View is { } view ? new GridView(view, access.CanEdit(view)) : null,
        },
        GridViewWriteStatus.VersionConflict => GridViewOutcome.Of(GridViewOutcomeStatus.VersionConflict),
        GridViewWriteStatus.DuplicateName => new GridViewOutcome
        {
            Status = GridViewOutcomeStatus.Conflict,
            ConflictDetail = "A view with this name already exists.",
        },
        GridViewWriteStatus.LimitReached => new GridViewOutcome
        {
            Status = GridViewOutcomeStatus.Conflict,
            ConflictDetail = $"At most {GridViewLimits.MaxViews} views; delete one first.",
        },
        _ => GridViewOutcome.Of(GridViewOutcomeStatus.NotFound),
    };

    private static GridViewVisibility Visibility(GridViewRecord view) => view.Shared ? GridViewVisibility.Shared : GridViewVisibility.Personal;

    private static IEnumerable<string> Changed(GridViewRecord current, GridViewDefinition next)
    {
        if (!string.Equals(current.Name, next.Name, StringComparison.Ordinal))
        {
            yield return "name";
        }

        if (current.Shared != next.Shared)
        {
            yield return "visibility";
        }

        if (!current.Columns.SequenceEqual(next.Columns))
        {
            yield return "columns";
        }

        if (!current.Sort.SequenceEqual(next.Sort))
        {
            yield return "sort";
        }
    }

    /// <summary>IDs and settings only: the name is user content and stays out of audit details (ADR-013 §7).</summary>
    private static Dictionary<string, string?> Details(Guid id, GridViewRecord? current, GridViewDefinition definition, long version)
    {
        var details = new Dictionary<string, string?>
        {
            ["viewId"] = id.ToString(),
            ["visibility"] = definition.Shared ? "shared" : "personal",
            ["columns"] = Invariant(definition.Columns.Count),
            ["sort"] = string.Join(',', definition.Sort.Select(s => s.Field + ":" + (s.Direction == SearchSortDirection.Desc ? "desc" : "asc"))),
            ["version"] = Invariant(version),
        };
        if (current is not null)
        {
            details["previousVisibility"] = current.Shared ? "shared" : "personal";
            details["ownerId"] = current.OwnerId.ToString();
        }

        return details;
    }

    private AuditEvent Event(SecurityPrincipal actor, Guid workspaceId, Guid viewId, string action, IReadOnlyDictionary<string, string?> details) => new()
    {
        WorkspaceId = workspaceId,
        OccurredAt = time.GetUtcNow(),
        Category = AuditTaxonomy.GridView.Category,
        Action = action,
        ActorType = AuditActorType.User,
        ActorId = actor.UserId.ToString(),
        ActorDisplay = actor.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
            ? actor.DisplayName[..AuditEventRules.MaxActorDisplayLength]
            : actor.DisplayName,
        ClientIp = actor.ClientIp,
        UserAgent = actor.UserAgent,
        ResourceType = ResourceType,
        ResourceId = viewId.ToString(),
        Outcome = AuditOutcome.Success,
        CorrelationId = actor.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
        Details = details,
    };

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record Access(GridViewViewer Viewer, bool ManagesShared)
    {
        /// <summary>Shared views: <c>View.ManageShared</c>. Personal views: the owner or a Workspace Admin.</summary>
        public bool CanEdit(GridViewRecord view) => view.Shared ? ManagesShared : view.OwnerId == Viewer.UserId || Viewer.SeesAll;
    }
}
