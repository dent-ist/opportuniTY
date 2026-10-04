using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;

namespace Opportunity.Security.Authorization;

/// <summary>
/// Installation-level permissions of the Installation Admin role (ADR-015 D5.7). They grant no document access: an
/// installation admin who needs content must hold a workspace role. Only the ones with an endpoint are listed.
/// </summary>
public static class InstallationPermissions
{
    /// <summary>Create workspaces (E04-T05); deletion approval is <c>Installation.ApproveDeletion</c> (E20).</summary>
    public const string ManageWorkspaces = "Installation.ManageWorkspaces";

    /// <summary>All installation permissions of the Installation Admin role.</summary>
    public static IReadOnlyList<string> All { get; } = [ManageWorkspaces];

    /// <summary>True when <paramref name="user"/> holds the Installation Admin role (a member of one of the configured groups).</summary>
    public static bool IsInstallationAdmin(ClaimsPrincipal user, InstallationAuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(options);
        return user.Identity?.IsAuthenticated == true
            && user.FindAll(Authentication.OpportunityClaimTypes.Group)
                .Any(g => g.Value.Length > 0 && options.InstallationAdminGroups.Contains(g.Value, StringComparer.Ordinal));
    }

    /// <summary>The installation permissions <paramref name="user"/> holds, for display (<c>GET /api/v1/me</c>).</summary>
    public static IReadOnlyList<string> Granted(ClaimsPrincipal user, InstallationAuthorizationOptions options) =>
        IsInstallationAdmin(user, options) ? All : [];
}

/// <summary>
/// Configuration section <c>Authorization</c>. Until installation role assignments get their own store and API, the
/// Installation Admin role is held by members of these IdP groups (ADR-015 D3.4: groups come from the IdP snapshot of
/// the session). Empty means nobody holds it (default deny).
/// </summary>
public sealed class InstallationAuthorizationOptions
{
    public const string SectionName = "Authorization";

    public IList<string> InstallationAdminGroups { get; } = [];
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

        if (InstallationPermissions.IsInstallationAdmin(context.User, options.CurrentValue))
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
