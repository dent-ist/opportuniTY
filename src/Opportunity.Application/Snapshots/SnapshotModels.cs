using Opportunity.Application.Audit;
using Opportunity.Core.Documents;
using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Snapshots;

/// <summary>A snapshot header to insert in <see cref="SnapshotStatus.Materializing"/>, claimed by its creator's process.</summary>
public sealed record NewSnapshot
{
    public required Guid WorkspaceId { get; init; }

    public Guid SnapshotId { get; init; } = Guid.CreateVersion7();

    public required string Name { get; init; }

    public required SnapshotPurpose Purpose { get; init; }

    public required SnapshotSourceKind SourceKind { get; init; }

    /// <summary>Query sources: the query-language text as entered.</summary>
    public string? QueryText { get; init; }

    /// <summary>Query sources: the normalized interpretation (ADR-008), shown back for confirmation.</summary>
    public string? NormalizedQuery { get; init; }

    public Guid? SourceSnapshotId { get; init; }

    /// <summary>Explicit-ID sources: how many distinct IDs were asked for.</summary>
    public int? RequestedCount { get; init; }

    public int PageSize { get; init; } = SnapshotRules.DefaultPageSize;

    /// <summary>E09-T03: relationships added to the selection before the freeze (part of the selection; immutable).</summary>
    public RelationshipExpansion Expansion { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public string? CorrelationId { get; init; }

    public string? ClientIdempotencyKey { get; init; }

    /// <summary>The claim the creating process takes so the background materializer leaves the snapshot alone.</summary>
    public required string ClaimOwner { get; init; }

    public required TimeSpan ClaimLease { get; init; }
}

/// <param name="Created">False when an earlier request with the same Idempotency-Key created <paramref name="Snapshot"/>.</param>
public sealed record SnapshotCreation(SnapshotRecord Snapshot, bool Created);

/// <summary>The stored header of a snapshot.</summary>
public sealed record SnapshotRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid SnapshotId { get; init; }

    public required SnapshotStatus Status { get; init; }

    public string? StatusReason { get; init; }

    public required string Name { get; init; }

    public required SnapshotPurpose Purpose { get; init; }

    public required SnapshotSourceKind SourceKind { get; init; }

    public string? QueryText { get; init; }

    public string? NormalizedQuery { get; init; }

    public Guid? SourceSnapshotId { get; init; }

    public int? RequestedCount { get; init; }

    public MaterializationStrategy Strategy { get; init; } = MaterializationStrategy.PgMemberPages;

    /// <summary>E09-T03: relationships added to the selection before the freeze.</summary>
    public RelationshipExpansion Expansion { get; init; }

    public int PageSize { get; init; }

    public long? SearchGeneration { get; init; }

    public int? ProjectionGeneration { get; init; }

    public bool? SelectedWhileIndexing { get; init; }

    public DateTimeOffset? SelectedAt { get; init; }

    public long? DocumentCount { get; init; }

    public long? CandidateCount { get; init; }

    public long? ExcludedNoAccess { get; init; }

    public long? ExcludedMissing { get; init; }

    public IReadOnlyDictionary<SnapshotInclusionReason, long> InclusionCounts { get; init; } = new Dictionary<SnapshotInclusionReason, long>();

    public int? PageCount { get; init; }

    public byte[]? RootSha256 { get; init; }

    public DateTimeOffset? MaterializedAt { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public string? CorrelationId { get; init; }

    public string? ClientIdempotencyKey { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int AttemptCount { get; init; }

    public DateTimeOffset? ExpiredAt { get; init; }

    /// <summary>Whether any job or review Batch Set uses the snapshot (a referenced snapshot no longer expires as unconfirmed).</summary>
    public bool Referenced { get; init; }
}

/// <summary>Keyset position of the snapshot list (newest first).</summary>
public sealed record SnapshotListCursor(DateTimeOffset CreatedAt, Guid SnapshotId);

/// <summary>
/// ADR-001 §7.2 read for a snapshot's provenance: the applied watermark (every change with a generation at or below it
/// is applied to the index) and the workspace's generation counter. Equal means the projection is current.
/// </summary>
public sealed record SearchWatermark(long Applied, long Counter)
{
    public bool IsCurrent => Applied >= Counter;
}

/// <summary>What the freeze transaction needs besides the staged candidates.</summary>
public sealed record SnapshotFreezeRequest
{
    public required Guid WorkspaceId { get; init; }

    public required Guid SnapshotId { get; init; }

    /// <summary>The claim owner; the freeze is refused when the claim was lost.</summary>
    public required string ClaimOwner { get; init; }

    public long? SearchGeneration { get; init; }

    public int? ProjectionGeneration { get; init; }

    public bool? SelectedWhileIndexing { get; init; }

    public DateTimeOffset? SelectedAt { get; init; }

    /// <summary>Candidates the selection itself dropped before staging (the Q-12 search post-filter).</summary>
    public long ExcludedBeforeStaging { get; init; }

    /// <summary>Candidate IDs read per authorization call.</summary>
    public int AuthorizationBatchSize { get; init; } = 5_000;

    /// <summary>
    /// Audit events of the freeze, built from the frozen header and inserted in the freeze transaction, so the snapshot
    /// cannot become Ready without its record (ADR-013 §2.1).
    /// </summary>
    public Func<SnapshotRecord, IReadOnlyList<AuditEvent>>? Audit { get; init; }
}

/// <summary>
/// Authorizes candidate members for the creator inside the freeze (ADR-002 §5.2.2). Returns the IDs that must be
/// excluded, with the PDP reason.
/// </summary>
public delegate Task<IReadOnlyDictionary<Guid, string>> SnapshotMemberAuthorizer(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken);

public enum SnapshotFreezeOutcome
{
    Frozen,

    /// <summary>The snapshot is not Materializing under this claim any more (another materializer took it over).</summary>
    ClaimLost,
}

public sealed record SnapshotFreezeResult(SnapshotFreezeOutcome Outcome, SnapshotRecord? Snapshot);

/// <summary>One stored membership page, for verification.</summary>
public sealed record SnapshotPage(int PageNo, long FirstOrdinal, IReadOnlyList<SnapshotMember> Members, byte[] Sha256);

/// <summary>Result of recomputing a snapshot's hashes from its stored members.</summary>
/// <param name="Valid">Every page hash, the dense ordinals 1…N, the document count and the root hash match.</param>
public sealed record SnapshotVerification(bool Valid, long DocumentCount, int PageCount, IReadOnlyList<string> Problems);
