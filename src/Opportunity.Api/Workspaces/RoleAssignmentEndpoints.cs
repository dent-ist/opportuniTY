using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Opportunity.Api.Conventions;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;
using Opportunity.Security.Authorization;

using AspNetAuthorization = Microsoft.AspNetCore.Authorization.IAuthorizationService;

namespace Opportunity.Api.Workspaces;

/// <summary>
/// Roles, permissions and user/group assignment (E05-T08; ADR-015 D5.6, D6.4, D6.5) for Admin › Users &amp; Groups and
/// Admin › Roles &amp; Security: the built-in roles with their permissions (read-only: grants are fixed in code), every
/// user and IdP group holding a role, and replacing the roles of one user or group. Everything needs
/// <c>Workspace.ManageUsers</c>; changes are versioned as one set (If-Match), audited in their own transaction and apply
/// to the very next request. Self-protection: nobody adds a role to themselves; giving up one's own role needs
/// <c>confirmSelfRemoval</c>; the last Workspace Admin assignment cannot be removed; Break-glass is assigned to users only
/// and by an Installation Admin only.
/// </summary>
public sealed class RoleAssignmentEndpoints : IApiEndpointModule
{
    public const string RolesPath = "/roles";
    public const string AssignmentsPath = "/role-assignments";

    private const string Tag = "Roles";

    private static readonly RoleCatalogResource Catalog = new(
        [.. RoleCatalog.All.Select(r => new RoleResource(
            r.Key,
            r.DisplayName,
            [.. PermissionCatalog.All.Where(p => r.Grants.Contains(p.Permission)).Select(p => p.Name)],
            UsersOnly: r.Role == WorkspaceRole.BreakGlass,
            NeedsInstallationAdmin: r.Role == WorkspaceRole.BreakGlass))],
        [.. PermissionCatalog.All.Select(p => new PermissionResource(p.Name, p.Description, p.BreakGlassEligible))]);

    public void MapEndpoints(ApiRouteGroups routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var ws = routes.Workspace;

        ws.MapGet(RolesPath, ListRoles)
            .WithName("ListWorkspaceRoles")
            .WithTags(Tag)
            .Produces<RoleCatalogResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("The built-in roles and the permissions each grants (fixed in code; docs/security/permission-matrix.md).")
            .RequirePermission(Permission.WorkspaceManageUsers);

        ws.MapGet(AssignmentsPath, ListAssignmentsAsync)
            .WithName("ListRoleAssignments")
            .WithTags(Tag)
            .Produces<RoleAssignmentListResource>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Every user and IdP group holding a role in the workspace, with the set version as ETag.")
            .RequirePermission(Permission.WorkspaceManageUsers);

        ws.MapPut(AssignmentsPath + "/users/{userId}", PutUserAsync)
            .WithName("PutUserRoleAssignments")
            .WithTags(Tag)
            .Produces<RoleAssignmentChangeResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Replace the roles a user holds in the workspace (If-Match: the role-assignment set version).")
            .WithDescription("Adding a role to yourself answers 403 self-protection; removing one of your own roles needs confirmSelfRemoval "
                + "(409 confirmation-required otherwise); removing the last Workspace Admin assignment answers 409 last-administrator; "
                + "assigning Break-glass needs Installation.AssignBreakGlass. The user must have signed in once.")
            .RequirePermission(Permission.WorkspaceManageUsers);

        ws.MapPut(AssignmentsPath + "/groups", PutGroupAsync)
            .WithName("PutGroupRoleAssignments")
            .WithTags(Tag)
            .Produces<RoleAssignmentChangeResource>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithSummary("Replace the roles an IdP group (?name=) holds in the workspace (If-Match: the role-assignment set version).")
            .WithDescription("A group you belong to applies to you: the self-protection and confirmation rules of the user route apply. "
                + "Break-glass is never assigned to a group.")
            .RequirePermission(Permission.WorkspaceManageUsers);

        ws.MapGet(AssignmentsPath + "/candidates", ListCandidatesAsync)
            .WithName("ListRoleAssignmentCandidates")
            .WithTags(Tag)
            .Produces<RoleAssignmentCandidateList>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Users (after their first sign-in) and known IdP groups whose name contains q, to assign roles to.")
            .RequirePermission(Permission.WorkspaceManageUsers);
    }

    internal static IResult ListRoles(string workspaceId)
    {
        _ = workspaceId;
        return TypedResults.Ok(Catalog);
    }

    internal static async Task<IResult> ListAssignmentsAsync(
        string workspaceId, HttpContext context, RoleAssignmentService service, IOptionsMonitor<InstallationAuthorizationOptions> installation,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (context.GetWorkspaceAccess() is not { } access
            || await service.ReadAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false) is not { } set)
        {
            return Problems.NotFound();
        }

        var items = set.Assignments
            .GroupBy(a => (a.UserId, a.GroupName))
            .Select(g => ToResource(g.Key.UserId is { } u ? RolePrincipal.User(u) : RolePrincipal.Group(g.Key.GroupName!), set, access.Principal))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Kind)
            .ThenBy(p => p.UserId)
            .ToList();
        context.Response.Headers.ETag = EntityTags.ForVersion(set.Version);
        return TypedResults.Ok(new RoleAssignmentListResource(items, set.Version, set.AdministratorPaths,
            InstallationPermissions.IsInstallationAdmin(context.User, installation.CurrentValue)));
    }

    internal static Task<IResult> PutUserAsync(
        string workspaceId, string userId, RoleAssignmentRequest request, HttpContext context, RoleAssignmentService service,
        IOptionsMonitor<InstallationAuthorizationOptions> installation, AspNetAuthorization aspNetAuthorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        return Guid.TryParse(userId, out var id)
            ? ReplaceAsync(RolePrincipal.User(id), request, context, service, installation, aspNetAuthorization, cancellationToken)
            : Task.FromResult<IResult>(Problems.NotFound());
    }

    internal static Task<IResult> PutGroupAsync(
        string workspaceId, [FromQuery(Name = "name")] string? name, RoleAssignmentRequest request, HttpContext context, RoleAssignmentService service,
        IOptionsMonitor<InstallationAuthorizationOptions> installation, AspNetAuthorization aspNetAuthorization, CancellationToken cancellationToken)
    {
        _ = workspaceId;
        return RoleAssignmentService.NormalizeGroupName(name) is { } group
            ? ReplaceAsync(RolePrincipal.Group(group), request, context, service, installation, aspNetAuthorization, cancellationToken)
            : Task.FromResult<IResult>(Problems.Validation(new Dictionary<string, string[]>
            {
                ["name"] = [$"The IdP group name: 1 to {RoleAssignmentService.MaxGroupNameLength} characters without control characters."],
            }));
    }

    internal static async Task<IResult> ListCandidatesAsync(
        string workspaceId, [FromQuery(Name = "q")] string? q, [FromQuery(Name = "limit")] int? limit, RoleAssignmentService service,
        CancellationToken cancellationToken)
    {
        _ = workspaceId;
        if (q is { Length: > 200 })
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["q"] = ["At most 200 characters."] });
        }

        var found = await service.FindCandidatesAsync(q, limit ?? 20, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new RoleAssignmentCandidateList([.. found.Select(c => new RoleAssignmentCandidateResource(
            c.UserId is null ? RolePrincipalKind.Group : RolePrincipalKind.User, c.UserId, c.GroupName, c.DisplayName, c.Email))]));
    }

    private static async Task<IResult> ReplaceAsync(
        RolePrincipal principal, RoleAssignmentRequest? request, HttpContext context, RoleAssignmentService service,
        IOptionsMonitor<InstallationAuthorizationOptions> installation, AspNetAuthorization aspNetAuthorization, CancellationToken cancellationToken)
    {
        if (context.GetWorkspaceAccess() is not { } access
            || await service.ReadAsync(access.WorkspaceId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return Problems.NotFound();
        }

        if (EntityTags.CheckIfMatch(context.Request, current.Version) is { } precondition)
        {
            return precondition;
        }

        var canAssignBreakGlass = InstallationPermissions.IsInstallationAdmin(context.User, installation.CurrentValue);
        var outcome = await service.ReplaceAsync(access.Principal, access.WorkspaceId, principal, request?.Roles, request?.ConfirmSelfRemoval ?? false,
            current.Version, canAssignBreakGlass, cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case RoleAssignmentStatus.Ok when outcome.Set is { } saved:
                context.Response.Headers.ETag = EntityTags.ForVersion(saved.Version);
                return TypedResults.Ok(new RoleAssignmentChangeResource(ToResource(principal, saved, access.Principal), saved.Version, saved.AdministratorPaths));
            case RoleAssignmentStatus.Invalid:
                return Problems.Validation(outcome.Errors.ToDictionary());
            case RoleAssignmentStatus.VersionConflict:
                return Problems.Create(StatusCodes.Status412PreconditionFailed, ProblemCodes.VersionConflict,
                    "The role assignments were changed since they were read.");
            case RoleAssignmentStatus.SelfProtection:
                return Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.SelfProtection,
                    "You cannot add roles to yourself or to a group you belong to; another administrator must do it.");
            case RoleAssignmentStatus.ConfirmationRequired:
                return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.ConfirmationRequired,
                    "This change removes a role from you. Confirm it with confirmSelfRemoval.");
            case RoleAssignmentStatus.LastAdministrator:
                return Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.LastAdministrator,
                    "The workspace needs at least one Workspace Admin. Assign the role to someone else first.");
            case RoleAssignmentStatus.BreakGlassNeedsInstallationAdmin:
                // The policy check writes the AuthZ.Denied audit event of the refused installation permission.
                await aspNetAuthorization.AuthorizeAsync(context.User, context,
                    InstallationAuthorizationConventions.PolicyName(InstallationPermissions.AssignBreakGlass)).ConfigureAwait(false);
                return Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden,
                    "Only an Installation Admin can assign the Break-glass role.");
            default:
                return Problems.NotFound();
        }
    }

    private static RoleAssignmentPrincipalResource ToResource(RolePrincipal principal, RoleAssignmentSet set, Application.Authorization.SecurityPrincipal caller)
    {
        var held = set.Assignments.Where(a => principal.Matches(a.UserId, a.GroupName)).ToList();
        var roles = held.Select(a => a.Role).Distinct().Order().Select(r => r.Key()).ToList();
        var first = held.FirstOrDefault();
        var displayName = principal.GroupName ?? first?.DisplayName ?? first?.Email ?? principal.UserId!.Value.ToString();
        return new RoleAssignmentPrincipalResource(
            principal.IsGroup ? RolePrincipalKind.Group : RolePrincipalKind.User,
            principal.UserId,
            principal.GroupName,
            displayName,
            principal.IsGroup ? null : first?.Email,
            roles,
            RoleAssignmentService.AppliesTo(principal, caller));
    }
}

public static class RoleAssignmentEndpointRegistration
{
    /// <summary>The role administration endpoints and use case (the store comes with AddPostgresWorkspaceStore).</summary>
    public static IServiceCollection AddRoleAssignmentEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<RoleAssignmentService>();
        services.AddSingleton<IApiEndpointModule, RoleAssignmentEndpoints>();
        return services;
    }
}
