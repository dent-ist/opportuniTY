using Opportunity.Core.Documents;

namespace Opportunity.Application.Documents;

/// <summary>
/// The duplicate-group and email-thread rows of a set of documents (ADR-009 §3-§4, Q-09). Import chunks record them in
/// their own transaction through <c>Opportunity.Data.Relationships.RelationshipWriter</c>; this request is the same
/// operation for callers without one.
/// </summary>
/// <param name="DuplicateGroups">Groups the documents now reference (from <see cref="UpstreamRelationships.Apply"/>).</param>
/// <param name="EmailThreads">Threads the documents now reference.</param>
/// <param name="CoveredDocumentIds">
/// Documents whose search work the caller already creates (e.g. the chunk's IndexChunkTask). Other documents whose
/// primary flag changes are returned in <see cref="RelationshipSyncResult.OtherChangedDocuments"/>.
/// </param>
/// <param name="PreviousDuplicateGroupIds">Overlays: groups the covered documents referenced before this write.</param>
/// <param name="PreviousEmailThreadIds">Overlays: threads the covered documents referenced before this write.</param>
public sealed record RelationshipSync(
    IReadOnlyCollection<DuplicateGroupKey> DuplicateGroups,
    IReadOnlyCollection<EmailThreadKey> EmailThreads,
    IReadOnlyCollection<Guid> CoveredDocumentIds,
    IReadOnlyCollection<Guid>? PreviousDuplicateGroupIds = null,
    IReadOnlyCollection<Guid>? PreviousEmailThreadIds = null);

/// <param name="DuplicateGroupsTouched">Groups recomputed (member count and primary).</param>
/// <param name="EmailThreadsTouched">Threads recomputed (member count).</param>
/// <param name="PrimaryFlagChanges">Documents whose <c>IsDuplicatePrimary</c> changed; each got a new DocumentVersion.</param>
/// <param name="OtherChangedDocuments">The changed documents outside <see cref="RelationshipSync.CoveredDocumentIds"/>.</param>
public sealed record RelationshipSyncResult(
    int DuplicateGroupsTouched,
    int EmailThreadsTouched,
    int PrimaryFlagChanges,
    IReadOnlyList<(Guid DocumentId, long DocumentVersion)> OtherChangedDocuments);

/// <summary>A duplicate group with its label data (R15/R16): size, primary and the value that formed it.</summary>
public sealed record DuplicateGroupInfo(
    Guid DuplicateGroupId,
    DuplicateGroupSource Source,
    DuplicateHashKind HashKind,
    string HashValue,
    Guid? PrimaryFamilyId,
    Guid? PrimaryDocumentId,
    int MemberCount);

public sealed record EmailThreadInfo(Guid EmailThreadId, EmailThreadSource Source, string ThreadKey, int MemberCount);

/// <summary>One finding kind of the consistency report, with its count and a bounded sample.</summary>
public sealed record RelationshipFinding(RelationshipFindingKind Kind, long Count, IReadOnlyList<string> Sample);

public enum RelationshipFindingKind
{
    /// <summary>A group's stored member count differs from its live members.</summary>
    DuplicateGroupCountMismatch,

    /// <summary>A group's stored primary is not the ADR-009 R15 primary, or member flags disagree with it.</summary>
    DuplicateGroupPrimaryMismatch,

    /// <summary>A group row no document references.</summary>
    EmptyDuplicateGroup,

    /// <summary>One upstream group whose members carry different upstream dedupe hashes.</summary>
    DuplicateGroupHashConflict,

    /// <summary>One upstream dedupe hash spread over several upstream groups.</summary>
    DedupeHashSplitAcrossGroups,

    /// <summary>A document flagged primary without a duplicate group.</summary>
    PrimaryWithoutGroup,

    /// <summary>A thread's stored member count differs from its live members.</summary>
    EmailThreadCountMismatch,

    /// <summary>A thread row no document references.</summary>
    EmptyEmailThread,

    /// <summary>An attachment (a document with a parent) that carries an email thread (ADR-009 R20).</summary>
    AttachmentInEmailThread,
}

/// <summary>Duplicate-group and thread consistency of a workspace. Read-only; <see cref="IsConsistent"/> when nothing is found.</summary>
public sealed record RelationshipConsistencyReport(
    Guid WorkspaceId,
    long DuplicateGroups,
    long DocumentsInDuplicateGroups,
    long EmailThreads,
    long DocumentsInEmailThreads,
    IReadOnlyList<RelationshipFinding> Findings)
{
    public bool IsConsistent => Findings.All(f => f.Count == 0);

    public RelationshipFinding Finding(RelationshipFindingKind kind) => Findings.Single(f => f.Kind == kind);
}

/// <param name="Documents">Documents resolved.</param>
/// <param name="ChangedDocuments">Documents whose family columns changed; each got a new DocumentVersion and search work.</param>
/// <param name="Issues">Family report lines now recorded for the workspace.</param>
public sealed record FamilyResolutionSummary(int Documents, int ChangedDocuments, int Issues);

public interface IDocumentRelationshipRepository
{
    /// <summary>
    /// Re-resolves every family of the workspace from the stored sources (ADR-009 §2, E09-T01) and rewrites the family
    /// report; changed documents get a new DocumentVersion and SearchOutbox rows, and their duplicate groups are
    /// recomputed. Import chunks do the same incrementally; on a consistent workspace this changes nothing.
    /// </summary>
    Task<FamilyResolutionSummary> ResolveFamiliesAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records groups and threads and recomputes their member counts, primaries and primary flags in one transaction;
    /// changed documents outside <see cref="RelationshipSync.CoveredDocumentIds"/> get SearchOutbox rows.
    /// </summary>
    Task<RelationshipSyncResult> SyncAsync(Guid workspaceId, RelationshipSync sync, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes every group and thread of the workspace and removes rows nothing references: the repair after
    /// family resolution (FamilyDate and FamilyId feed the primary) or a consistency finding.
    /// </summary>
    Task<RelationshipSyncResult> RecomputeAllAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<DuplicateGroupInfo?> GetDuplicateGroupAsync(Guid workspaceId, Guid duplicateGroupId, CancellationToken cancellationToken = default);

    Task<EmailThreadInfo?> GetEmailThreadAsync(Guid workspaceId, Guid emailThreadId, CancellationToken cancellationToken = default);

    /// <summary>Live members in review order (primary family first, then natural ControlNumber order).</summary>
    Task<IReadOnlyList<Guid>> GetDuplicateMembersAsync(Guid workspaceId, Guid duplicateGroupId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Checks counts, primaries, hash agreement and the R20 rule; at most <paramref name="sampleSize"/> examples per finding.</summary>
    Task<RelationshipConsistencyReport> CheckConsistencyAsync(Guid workspaceId, int sampleSize = 20, CancellationToken cancellationToken = default);
}
