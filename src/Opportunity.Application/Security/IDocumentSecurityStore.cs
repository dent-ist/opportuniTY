using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Contracts.Api;
using Opportunity.Core.Security;

namespace Opportunity.Application.Security;

/// <summary>A restriction class to store: display name, the roles that may see it and the choices that apply it.</summary>
public sealed record RestrictionClassDefinition(string DisplayName, IReadOnlyList<WorkspaceRole> Roles, IReadOnlyList<SecurityChoiceRef> Rules);

public sealed record RestrictionClassState(
    string ClassKey, bool IsBuiltIn, RestrictionClassDefinition Definition, DateTimeOffset UpdatedAt, long Version);

/// <summary>An ethical wall to store (Q-13, ADR-015 D6.2). Custodians are normalized (trimmed, lower case).</summary>
public sealed record EthicalWallDefinition(
    string Name,
    string? Description,
    IReadOnlyList<Guid> UserIds,
    IReadOnlyList<string> Groups,
    IReadOnlyList<Guid> DocumentIds,
    IReadOnlyList<string> Custodians,
    IReadOnlyList<SecurityChoiceRef> Choices);

public sealed record EthicalWallState(Guid WallId, EthicalWallDefinition Definition, DateTimeOffset UpdatedAt, long Version);

/// <summary>A field-level restriction: hidden without a role in <see cref="VisibleTo"/>, read-only without one in <see cref="EditableBy"/>.</summary>
public sealed record FieldRestrictionState(int FieldId, IReadOnlyList<WorkspaceRole> VisibleTo, IReadOnlyList<WorkspaceRole> EditableBy,
    DateTimeOffset UpdatedAt, long Version);

/// <summary>A break-glass activation (V0012, Q-45).</summary>
public sealed record BreakGlassActivationState(
    Guid ActivationId, Guid UserId, string? UserDisplayName, string Reason, DateTimeOffset ActivatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt, string? EndedReason);

/// <summary>The roles a principal holds in a workspace and the workspace's field restrictions (one read).</summary>
public sealed record FieldAccessState(IReadOnlySet<WorkspaceRole> Roles, IReadOnlyList<FieldRestrictionState> Restrictions);

public enum SecurityWriteStatus
{
    Ok,
    Created,
    NotFound,
    VersionConflict,
    NameTaken,

    /// <summary>Referenced documents do not exist (or are deleted); see <see cref="SecurityWrite{T}.UnknownDocuments"/>.</summary>
    UnknownDocuments,

    /// <summary>The caller already has an open break-glass activation.</summary>
    AlreadyActive,
}

/// <param name="DocumentsChanged">Documents whose restriction classes or wall coverage changed (re-projected on the security lane).</param>
public sealed record SecurityWrite<T>(SecurityWriteStatus Status, T? Value = default, int DocumentsChanged = 0)
    where T : class
{
    public IReadOnlyList<Guid> UnknownDocuments { get; init; } = [];
}

/// <summary>
/// PostgreSQL state of document- and field-level security administration (E05-T06; V0012, V0044). Every write runs in
/// one workspace transaction with its audit event and, when document visibility changes, the documents' restriction
/// classes or wall coverage, their projection versions and their security-lane search work (§24 rule 1: the PDP enforces
/// the change from the commit on; search follows on the priority lane, Q-10).
/// </summary>
public interface IDocumentSecurityStore
{
    Task<IReadOnlyList<RestrictionClassState>> ListClassesAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Creates (<paramref name="expectedVersion"/> null) or replaces a class and re-derives which documents carry it.</summary>
    Task<SecurityWrite<RestrictionClassState>> PutClassAsync(
        Guid workspaceId, string classKey, RestrictionClassDefinition definition, long? expectedVersion, Guid actorId, AuditEvent audit,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a workspace-defined class; its documents lose it.</summary>
    Task<SecurityWrite<RestrictionClassState>> DeleteClassAsync(
        Guid workspaceId, string classKey, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EthicalWallState>> ListWallsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<EthicalWallState?> GetWallAsync(Guid workspaceId, Guid wallId, CancellationToken cancellationToken = default);

    Task<SecurityWrite<EthicalWallState>> CreateWallAsync(
        Guid workspaceId, Guid wallId, EthicalWallDefinition definition, Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<SecurityWrite<EthicalWallState>> UpdateWallAsync(
        Guid workspaceId, Guid wallId, EthicalWallDefinition definition, long expectedVersion, Guid actorId, AuditEvent audit,
        CancellationToken cancellationToken = default);

    Task<SecurityWrite<EthicalWallState>> DeleteWallAsync(
        Guid workspaceId, Guid wallId, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FieldRestrictionState>> ListFieldRestrictionsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<SecurityWrite<FieldRestrictionState>> PutFieldRestrictionAsync(
        Guid workspaceId, int fieldId, IReadOnlyList<WorkspaceRole> visibleTo, IReadOnlyList<WorkspaceRole> editableBy, long? expectedVersion,
        Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<SecurityWrite<FieldRestrictionState>> DeleteFieldRestrictionAsync(
        Guid workspaceId, int fieldId, long expectedVersion, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>The principal's roles (directly or through groups) and the field restrictions, in one read.</summary>
    Task<FieldAccessState> ReadFieldAccessAsync(Guid workspaceId, SecurityPrincipal principal, CancellationToken cancellationToken = default);

    /// <summary>Opens an activation unless the user has one open (<see cref="SecurityWriteStatus.AlreadyActive"/>).</summary>
    Task<SecurityWrite<BreakGlassActivationState>> ActivateBreakGlassAsync(
        Guid workspaceId, Guid activationId, Guid userId, string reason, TimeSpan duration, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Ends an open activation (<paramref name="endedReason"/> <c>Ended</c> or <c>Revoked</c>).</summary>
    Task<SecurityWrite<BreakGlassActivationState>> EndBreakGlassAsync(
        Guid workspaceId, Guid activationId, string endedReason, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<BreakGlassActivationState?> GetBreakGlassAsync(Guid workspaceId, Guid activationId, CancellationToken cancellationToken = default);

    /// <summary>Activations newest first, all users or one (<paramref name="userId"/>), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<BreakGlassActivationState>> ListBreakGlassAsync(
        Guid workspaceId, Guid? userId, int limit, CancellationToken cancellationToken = default);
}
