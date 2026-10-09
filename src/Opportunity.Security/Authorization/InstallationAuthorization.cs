using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;

namespace Opportunity.Security.Authorization;

/// <summary>
/// Installation-level permissions (ADR-015 D5.7) of the Installation Admin role and of the Retention Approver role
/// (ADR-014 §3.2, Q-23). They grant no document access: an installation admin who needs content must hold a workspace
/// role. Only the ones with an endpoint are listed.
/// </summary>
public static class InstallationPermissions
{
    /// <summary>Create workspaces (E04-T05); deletion approval is <c>Installation.ApproveDeletion</c> (E20).</summary>
    public const string ManageWorkspaces = "Installation.ManageWorkspaces";

    /// <summary>Assign the Break-glass workspace role (ADR-015 D6.4; E05-T08), together with <c>Workspace.ManageUsers</c>.</summary>
    public const string AssignBreakGlass = "Installation.AssignBreakGlass";

    /// <summary>
    /// Approve (and cancel) workspace deletion requests (E20-T02). Held by the Retention Approver role, not by Installation
    /// Admin: ADR-014 §3.2 makes the approver a designated installation role outside every workspace role.
    /// </summary>
    public const string ApproveDeletion = "Installation.ApproveDeletion";

    /// <summary>All installation permissions of the Installation Admin role.</summary>
    public static IReadOnlyList<string> All { get; } = [ManageWorkspaces, AssignBreakGlass];

    /// <summary>True when <paramref name="user"/> holds the Installation Admin role (a member of one of the configured groups).</summary>
    public static bool IsInstallationAdmin(ClaimsPrincipal user, InstallationAuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(options);
        return user.Identity?.IsAuthenticated == true
            && user.FindAll(Authentication.OpportunityClaimTypes.Group)
                .Any(g => g.Value.Length > 0 && options.InstallationAdminGroups.Contains(g.Value, StringComparer.Ordinal));
    }

    /// <summary>True when <paramref name="user"/> holds the Retention Approver role (a member of one of its configured groups).</summary>
    public static bool IsRetentionApprover(ClaimsPrincipal user, InstallationAuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(options);
        return user.Identity?.IsAuthenticated == true
            && user.FindAll(Authentication.OpportunityClaimTypes.Group)
                .Any(g => g.Value.Length > 0 && options.RetentionApproverGroups.Contains(g.Value, StringComparer.Ordinal));
    }

    /// <summary>True when <paramref name="user"/> holds <paramref name="permission"/>.</summary>
    public static bool Holds(ClaimsPrincipal user, string permission, InstallationAuthorizationOptions options) =>
        permission == ApproveDeletion ? IsRetentionApprover(user, options) : All.Contains(permission) && IsInstallationAdmin(user, options);

    /// <summary>The installation permissions <paramref name="user"/> holds, for display (<c>GET /api/v1/me</c>).</summary>
    public static IReadOnlyList<string> Granted(ClaimsPrincipal user, InstallationAuthorizationOptions options) =>
        [.. IsInstallationAdmin(user, options) ? All : [], .. IsRetentionApprover(user, options) ? [ApproveDeletion] : Array.Empty<string>()];
}

/// <summary>
/// Configuration section <c>Authorization</c>. Until installation role assignments get their own store and API, the
/// Installation Admin and Retention Approver roles are held by members of these IdP groups (ADR-015 D3.4: groups come
/// from the IdP snapshot of the session). Empty means nobody holds the role (default deny).
/// </summary>
public sealed class InstallationAuthorizationOptions
{
    public const string SectionName = "Authorization";

    public IList<string> InstallationAdminGroups { get; } = [];

    /// <summary>Members approve workspace deletions (<c>Installation.ApproveDeletion</c>, ADR-014 §3.2).</summary>
    public IList<string> RetentionApproverGroups { get; } = [];
}

/// <summary>Requires an installation permission; declare with <see cref="InstallationAuthorizationConventions.RequireInstallationPermission{TBuilder}"/>.</summary>
public sealed class InstallationPermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public static class InstallationAuthorizationConventions
{
    public static string PolicyName(string permission) => "installation:" + permission;

    /// <summary>Requires <paramref name="permission"/> for the installation (403 otherwise, audited as <c>AuthZ.Denied</c>).</summary>
    public static TBuilder RequireInstallationPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.RequireAuthorization(PolicyName(permission));
    }
}

/// <summary>
/// Decides installation permissions from the principal's IdP groups. A denial of a signed-in user is written to the
/// system audit chain before the 403 (ADR-015 D5.3: every non-allow decision is audited).
/// </summary>
internal sealed class InstallationPermissionHandler(
    IOptionsMonitor<InstallationAuthorizationOptions> options, IAuditEventWriter audit, TimeProvider time)
    : AuthorizationHandler<InstallationPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, InstallationPermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (InstallationPermissions.Holds(context.User, requirement.Permission, options.CurrentValue))
        {
            context.Succeed(requirement);
            return;
        }

        var principal = context.Resource is HttpContext http
            ? http.ToSecurityPrincipal()
            : new SecurityPrincipal { UserId = Guid.Empty, DisplayName = context.User.Identity?.Name ?? "unknown" };
        await audit.WriteAsync(new AuditEvent
        {
            WorkspaceId = null,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.AuthZ.Category,
            Action = AuditTaxonomy.AuthZ.Denied,
            ActorType = AuditActorType.User,
            ActorId = principal.UserId == Guid.Empty ? "unknown" : principal.UserId.ToString(),
            ActorDisplay = principal.DisplayName,
            ClientIp = principal.ClientIp,
            UserAgent = principal.UserAgent,
            ResourceType = "Installation",
            ResourceId = "installation",
            Outcome = AuditOutcome.Denied,
            ReasonCode = AuthorizationReasons.PermissionNotGranted,
            CorrelationId = principal.CorrelationId,
            Details = new Dictionary<string, string?> { ["permission"] = requirement.Permission },
        }).ConfigureAwait(false);
        context.Fail(new AuthorizationFailureReason(this, AuthorizationReasons.PermissionNotGranted));
    }
}
