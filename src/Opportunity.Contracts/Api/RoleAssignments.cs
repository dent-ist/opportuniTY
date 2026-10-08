namespace Opportunity.Contracts.Api;

/// <summary>One permission of the closed catalogue (docs/security/permission-matrix.md).</summary>
/// <param name="Name">Dotted wire name, e.g. <c>Document.View</c>.</param>
/// <param name="BreakGlassEligible">An active break-glass activation may exercise it past classes and walls (Q-45).</param>
public sealed record PermissionResource(string Name, string Description, bool BreakGlassEligible);

/// <summary>A built-in workspace role and the permissions it grants (fixed in code; custom roles are post-MVP).</summary>
/// <param name="Key">The role key stored in assignments, e.g. <c>QcReviewer</c>.</param>
/// <param name="UsersOnly">Assignable to individual users only (Break-glass).</param>
/// <param name="NeedsInstallationAdmin">Only an Installation Admin may assign it (Break-glass, ADR-015 D6.4).</param>
public sealed record RoleResource(string Key, string DisplayName, IReadOnlyList<string> Permissions, bool UsersOnly, bool NeedsInstallationAdmin);

/// <summary><c>GET …/roles</c>: the roles × permissions matrix of the workspace.</summary>
public sealed record RoleCatalogResource(IReadOnlyList<RoleResource> Roles, IReadOnlyList<PermissionResource> Permissions);

public enum RolePrincipalKind
{
    User,
    Group,
}

/// <summary>A user or IdP group with the roles they hold in the workspace.</summary>
/// <param name="DisplayName">The user's display name (or email), or the group name.</param>
/// <param name="Roles">Role keys, in catalogue order.</param>
/// <param name="AppliesToYou">The assignment applies to the caller (their user or one of their groups): they may give these roles up
/// after confirming, but never add to them (ADR-015 D6.5).</param>
public sealed record RoleAssignmentPrincipalResource(
    RolePrincipalKind Kind,
    Guid? UserId,
    string? GroupName,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> Roles,
    bool AppliesToYou);

/// <summary><c>GET …/role-assignments</c>: every user and group holding a role, by name.</summary>
/// <param name="Version">Version of the whole assignment set; also the ETag (<c>If-Match</c> on every change).</param>
/// <param name="AdministratorPaths">Workspace Admin assignments (users and groups); the last one cannot be removed.</param>
/// <param name="CanAssignBreakGlass">The caller is an Installation Admin and may assign Break-glass.</param>
public sealed record RoleAssignmentListResource(
    IReadOnlyList<RoleAssignmentPrincipalResource> Items, long Version, int AdministratorPaths, bool CanAssignBreakGlass);

/// <summary>Body of <c>PUT …/role-assignments/users/{userId}</c> and <c>PUT …/role-assignments/groups?name=</c>.</summary>
/// <param name="Roles">Every role the user or group should hold afterwards; empty removes them from the workspace.</param>
/// <param name="ConfirmSelfRemoval">Required when the change removes a role that applies to the caller.</param>
public sealed record RoleAssignmentRequest(IReadOnlyList<string>? Roles, bool ConfirmSelfRemoval = false);

/// <summary>Answer of a role change: the principal as it is now and the new set version (also the ETag).</summary>
public sealed record RoleAssignmentChangeResource(RoleAssignmentPrincipalResource Principal, long Version, int AdministratorPaths);

/// <summary>A user (after their first sign-in) or IdP group roles can be assigned to.</summary>
public sealed record RoleAssignmentCandidateResource(RolePrincipalKind Kind, Guid? UserId, string? GroupName, string DisplayName, string? Email);

/// <summary><c>GET …/role-assignments/candidates?q=</c>.</summary>
public sealed record RoleAssignmentCandidateList(IReadOnlyList<RoleAssignmentCandidateResource> Items);
