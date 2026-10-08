using System.Diagnostics;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Security;

namespace Opportunity.Application.Workspaces;

public enum RoleAssignmentStatus
{
    Ok,
    NotFound,
    Invalid,
    VersionConflict,

    /// <summary>ADR-015 D6.5: the caller would gain a role (directly or through one of their groups).</summary>
    SelfProtection,

    /// <summary>The caller would give up one of their own roles without having confirmed it.</summary>
    ConfirmationRequired,

    /// <summary>The change would remove the last Workspace Admin assignment.</summary>
    LastAdministrator,

    /// <summary>Assigning Break-glass needs the Installation Admin role (ADR-015 D6.4).</summary>
    BreakGlassNeedsInstallationAdmin,
}

public sealed record RoleAssignmentOutcome(RoleAssignmentStatus Status, RoleAssignmentSet? Set = null)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>
/// Role assignment administration (E05-T08; ADR-015 D5.6, D6.4, D6.5): replaces the built-in roles one user or IdP group
/// holds in a workspace. Grants of the roles themselves are fixed in code (<see cref="RoleCatalog"/>, custom roles are
/// post-MVP). Rules, in order: nobody adds a role to themselves, directly or through a group they belong to
/// (self-protection); giving up one's own role needs an explicit confirmation; Break-glass is assigned only to users, only
/// by an Installation Admin and never to oneself; and no change may remove the workspace's last Workspace Admin
/// assignment (checked again under the workspace lock by the store). Each change is audited in its own transaction and
/// applies to the very next request, because the PDP reads role assignments from PostgreSQL per request.
/// </summary>
public sealed class RoleAssignmentService(IRoleAssignmentStore store, TimeProvider time)
{
    public const int MaxGroupNameLength = 256;
    public const int MaxCandidates = 50;

    private const string CorrelationTag = "opportunity.correlation_id";

    public Task<RoleAssignmentSet?> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        store.ReadAsync(workspaceId, cancellationToken);

    public Task<IReadOnlyList<RoleAssignmentCandidate>> FindCandidatesAsync(string? contains, int limit, CancellationToken cancellationToken = default) =>
        store.FindCandidatesAsync(string.IsNullOrWhiteSpace(contains) ? null : contains.Trim(), Math.Clamp(limit, 1, MaxCandidates), cancellationToken);

    /// <summary>True when the assignment of <paramref name="principal"/> applies to <paramref name="caller"/>.</summary>
    public static bool AppliesTo(RolePrincipal principal, SecurityPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(caller);
        return principal.UserId is { } id
            ? id == caller.UserId
            : caller.Groups.Contains(principal.GroupName!, StringComparer.Ordinal);
    }

    /// <summary>Validates a group name from a request: trimmed, 1–256 characters, no control characters.</summary>
    public static string? NormalizeGroupName(string? name)
    {
        var trimmed = name?.Trim();
        return trimmed is { Length: > 0 and <= MaxGroupNameLength } && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    /// <summary>Replaces the roles <paramref name="principal"/> holds with <paramref name="roleKeys"/> (empty: removes them all).</summary>
    /// <param name="expectedVersion">The set version the caller read (If-Match).</param>
    /// <param name="canAssignBreakGlass">The caller holds <c>Installation.AssignBreakGlass</c>.</param>
    public async Task<RoleAssignmentOutcome> ReplaceAsync(
        SecurityPrincipal caller,
        Guid workspaceId,
        RolePrincipal principal,
        IReadOnlyList<string>? roleKeys,
        bool confirmSelfRemoval,
        long expectedVersion,
        bool canAssignBreakGlass,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(principal);
        if (roleKeys is null)
        {
            return Invalid("roles", "The roles are required; send an empty list to remove every role.");
        }

        var roles = new HashSet<WorkspaceRole>();
        foreach (var key in roleKeys)
        {
            if (!RoleCatalog.TryParse(key, out var role))
            {
                return Invalid("roles", $"'{key}' is not a role. Roles: {string.Join(", ", RoleCatalog.All.Select(r => r.Key))}.");
            }

            roles.Add(role);
        }

        if (principal.IsGroup && roles.Contains(WorkspaceRole.BreakGlass))
        {
            return Invalid("roles", "Break-glass is assigned to individual users only, never to a group.");
        }

        if (await store.ReadAsync(workspaceId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new(RoleAssignmentStatus.NotFound);
        }

        if (current.Version != expectedVersion)
        {
            return new(RoleAssignmentStatus.VersionConflict);
        }

        var before = current.RolesOf(principal);
        var added = roles.Except(before).ToHashSet();
        var removed = before.Except(roles).ToHashSet();
        if (added.Count == 0 && removed.Count == 0)
        {
            return new(RoleAssignmentStatus.Ok, current);
        }

        var self = AppliesTo(principal, caller);
        if (self && added.Count > 0)
        {
            return new(RoleAssignmentStatus.SelfProtection);
        }

        if (added.Contains(WorkspaceRole.BreakGlass) && !canAssignBreakGlass)
        {
            return new(RoleAssignmentStatus.BreakGlassNeedsInstallationAdmin);
        }

        if (removed.Contains(WorkspaceRole.WorkspaceAdmin) && current.AdministratorPaths <= 1)
        {
            return new(RoleAssignmentStatus.LastAdministrator);
        }

        if (self && !confirmSelfRemoval)
        {
            return new(RoleAssignmentStatus.ConfirmationRequired);
        }

        var audit = new AuditEvent
        {
            WorkspaceId = workspaceId,
            OccurredAt = time.GetUtcNow(),
            Category = AuditTaxonomy.Security.Category,
            Action = AuditTaxonomy.Security.RoleAssigned,
            ActorType = AuditActorType.User,
            ActorId = caller.UserId.ToString(),
            ActorDisplay = caller.DisplayName.Length > AuditEventRules.MaxActorDisplayLength
                ? caller.DisplayName[..AuditEventRules.MaxActorDisplayLength]
                : caller.DisplayName,
            ClientIp = caller.ClientIp,
            UserAgent = caller.UserAgent,
            ResourceType = AuditTaxonomy.Security.RoleAssignmentResourceType,
            Outcome = AuditOutcome.Success,
            CorrelationId = caller.CorrelationId ?? Activity.Current?.GetTagItem(CorrelationTag) as string,
            Details = self ? new Dictionary<string, string?> { ["selfRemoval"] = "true" } : new Dictionary<string, string?>(),
        };
        var write = await store.ReplaceAsync(workspaceId, principal, roles, expectedVersion, caller.UserId, audit, cancellationToken)
            .ConfigureAwait(false);
        return write.Status switch
        {
            RoleAssignmentWriteStatus.Ok => new(RoleAssignmentStatus.Ok, write.Set),
            RoleAssignmentWriteStatus.VersionConflict => new(RoleAssignmentStatus.VersionConflict),
            RoleAssignmentWriteStatus.LastAdministrator => new(RoleAssignmentStatus.LastAdministrator),
            RoleAssignmentWriteStatus.UnknownUser => Invalid("userId", "No user with this id has signed in to this installation yet."),
            _ => new(RoleAssignmentStatus.NotFound),
        };
    }

    private static RoleAssignmentOutcome Invalid(string key, string message) =>
        new(RoleAssignmentStatus.Invalid) { Errors = new Dictionary<string, string[]> { [key] = [message] } };
}
