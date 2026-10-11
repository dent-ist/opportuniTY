namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET …/compliance/wall-proof</c> (E14-T06): every audited access attempt by a walled user on a document their wall
/// covers in the period, plus every attempt a wall refused. <see cref="Held"/> is true when none was allowed on the
/// normal path (break-glass access is listed separately). Walls are evaluated as they are now, from the time each member
/// was added; <see cref="WallChanges"/> lists the changes made in the period. Documents are named by id only.
/// </summary>
/// <param name="From">Start of the period (inclusive, UTC).</param>
/// <param name="To">End of the period (exclusive, UTC).</param>
/// <param name="Truncated">More attempts exist than are listed (the counts include all; breaches are listed first).</param>
public sealed record WallProofResource(
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    bool Held,
    WallProofCountsResource Counts,
    IReadOnlyList<WallProofWallResource> Walls,
    IReadOnlyList<WallProofAttemptResource> Attempts,
    bool Truncated,
    IReadOnlyList<WallProofChangeResource> WallChanges);

public sealed record WallProofCountsResource(long Attempts, long Denied, long BreakGlass, long Allowed, long Failed);

/// <param name="DocumentsCovered">Documents the wall covers now.</param>
public sealed record WallProofWallResource(
    Guid WallId, string Name, DateTimeOffset CreatedAt, IReadOnlyList<WallProofMemberResource> Members, long DocumentsCovered);

/// <summary>A user (<see cref="UserId"/>) or IdP group (<see cref="Group"/>) behind the wall, since <see cref="AddedAt"/>.</summary>
public sealed record WallProofMemberResource(Guid? UserId, string? Group, string? DisplayName, DateTimeOffset AddedAt);

/// <param name="Result">Denied | BreakGlass | Allowed | Failed. Allowed means the wall did not hold.</param>
/// <param name="Action">The audited action, <c>Category.Action</c>.</param>
public sealed record WallProofAttemptResource(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string Result,
    Guid UserId,
    string UserDisplay,
    Guid DocumentId,
    IReadOnlyList<Guid> WallIds,
    string Action,
    string Outcome,
    string? ReasonCode,
    string AccessPath,
    string CorrelationId);

/// <param name="Action">WallCreated | WallChanged | WallDeleted | WallMemberAdded | WallMemberRemoved.</param>
public sealed record WallProofChangeResource(Guid EventId, DateTimeOffset OccurredAt, string Action, string ActorDisplay, string? WallId);

/// <summary>
/// <c>GET …/compliance/access-review</c> (E14-T06, SEC-13): every user with a role in the workspace (directly or through an
/// IdP group the user last signed in with) or behind an ethical wall, and every such group, with their roles, walls,
/// last workspace access (newest successful audited action) and last sign-in.
/// </summary>
public sealed record AccessReviewResource(DateTimeOffset GeneratedAt, int Users, int Groups, IReadOnlyList<AccessReviewEntryResource> Entries);

/// <param name="Principal">User | Group.</param>
/// <param name="KnownMembers">Groups only: users the IdP last reported in the group.</param>
public sealed record AccessReviewEntryResource(
    string Principal,
    Guid? UserId,
    string? Group,
    string? DisplayName,
    string? Email,
    IReadOnlyList<AccessReviewRoleResource> Roles,
    IReadOnlyList<AccessReviewWallResource> Walls,
    DateTimeOffset? LastAccessAt,
    DateTimeOffset? LastSignInAt,
    int? KnownMembers);

/// <param name="Role">The role key, e.g. <c>Reviewer</c>.</param>
/// <param name="ViaGroup">The IdP group the role is held through; null when assigned directly.</param>
public sealed record AccessReviewRoleResource(string Role, string DisplayName, string? ViaGroup, DateTimeOffset AssignedAt);

public sealed record AccessReviewWallResource(Guid WallId, string Name, string? ViaGroup);
