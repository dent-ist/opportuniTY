using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;

namespace Opportunity.Application.Workspaces;

/// <summary>The settings a workspace administrator edits (E04-T05). Values are already trimmed and validated.</summary>
public sealed record WorkspaceSettings(string Name, string? MatterNumber, string DisplayTimeZone, string StorageProfile);

public enum WorkspaceWriteOutcome
{
    Ok,
    NotFound,
    VersionConflict,

    /// <summary>The display time zone is not an IANA zone known to PostgreSQL.</summary>
    InvalidTimeZone,

    /// <summary>The storage profile cannot change once the workspace has stored an object.</summary>
    StorageProfileLocked,
}

public sealed record WorkspaceWriteResult(WorkspaceWriteOutcome Outcome, Workspace? Workspace = null);

/// <summary>Keyset position in the caller's workspace list (case-insensitive name, then id).</summary>
public sealed record WorkspaceListPosition(string SortName, Guid WorkspaceId);

public sealed record WorkspacePage(IReadOnlyList<Workspace> Items, long Total, WorkspaceListPosition? Next);

/// <summary>One role assignment: to a user (<see cref="UserId"/>) or to an IdP group (<see cref="GroupName"/>).</summary>
public sealed record WorkspaceMember(
    Guid AssignmentId, WorkspaceRole Role, Guid? UserId, string? UserDisplayName, string? GroupName, DateTimeOffset AssignedAt);

public sealed record WorkspaceMemberPage(IReadOnlyList<WorkspaceMember> Items, long Total, Guid? NextAfter);

/// <summary>
/// Workspace registry writes and the membership-scoped reads (E04-T05). Callers authorize first: creation needs
/// <c>Installation.ManageWorkspaces</c>, updates and member lists a workspace permission (PEP-1). Every change writes its
/// audit event in the same transaction (ADR-013 §2.1). The list never shows a workspace the PDP would answer 404 for.
/// </summary>
public interface IWorkspaceStore
{
    /// <summary>The workspaces <paramref name="principal"/> is a member of (PDP step 1), by name.</summary>
    Task<WorkspacePage> ListForPrincipalAsync(
        SecurityPrincipal principal, WorkspaceListPosition? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Creates the workspace and makes <paramref name="creator"/> its first Workspace Admin.</summary>
    Task<WorkspaceWriteResult> CreateAsync(
        Guid workspaceId, WorkspaceSettings settings, SecurityPrincipal creator, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult> UpdateAsync(
        Guid workspaceId, long expectedVersion, WorkspaceSettings settings, SecurityPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Role assignments of the workspace in assignment order.</summary>
    Task<WorkspaceMemberPage> ListMembersAsync(Guid workspaceId, Guid? after, int limit, CancellationToken cancellationToken = default);
}
