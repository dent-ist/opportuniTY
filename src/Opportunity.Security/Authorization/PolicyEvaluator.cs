using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;

namespace Opportunity.Security.Authorization;

/// <summary>
/// The ADR-015 D5.2 evaluation, as a pure function of PostgreSQL state. First failing step decides; default deny.
/// <list type="number">
/// <item>Workspace exists and is not being deleted, and the principal holds a role in it (directly or through a group;
/// break-glass only while activated): otherwise NotFound.</item>
/// <item>The permission is in the union of the held roles' grants: otherwise Deny.</item>
/// <item>Documents: it exists, every restriction class on it is granted to a held role, and no wall naming the
/// principal covers it: otherwise NotFound. A wall overrides every grant, Workspace Admin included.</item>
/// <item>Step 3's class and wall rules are lifted only by an active break-glass activation, and only for the
/// break-glass-eligible read permissions (D6.4, Q-45).</item>
/// </list>
/// </summary>
public static class PolicyEvaluator
{
    public static bool BreakGlassActive(PrincipalSecurityState state, DateTimeOffset now) =>
        state.BreakGlassExpiresAt is { } expires && expires > now && state.Roles.Contains(WorkspaceRole.BreakGlass);

    /// <summary>Steps 1–2 (membership and permission). <paramref name="permission"/> null checks membership only.</summary>
    public static AuthorizationDecision EvaluateWorkspace(PrincipalSecurityState? state, Permission? permission, DateTimeOffset now)
    {
        if (state is null || state.WorkspaceStatus is WorkspaceStatus.Deleting or WorkspaceStatus.Purged)
        {
            return AuthorizationDecision.NotFound(AuthorizationReasons.WorkspaceNotFound);
        }

        var breakGlass = BreakGlassActive(state, now);
        var member = state.Roles.Any(r => r != WorkspaceRole.BreakGlass) || breakGlass;
        if (!member)
        {
            return AuthorizationDecision.NotFound(AuthorizationReasons.NotAMember);
        }

        if (permission is not { } p)
        {
            return AuthorizationDecision.Allow();
        }

        if (state.Roles.Any(r => r != WorkspaceRole.BreakGlass && r.Grants(p)))
        {
            return AuthorizationDecision.Allow();
        }

        return breakGlass && WorkspaceRole.BreakGlass.Grants(p)
            ? AuthorizationDecision.Allow(breakGlass: true)
            : AuthorizationDecision.Deny(AuthorizationReasons.PermissionNotGranted);
    }

    /// <summary>All steps for one document; <paramref name="document"/> null means it does not exist.</summary>
    public static AuthorizationDecision EvaluateDocument(
        PrincipalSecurityState? state, Permission permission, DocumentSecurityAttributes? document, DateTimeOffset now)
    {
        var workspace = EvaluateWorkspace(state, permission, now);
        if (!workspace.IsAllowed)
        {
            return workspace;
        }

        if (document is null)
        {
            return AuthorizationDecision.NotFound(AuthorizationReasons.DocumentNotFound);
        }

        var hidden = HiddenBy(state!, document);
        if (hidden is null)
        {
            return workspace;
        }

        return BreakGlassActive(state!, now) && PermissionCatalog.Get(permission).BreakGlassEligible
            ? AuthorizationDecision.Allow(breakGlass: true)
            : AuthorizationDecision.NotFound(hidden);
    }

    /// <summary>The principal-side filter for search and lists (D5.1, D8.1).</summary>
    public static VisibilityFilter Visibility(PrincipalSecurityState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (BreakGlassActive(state, now))
        {
            return new VisibilityFilter([], [], BreakGlass: true);
        }

        var denied = state.ClassGrants
            .Where(g => !IsVisible(state, g.Key))
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new VisibilityFilter(denied, [.. state.WallIds.Order()], BreakGlass: false);
    }

    private static string? HiddenBy(PrincipalSecurityState state, DocumentSecurityAttributes document)
    {
        foreach (var classKey in document.RestrictionClasses)
        {
            if (!IsVisible(state, classKey))
            {
                return AuthorizationReasons.RestrictionClass;
            }
        }

        foreach (var wall in document.WallIds)
        {
            if (state.WallIds.Contains(wall))
            {
                return AuthorizationReasons.EthicalWall;
            }
        }

        return null;
    }

    // A class without grants (or unknown to the workspace) is visible to nobody: default deny.
    private static bool IsVisible(PrincipalSecurityState state, string classKey) =>
        state.ClassGrants.TryGetValue(classKey, out var roles)
        && state.Roles.Any(r => r != WorkspaceRole.BreakGlass && roles.Contains(r));
}
