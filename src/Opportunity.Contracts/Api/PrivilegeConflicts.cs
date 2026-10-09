namespace Opportunity.Contracts.Api;

/// <summary>Which relationship a privilege conflict is about.</summary>
public enum PrivilegeConflictKindResource
{
    Family,
    Duplicates,
}

/// <summary>Why a group is in conflict.</summary>
public enum PrivilegeConflictReasonResource
{
    /// <summary>A family member is coded Privilege Status = Withhold and another is not (Q-14).</summary>
    WithheldMember,

    /// <summary>Family members carry different calls of the chosen responsiveness field.</summary>
    ResponsivenessDiffers,

    /// <summary>Duplicates carry different privilege calls, at least one other than Not Privileged.</summary>
    PrivilegeCallsDiffer,
}

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/privilege-conflicts</c> (E13-T02): families and duplicate groups with
/// inconsistent privilege (or responsiveness) calls, computed on request. Only documents the caller may see are listed
/// or counted, and a group appears only when those documents alone are in conflict (Q-52).
/// </summary>
/// <param name="FamilyConflictCount">Family groups in <paramref name="Groups"/>.</param>
/// <param name="DuplicateConflictCount">Duplicate groups in <paramref name="Groups"/>.</param>
/// <param name="Truncated">More than 1,000 groups of a kind are in conflict; resolve these and run the report again.</param>
/// <param name="Groups">Families first, then duplicate groups, each by its first control number.</param>
public sealed record PrivilegeConflictReportResource(
    DateTimeOffset GeneratedAt,
    int? ResponsivenessFieldId,
    Guid? ProductionId,
    int FamilyConflictCount,
    int DuplicateConflictCount,
    bool Truncated,
    IReadOnlyList<PrivilegeConflictGroupResource> Groups);

/// <param name="GroupId">The family id or the duplicate group id.</param>
/// <param name="Members">Visible members: a family in family order, a duplicate group with its primary first.</param>
public sealed record PrivilegeConflictGroupResource(
    PrivilegeConflictKindResource Kind,
    Guid GroupId,
    IReadOnlyList<PrivilegeConflictReasonResource> Reasons,
    IReadOnlyList<PrivilegeConflictMemberResource> Members);

/// <param name="FamilySequence">0 for a family's parent, 1… for its attachments.</param>
/// <param name="IsPrimary">The primary of its duplicate group.</param>
/// <param name="InProduction">A member of the report's production (false when the report is not scoped to one).</param>
/// <param name="Responsiveness">Null unless the report was asked for a responsiveness field.</param>
public sealed record PrivilegeConflictMemberResource(
    Guid DocumentId,
    string ControlNumber,
    int FamilySequence,
    bool IsPrimary,
    bool InProduction,
    PrivilegeCodedValueResource PrivilegeStatus,
    PrivilegeCodedValueResource PrivilegeBasis,
    PrivilegeCodedValueResource? Responsiveness);

/// <summary>A field's current value on a document and who last changed it.</summary>
/// <param name="Values">Choice names in choice order; empty when there is no value.</param>
/// <param name="ChoiceIds">The choices' ids.</param>
/// <param name="ChangedBy">The reviewer who last set or cleared the value; null when never coded.</param>
public sealed record PrivilegeCodedValueResource(
    IReadOnlyList<string> Values, IReadOnlyList<int> ChoiceIds, ReviewerResource? ChangedBy, DateTimeOffset? ChangedAt);

/// <param name="DisplayName">The user's display name (the user id when the identity provider gave none).</param>
public sealed record ReviewerResource(Guid UserId, string DisplayName);

/// <summary>
/// Body of <c>POST …/privilege-conflicts/propagations</c> (Idempotency-Key required): "propagate privilege call to
/// duplicates" as one bulk coding job.
/// </summary>
/// <param name="Groups">1–1,000 duplicate groups, each with the member whose privilege call the others get.</param>
/// <param name="Fields">Privilege fields to copy (ids 37–41); omitted or empty: Privilege Status and Privilege Basis.</param>
public sealed record PrivilegeConflictPropagationRequest(IReadOnlyList<PrivilegeConflictPropagationGroup> Groups, IReadOnlyList<int>? Fields = null);

public sealed record PrivilegeConflictPropagationGroup(Guid DuplicateGroupId, Guid SourceDocumentId);
