namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceId}/documents/{documentId}/relationships?fields=a,b</c> (E09-T05, wave-12
/// contract): the document's family, duplicate group and email thread as the caller may see them. Members the caller
/// may not see are never listed; restriction-class denials only raise <c>restrictedCount</c> (Q-52, Q-11). Documents
/// behind an ethical wall are not counted either (Q-13).
/// </summary>
public sealed record DocumentRelationshipsResource(
    Guid DocumentId,
    RelatedFamilyResource Family,
    RelatedDuplicatesResource Duplicates,
    RelatedThreadResource Thread);

/// <param name="FamilyId">Null when the document is a family of one.</param>
/// <param name="Parent">The family's top-level parent when the caller may see it.</param>
/// <param name="Members">Every visible member in family order (by familySequence), the document itself flagged <c>isSelf</c>.</param>
public sealed record RelatedFamilyResource(
    Guid? FamilyId, RelatedDocumentResource? Parent, IReadOnlyList<RelatedDocumentResource> Members, int RestrictedCount);

/// <param name="Members">Visible members, the primary first, then by control number.</param>
public sealed record RelatedDuplicatesResource(
    Guid? DuplicateGroupId, Guid? PrimaryDocumentId, IReadOnlyList<RelatedDocumentResource> Members, int RestrictedCount);

/// <param name="Members">The first 200 visible members by document date, then control number.</param>
/// <param name="Total">Visible members of the thread.</param>
public sealed record RelatedThreadResource(
    Guid? EmailThreadId, IReadOnlyList<RelatedDocumentResource> Members, int Total, int RestrictedCount);

/// <param name="IsParent">Top-level parent of a family with other visible members.</param>
/// <param name="IsPrimary">The primary of its duplicate group.</param>
/// <param name="IsSelf">The document the relationships were asked for.</param>
/// <param name="Coding">Current values of the requested <c>fields</c> by query name (choices by name); fields without a value are omitted.</param>
public sealed record RelatedDocumentResource(
    Guid DocumentId,
    string ControlNumber,
    string? FileName,
    DateTimeOffset? DocumentDate,
    int? FamilySequence,
    bool IsParent,
    bool IsPrimary,
    bool IsSelf,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Coding);

/// <summary>Which related documents a propagation reaches.</summary>
public enum CodingPropagationScopeResource
{
    Family,
    Duplicates,
    FamilyAndDuplicates,
}

/// <summary>Interactive below the threshold; a bulk coding job above it (Q-14).</summary>
public enum CodingPropagationModeResource
{
    Interactive,
    Job,
}

/// <summary>
/// Body of <c>POST …/coding-propagations/preview</c> ("Apply to family / duplicates", E09-T05, Q-14, Q-48): the values
/// of <paramref name="Fields"/> on the source document, as coded now, are to be copied to its related documents.
/// </summary>
/// <param name="Fields">1–100 coding field ids.</param>
public sealed record CodingPropagationPreviewRequest(Guid SourceDocumentId, CodingPropagationScopeResource Scope, IReadOnlyList<int> Fields);

/// <summary>What a propagation would do; apply it with <c>POST …/coding-propagations {previewId}</c> within 10 minutes.</summary>
/// <param name="TargetCount">Related documents the caller may code (the source excluded).</param>
/// <param name="ConflictCount">Targets with a different, non-empty value in at least one field.</param>
/// <param name="Conflicts">The first 100 conflicting (document, field) pairs.</param>
/// <param name="RestrictedCount">Related documents hidden from the caller by a restriction class (never listed).</param>
/// <param name="SkippedCount">Targets that already hold the source's values (nothing to change).</param>
/// <param name="Mode">job when <paramref name="TargetCount"/> exceeds <paramref name="Threshold"/>.</param>
public sealed record CodingPropagationPreviewResource(
    Guid PreviewId,
    int TargetCount,
    int ConflictCount,
    IReadOnlyList<CodingPropagationConflictResource> Conflicts,
    int RestrictedCount,
    int SkippedCount,
    CodingPropagationModeResource Mode,
    int Threshold);

/// <param name="CurrentValues">The target's value (choices by name).</param>
/// <param name="NewValues">The source's value that would replace it; empty when the source has none (the target is cleared).</param>
public sealed record CodingPropagationConflictResource(
    Guid DocumentId, string ControlNumber, int FieldId, IReadOnlyList<string> CurrentValues, IReadOnlyList<string> NewValues);

/// <summary>Body of <c>POST …/coding-propagations</c> (Idempotency-Key required).</summary>
public sealed record CodingPropagationApplyRequest(Guid PreviewId);

/// <summary>
/// The answer of an applied propagation: interactive (<c>200</c>, <paramref name="Applied"/> and
/// <paramref name="Skipped"/> set) or a bulk coding job (<c>202</c>, <paramref name="Job"/> set; follow it in the job
/// monitor and its outcomes at <c>…/bulk-coding/{jobId}/report</c>).
/// </summary>
/// <param name="Applied">Documents whose coding changed.</param>
/// <param name="Skipped">
/// Targets left unchanged because they were edited after the preview, deleted, or are no longer accessible.
/// </param>
public sealed record CodingPropagationResultResource(
    CodingPropagationModeResource Mode, int? Applied = null, int? Skipped = null, JobResource? Job = null);
