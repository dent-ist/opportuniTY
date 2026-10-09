using Opportunity.Application.Jobs;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Coding;

/// <summary>Which relationship a privilege conflict is about (E13-T02).</summary>
public enum PrivilegeConflictKind : short
{
    /// <summary>The members of one family (<c>family_id</c>).</summary>
    Family = 1,

    /// <summary>The members of one duplicate group (<c>duplicate_group_id</c>).</summary>
    Duplicates = 2,
}

/// <summary>Why a group is in conflict; a family can have both family reasons.</summary>
[Flags]
public enum PrivilegeConflictReasons
{
    None = 0,

    /// <summary>
    /// Family: a member is coded Privilege Status = Withhold and another is not, so producing the family leaves it
    /// incomplete or misleading (Q-14: a withheld parent with produced attachments, or the reverse).
    /// </summary>
    WithheldMember = 1,

    /// <summary>Family: members carry different calls of the chosen responsiveness field.</summary>
    ResponsivenessDiffers = 2,

    /// <summary>Duplicates: the members' Privilege Status differs and at least one has a call other than Not Privileged.</summary>
    PrivilegeCallsDiffer = 4,
}

/// <summary>Candidate groups of a privilege conflict report, read set-based from the coding store.</summary>
/// <param name="ResponsivenessFieldId">A single-choice coding field whose differing calls inside a family are reported; null: none.</param>
/// <param name="ProductionId">Only groups with a member in this production's frozen members; null: the whole workspace.</param>
/// <param name="MaxGroups">At most this many groups per kind; one more tells that the list was truncated.</param>
public sealed record PrivilegeConflictQuery(Guid WorkspaceId, int? ResponsivenessFieldId = null, Guid? ProductionId = null, int MaxGroups = 1_000);

/// <summary>A coding field's current value on one document and who last changed it (null when never coded).</summary>
public sealed record PrivilegeCodedValue(IReadOnlyList<int> ChoiceIds, Guid? ChangedBy, DateTimeOffset? ChangedAt)
{
    public static PrivilegeCodedValue Empty { get; } = new([], null, null);

    public int? SingleChoiceId => ChoiceIds.Count > 0 ? ChoiceIds[0] : null;
}

/// <summary>One live member of a candidate group with its privilege (and optionally responsiveness) coding.</summary>
/// <param name="InProduction">The document is a member of the query's production (always false without one).</param>
public sealed record PrivilegeConflictMember(
    Guid DocumentId,
    string ControlNumber,
    int FamilySequence,
    bool IsDuplicatePrimary,
    bool InProduction,
    PrivilegeCodedValue Status,
    PrivilegeCodedValue Basis,
    PrivilegeCodedValue Responsiveness);

/// <summary>A family or duplicate group whose members (all of them, unfiltered) are in conflict.</summary>
public sealed record PrivilegeConflictCandidate(PrivilegeConflictKind Kind, Guid GroupId, IReadOnlyList<PrivilegeConflictMember> Members);

/// <param name="Truncated">More than <see cref="PrivilegeConflictQuery.MaxGroups"/> groups of a kind are in conflict.</param>
public sealed record PrivilegeConflictCandidates(IReadOnlyList<PrivilegeConflictCandidate> Groups, bool Truncated);

/// <summary>
/// The conflict rules (E13-T02), applied by the store to every member and again by the service to the members a caller
/// may see (Q-52: a conflict that needs a hidden member to exist is not a conflict for that caller).
/// </summary>
public static class PrivilegeConflictRules
{
    /// <param name="withholdChoiceId">The Privilege Status choice with key <c>privilege-status.withhold</c>.</param>
    /// <param name="notPrivilegedChoiceId">The Privilege Status choice with key <c>privilege-status.not-privileged</c>.</param>
    public static PrivilegeConflictReasons Evaluate(
        PrivilegeConflictKind kind, IReadOnlyCollection<PrivilegeConflictMember> members, int? withholdChoiceId, int? notPrivilegedChoiceId,
        bool responsiveness)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count < 2)
        {
            return PrivilegeConflictReasons.None;
        }

        var reasons = PrivilegeConflictReasons.None;
        if (kind == PrivilegeConflictKind.Family)
        {
            if (withholdChoiceId is { } withhold
                && members.Any(m => m.Status.SingleChoiceId == withhold)
                && members.Any(m => m.Status.SingleChoiceId != withhold))
            {
                reasons |= PrivilegeConflictReasons.WithheldMember;
            }

            if (responsiveness && members.SelectMany(m => m.Responsiveness.ChoiceIds).Distinct().Count() > 1)
            {
                reasons |= PrivilegeConflictReasons.ResponsivenessDiffers;
            }
        }
        else if (members.Select(m => m.Status.SingleChoiceId ?? 0).Distinct().Count() > 1
                 && members.Any(m => m.Status.SingleChoiceId is { } id && id != notPrivilegedChoiceId))
        {
            reasons |= PrivilegeConflictReasons.PrivilegeCallsDiffer;
        }

        return reasons;
    }
}

/// <summary>A conflict group as one caller may see it: only visible members, still in conflict among themselves.</summary>
public sealed record PrivilegeConflictGroup(PrivilegeConflictKind Kind, Guid GroupId, PrivilegeConflictReasons Reasons, IReadOnlyList<PrivilegeConflictMember> Members);

/// <summary>The on-demand report (E13-T02 AC 1).</summary>
/// <param name="DisplayNames">Display names of the users who last changed the reported values.</param>
/// <param name="Truncated">The store found more conflicting groups than it reads per kind; resolve these and run again.</param>
public sealed record PrivilegeConflictReport(
    DateTimeOffset GeneratedAt,
    FieldCatalog Catalog,
    int? ResponsivenessFieldId,
    Guid? ProductionId,
    IReadOnlyList<PrivilegeConflictGroup> Groups,
    IReadOnlyDictionary<Guid, string> DisplayNames,
    bool Truncated);

public enum PrivilegeConflictStatus
{
    Ok,

    /// <summary>The production does not exist or is not visible: 404.</summary>
    NotFound,

    /// <summary>A permission is missing, or Privilege Status is hidden from the caller's roles: 403.</summary>
    Forbidden,

    Invalid,

    /// <summary>The propagation's Idempotency-Key created a different job before: 422.</summary>
    IdempotencyKeyReuse,
}

public sealed record PrivilegeConflictReportOutcome
{
    public required PrivilegeConflictStatus Status { get; init; }

    public PrivilegeConflictReport? Report { get; init; }

    public IReadOnlyList<FieldError> Errors { get; init; } = [];

    internal static PrivilegeConflictReportOutcome Of(PrivilegeConflictStatus status, params FieldError[] errors) => new() { Status = status, Errors = errors };
}

/// <summary>One duplicate group whose privilege call is copied from <paramref name="SourceDocumentId"/> to its other members.</summary>
public sealed record PrivilegePropagationGroup(Guid DuplicateGroupId, Guid SourceDocumentId);

/// <summary>"Propagate privilege call to duplicates" (E13-T02): the groups, the privilege fields to copy and the client's key.</summary>
/// <param name="FieldIds">Privilege fields (ids 37–41) to copy; empty: Privilege Status and Privilege Basis.</param>
public sealed record PrivilegePropagationRequest(IReadOnlyList<PrivilegePropagationGroup> Groups, IReadOnlyList<int> FieldIds, string IdempotencyKey);

public sealed record PrivilegePropagationOutcome
{
    public required PrivilegeConflictStatus Status { get; init; }

    public JobInfo? Job { get; init; }

    public IReadOnlyList<FieldError> Errors { get; init; } = [];

    internal static PrivilegePropagationOutcome Of(PrivilegeConflictStatus status, params FieldError[] errors) => new() { Status = status, Errors = errors };
}

/// <summary>
/// One group of a grouped propagation job: the duplicate group, its source document and, per copied field, the
/// source's value and the CodingEvent that set it (provenance, E09-T05).
/// </summary>
public sealed record GroupPropagationEntry(
    Guid DuplicateGroupId, Guid SourceDocumentId, IReadOnlyList<CodingFieldOperation> Operations, IReadOnlyDictionary<int, Guid> OriginEventIds);
