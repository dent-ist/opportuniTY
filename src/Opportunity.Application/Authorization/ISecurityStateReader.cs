using Opportunity.Core.Security;
using Opportunity.Core.Workspaces;

namespace Opportunity.Application.Authorization;

/// <summary>
/// Reads authoritative security state from PostgreSQL (ADR-015 D1.1, D5.1); implemented by <c>Opportunity.Data</c>.
/// Every read runs inside the requested workspace's RLS context, so it can never see another workspace's rows.
/// </summary>
public interface ISecurityStateReader
{
    /// <summary>
    /// The principal-side state, and the security attributes of <paramref name="documentIds"/> when given, in one
    /// round trip. <see cref="SecurityStateRead.Principal"/> is null when the workspace does not exist.
    /// </summary>
    Task<SecurityStateRead> ReadAsync(
        Guid workspaceId,
        SecurityPrincipal principal,
        bool includePrincipal,
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken = default);
}

public sealed record SecurityStateRead(
    PrincipalSecurityState? Principal,
    IReadOnlyDictionary<Guid, DocumentSecurityAttributes> Documents);

/// <summary>
/// What applies to one principal in one workspace: roles held directly or through groups, class grants of the
/// workspace, walls naming the user or one of their groups, and the expiry of an active break-glass activation.
/// </summary>
public sealed record PrincipalSecurityState(
    WorkspaceStatus WorkspaceStatus,
    IReadOnlySet<WorkspaceRole> Roles,
    IReadOnlyDictionary<string, IReadOnlySet<WorkspaceRole>> ClassGrants,
    IReadOnlySet<Guid> WallIds,
    DateTimeOffset? BreakGlassExpiresAt);

/// <summary>A document's restriction classes and wall coverage. Absent from the read when it does not exist or is deleted.</summary>
public sealed record DocumentSecurityAttributes(IReadOnlyList<string> RestrictionClasses, IReadOnlyList<Guid> WallIds)
{
    public static DocumentSecurityAttributes Unrestricted { get; } = new([], []);
}
