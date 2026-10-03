namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}</c>: the workspace and the current user's effective permissions in it
/// (dotted names from docs/security/permission-matrix.md). Permissions drive UI affordances only; every operation is
/// authorized again on the server. A workspace the user cannot access is a 404, indistinguishable from a missing one.
/// </summary>
public sealed record WorkspaceResource(
    Guid WorkspaceId,
    string Name,
    string? MatterNumber,
    string DisplayTimeZone,
    WorkspaceResourceStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Permissions,
    bool BreakGlassActive);

public enum WorkspaceResourceStatus
{
    Active,
    Closed,
}
