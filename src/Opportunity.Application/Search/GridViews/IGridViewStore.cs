using Opportunity.Application.Audit;
using Opportunity.Contracts.Search;

namespace Opportunity.Application.Search.GridViews;

/// <summary>A stored grid view (E16-T09).</summary>
public sealed record GridViewRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ViewId { get; init; }

    public required string Name { get; init; }

    public required Guid OwnerId { get; init; }

    public required string OwnerDisplayName { get; init; }

    public required bool Shared { get; init; }

    public required IReadOnlyList<GridViewColumn> Columns { get; init; }

    public required IReadOnlyList<SearchSortKey> Sort { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    public required long Version { get; init; }
}

/// <summary>What a create or replace writes (validated by <see cref="GridViewService"/>).</summary>
public sealed record GridViewDefinition(string Name, bool Shared, IReadOnlyList<GridViewColumn> Columns, IReadOnlyList<SearchSortKey> Sort);

/// <summary>Who reads: their own personal views and every shared view; Workspace Admins see every view.</summary>
public sealed record GridViewViewer(Guid UserId, bool SeesAll);

/// <summary>A user's list layout in a workspace: the view last used and their adjustments to it (full column and sort sets).</summary>
public sealed record GridLayoutRecord(Guid? ViewId, IReadOnlyList<GridViewColumn>? Columns, IReadOnlyList<SearchSortKey>? Sort, DateTimeOffset ModifiedAt);

public enum GridViewWriteStatus
{
    Ok,
    NotFound,
    VersionConflict,

    /// <summary>Another shared view, or another personal view of the owner, has the name.</summary>
    DuplicateName,

    /// <summary>The owner (personal) or the workspace (shared) already has <see cref="GridViewLimits.MaxViews"/> views.</summary>
    LimitReached,
}

public sealed record GridViewWriteResult(GridViewWriteStatus Status, GridViewRecord? View = null);

/// <summary>PostgreSQL store of grid views and layouts (tenant tables under RLS). Writes insert their audit event, when given, in the same transaction.</summary>
public interface IGridViewStore
{
    /// <summary>The views <paramref name="viewer"/> sees: shared first, then by name.</summary>
    Task<IReadOnlyList<GridViewRecord>> ListAsync(Guid workspaceId, GridViewViewer viewer, CancellationToken cancellationToken = default);

    /// <summary>The view when it exists and <paramref name="viewer"/> sees it.</summary>
    Task<GridViewRecord?> GetAsync(Guid workspaceId, Guid viewId, GridViewViewer viewer, CancellationToken cancellationToken = default);

    Task<GridViewWriteResult> CreateAsync(
        Guid workspaceId, Guid viewId, Guid ownerId, GridViewDefinition definition, AuditEvent? audit, CancellationToken cancellationToken = default);

    /// <summary>Replaces the view when its version is <paramref name="expectedVersion"/> (any version when null).</summary>
    Task<GridViewWriteResult> UpdateAsync(
        Guid workspaceId, Guid viewId, long? expectedVersion, Guid modifiedBy, GridViewDefinition definition, AuditEvent? audit,
        CancellationToken cancellationToken = default);

    Task<GridViewWriteResult> DeleteAsync(Guid workspaceId, Guid viewId, long? expectedVersion, AuditEvent? audit, CancellationToken cancellationToken = default);

    Task<GridLayoutRecord?> GetLayoutAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default);

    Task<GridLayoutRecord> SaveLayoutAsync(
        Guid workspaceId, Guid userId, Guid? viewId, IReadOnlyList<GridViewColumn>? columns, IReadOnlyList<SearchSortKey>? sort,
        CancellationToken cancellationToken = default);
}
