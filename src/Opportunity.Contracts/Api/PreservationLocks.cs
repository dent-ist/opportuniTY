namespace Opportunity.Contracts.Api;

/// <summary>
/// A workspace preservation lock (legal hold; E20-T01, ADR-014 §2) of <c>…/preservation-locks</c>. While any lock is
/// active, every delete or purge of the workspace's preserved records answers <c>423 Locked</c> (problem code
/// <c>preservation-locked</c>). The lock's version is its ETag and the If-Match of every release step.
/// </summary>
/// <param name="Status">active, releasePending (still active, waiting for a second person) or released.</param>
/// <param name="ReleaseRequiresApproval">Release needs a request and the approval of a different hold manager.</param>
public sealed record PreservationLockResource(
    Guid LockId,
    PreservationLockStatusResource Status,
    PreservationLockScope Scope,
    string Reason,
    string? MatterReference,
    bool ReleaseRequiresApproval,
    PreservationLockActor PlacedBy,
    DateTimeOffset PlacedAt,
    PreservationLockActor? ReleaseRequestedBy,
    DateTimeOffset? ReleaseRequestedAt,
    string? ReleaseReason,
    PreservationLockActor? ReleaseApprovedBy,
    DateTimeOffset? ReleasedAt,
    long Version);

/// <summary>A person who acted on a lock: the user id and the name they had at their last sign-in.</summary>
public sealed record PreservationLockActor(Guid UserId, string? DisplayName);

public enum PreservationLockStatusResource
{
    Active,
    ReleasePending,
    Released,
}

/// <summary>What a lock covers. Only the whole workspace in this version (a document-set scope is reserved by ADR-014).</summary>
public enum PreservationLockScope
{
    Workspace,
}

/// <summary><c>GET …/preservation-locks</c>: every lock of the workspace, active ones first, newest first.</summary>
public sealed record PreservationLockList(IReadOnlyList<PreservationLockResource> Items, int ActiveCount);

/// <summary>Body of <c>POST …/preservation-locks</c>. Text is trimmed.</summary>
/// <param name="Reason">Required, 1 to 2,000 characters: why the data must be preserved.</param>
/// <param name="MatterReference">Optional, at most 200 characters on one line: matter, order or notice reference.</param>
/// <param name="ReleaseRequiresApproval">Default true: releasing needs a second person (Q-23).</param>
public sealed record PreservationLockWrite(string? Reason, string? MatterReference = null, bool? ReleaseRequiresApproval = null);

/// <summary>Body of <c>POST …/preservation-locks/{lockId}/release</c>.</summary>
/// <param name="Reason">Required, 1 to 2,000 characters: why the data no longer needs to be preserved.</param>
public sealed record PreservationLockReleaseWrite(string? Reason);
