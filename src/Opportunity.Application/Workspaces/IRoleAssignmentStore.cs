using Opportunity.Application.Audit;
using Opportunity.Core.Security;

namespace Opportunity.Application.Workspaces;

/// <summary>Who a role assignment names: a user by id or an IdP group by name (exactly one of the two).</summary>
public sealed record RolePrincipal(Guid? UserId, string? GroupName)
{
    public static RolePrincipal User(Guid userId) => new(userId, null);

    public static RolePrincipal Group(string groupName) => new(null, groupName);

    public bool IsGroup => GroupName is not null;

    public bool Matches(Guid? userId, string? groupName) =>
        UserId is { } id ? userId == id : string.Equals(GroupName, groupName, StringComparison.Ordinal);
}

/// <summary>One stored role assignment with the display attributes of its user (from the installation's user table).</summary>
public sealed record RoleAssignmentEntry(
    Guid AssignmentId, WorkspaceRole Role, Guid? UserId, string? GroupName, string? DisplayName, string? Email, DateTimeOffset AssignedAt);

/// <summary>Every role assignment of a workspace and the version of the set (the ETag of the role-assignment matrix).</summary>
public sealed record RoleAssignmentSet(IReadOnlyList<RoleAssignmentEntry> Assignments, long Version)
{
    /// <summary>Workspace Admin assignments, to users or groups: the paths to administering the workspace.</summary>
    public int AdministratorPaths => Assignments.Count(a => a.Role == WorkspaceRole.WorkspaceAdmin);

    public IReadOnlySet<WorkspaceRole> RolesOf(RolePrincipal principal) =>
        Assignments.Where(a => principal.Matches(a.UserId, a.GroupName)).Select(a => a.Role).ToHashSet();
}

/// <summary>A user or IdP group an administrator may assign roles to (users appear after their first sign-in).</summary>
public sealed record RoleAssignmentCandidate(Guid? UserId, string? GroupName, string DisplayName, string? Email);

public enum RoleAssignmentWriteStatus
{
    Ok,
    NotFound,
    VersionConflict,

    /// <summary>The change would leave the workspace without a Workspace Admin assignment.</summary>
    LastAdministrator,

    /// <summary>A role was added for a user id that has never signed in to the installation.</summary>
    UnknownUser,
}

public sealed record RoleAssignmentWrite(RoleAssignmentWriteStatus Status, RoleAssignmentSet? Set = null);

/// <summary>
/// PostgreSQL state of workspace role assignments (V0012, V0046). A replace locks the workspace row, checks the set
/// version, writes the assignments, keeps at least one Workspace Admin assignment, ends live break-glass activations of
/// a user whose BreakGlass role is removed, bumps the version and inserts one audit event per assigned or revoked role,
/// all in one workspace transaction.
/// </summary>
public interface IRoleAssignmentStore
{
    /// <summary>The workspace's assignments, or null when the workspace does not exist.</summary>
    Task<RoleAssignmentSet?> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <param name="audit">Template of the events: the store sets action, resource id and details per changed role.</param>
    Task<RoleAssignmentWrite> ReplaceAsync(
        Guid workspaceId, RolePrincipal principal, IReadOnlySet<WorkspaceRole> roles, long expectedVersion, Guid actorId, AuditEvent audit,
        CancellationToken cancellationToken = default);

    /// <summary>Users (by display name or email) and known IdP groups whose name contains <paramref name="contains"/>.</summary>
    Task<IReadOnlyList<RoleAssignmentCandidate>> FindCandidatesAsync(
        string? contains, int limit, CancellationToken cancellationToken = default);
}
