namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}</c>: the workspace and the current user's effective permissions in it
/// (dotted names from docs/security/permission-matrix.md). Permissions drive UI affordances only; every operation is
/// authorized again on the server. A workspace the user cannot access is a 404, indistinguishable from a missing one.
/// The response carries the settings version as its <c>ETag</c>; <see cref="Version"/> repeats it for clients.
/// </summary>
/// <param name="StorageProfile">Named object-storage profile of the installation; never a bucket, container or key.</param>
/// <param name="SearchPlacement">
/// Read-only: where index management (ADR-006) placed the search projection; null until the workspace is placed
/// (normally by its first import) or when search is not configured for this API host. Never a physical index name.
/// </param>
/// <param name="ActivePreservationLocks">
/// Active preservation locks (legal holds, E20-T01): above zero, deleting or purging the workspace's data answers 423.
/// Shown to every member; the locks themselves need <c>Workspace.ManageHolds</c>.
/// </param>
public sealed record WorkspaceResource(
    Guid WorkspaceId,
    string Name,
    string? MatterNumber,
    string DisplayTimeZone,
    WorkspaceResourceStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Permissions,
    bool BreakGlassActive,
    string? StorageProfile = null,
    long? Version = null,
    DateTimeOffset? UpdatedAt = null,
    WorkspaceSearchPlacementResource? SearchPlacement = null,
    int ActivePreservationLocks = 0);

public enum WorkspaceResourceStatus
{
    Active,
    Closed,
}

/// <summary>Search projection placement facts (ADR-006 R2): tier, projection generation and lifecycle state.</summary>
public sealed record WorkspaceSearchPlacementResource(
    WorkspaceSearchPlacementKind Kind, int ProjectionGeneration, WorkspaceSearchPlacementState State);

public enum WorkspaceSearchPlacementKind
{
    /// <summary>A routed slice of a shared index.</summary>
    Shared,

    /// <summary>An index of its own.</summary>
    Dedicated,
}

public enum WorkspaceSearchPlacementState
{
    Active,

    /// <summary>Rebuilding into a new projection generation.</summary>
    Building,

    /// <summary>Moving between shared and dedicated.</summary>
    Moving,

    Deleting,
}

/// <summary>One item of <c>GET /api/v1/workspaces</c>: a workspace the current user is a member of.</summary>
public sealed record WorkspaceSummary(
    Guid WorkspaceId,
    string Name,
    string? MatterNumber,
    string DisplayTimeZone,
    WorkspaceResourceStatus Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// Body of <c>POST /api/v1/workspaces</c> and <c>PUT /api/v1/workspaces/{workspaceId}</c>. Text is trimmed; an empty
/// matter number clears it. <see cref="StorageProfile"/> defaults to the first configured profile on create and to the current
/// profile on update; it can change only while the workspace has no stored objects.
/// </summary>
/// <param name="DisplayTimeZone">IANA time zone ID, e.g. <c>Europe/Berlin</c>; display only, timestamps stay UTC.</param>
public sealed record WorkspaceWrite(string Name, string DisplayTimeZone, string? MatterNumber = null, string? StorageProfile = null);

/// <summary>One role assignment of <c>GET /api/v1/workspaces/{workspaceId}/members</c>: to a user or to an IdP group.</summary>
public sealed record WorkspaceMemberResource(
    Guid AssignmentId,
    WorkspaceMemberKind Kind,
    string Role,
    Guid? UserId,
    string? DisplayName,
    string? GroupName,
    DateTimeOffset AssignedAt);

public enum WorkspaceMemberKind
{
    User,
    Group,
}
