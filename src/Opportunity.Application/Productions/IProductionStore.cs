using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Productions;

namespace Opportunity.Application.Productions;

/// <summary>Lifecycle of a production. Stored as smallint; values are fixed forever.</summary>
public enum ProductionStatus : short
{
    Draft = 1,
    Finalized = 2,
    Voided = 3,
    Discarded = 4,
}

/// <summary>State of a draft's Bates allocation. Stored as smallint; values are fixed forever.</summary>
public enum BatesAllocationState : short
{
    None = 0,
    Allocating = 1,
    Allocated = 2,
    Failed = 3,
}

/// <summary>The stored production.</summary>
public sealed record ProductionRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ProductionId { get; init; }

    public required Guid LineageId { get; init; }

    public required int Version { get; init; }

    public required string Name { get; init; }

    public required Guid SnapshotId { get; init; }

    /// <summary>The canonical specification JSON (its bytes are what <see cref="SpecificationSha256"/> covers).</summary>
    public required string SpecificationJson { get; init; }

    public required byte[] SpecificationSha256 { get; init; }

    public required string BatesPrefix { get; init; }

    public required string BatesSuffix { get; init; }

    public required int BatesPadding { get; init; }

    public required long BatesStart { get; init; }

    public required ProductionStatus Status { get; init; }

    public required long RowVersion { get; init; }

    public required BatesAllocationState BatesState { get; init; }

    public Guid? BatesJobId { get; init; }

    public string? BatesReason { get; init; }

    public long? BatesFirst { get; init; }

    public long? BatesLast { get; init; }

    public long? BatesDocuments { get; init; }

    public long? BatesUnits { get; init; }

    public byte[]? AssignmentsSha256 { get; init; }

    /// <summary>The last integrity check's report (JSON object), when one ran.</summary>
    public string? IntegrityJson { get; init; }

    public string? Manifest { get; init; }

    public byte[]? ManifestSha256 { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    public DateTimeOffset? FinalizedAt { get; init; }

    public Guid? FinalizedBy { get; init; }

    public DateTimeOffset? VoidedAt { get; init; }

    public string? VoidReason { get; init; }

    public DateTimeOffset? DiscardedAt { get; init; }

    public string BatesPrefixKey => BatesFormat.KeyOf(BatesPrefix);
}

/// <summary>The specification-derived values the store checks and indexes.</summary>
/// <param name="MinimumNumbers">A lower bound of the numbers the production will need (its member count), for the start-number overlap check.</param>
public sealed record ProductionSpecificationRow(
    string Json, byte[] Sha256, string BatesPrefix, string BatesSuffix, int BatesPadding, long BatesStart, long MinimumNumbers);

/// <summary>A draft production to create with its <c>Production.Created</c> event.</summary>
public sealed record NewProduction
{
    public required Guid WorkspaceId { get; init; }

    public Guid ProductionId { get; init; } = Guid.CreateVersion7();

    /// <summary>The production this one supersedes (its lineage and next version); null for a first version.</summary>
    public Guid? PreviousVersionId { get; init; }

    public required string Name { get; init; }

    public required Guid SnapshotId { get; init; }

    public required ProductionSpecificationRow Specification { get; init; }

    public required Guid CreatedBy { get; init; }

    public required string CreatedByDisplay { get; init; }

    public IReadOnlyList<string> CreatedByGroups { get; init; } = [];

    /// <summary>Actor and request context of the audit event; the store fills in the resource.</summary>
    public required AuditEvent AuditTemplate { get; init; }
}

/// <summary>A live Bates range of another production that a requested range overlaps.</summary>
public sealed record BatesRangeConflict(Guid ProductionId, string ProductionName, long FirstNumber, long LastNumber, ProductionStatus Status);

public enum ProductionWriteStatus
{
    Applied,
    NotFound,

    /// <summary>If-Match did not match the current row version.</summary>
    VersionConflict,

    /// <summary>The production is not in a state that allows the change (e.g. finalized, or allocating).</summary>
    InvalidState,

    /// <summary>The Bates start number lies in (or the range overlaps) another production's live range of the same prefix.</summary>
    BatesConflict,
}

public sealed record ProductionWriteResult(
    ProductionWriteStatus Status, ProductionRecord? Production = null, IReadOnlyList<BatesRangeConflict>? Conflicts = null, string? Reason = null);

/// <summary>A production that is allocating, with its job's state: the ones the coordinator plans or completes.</summary>
public sealed record ActiveBatesAllocation(ProductionRecord Production, JobStatus JobStatus, long ChunksFailed);

/// <summary>One planned allocation chunk: a dense range of production sequence numbers.</summary>
public sealed record BatesChunkRange(int ChunkSequence, long SequenceFrom, long SequenceTo);

/// <summary>Outcome of the planning transaction.</summary>
/// <param name="Conflicts">Non-empty when the range overlaps another production's live range (nothing was reserved).</param>
/// <param name="Problem">Set when planning was refused for another reason (empty set, overflow, snapshot not Ready).</param>
public sealed record BatesPlanResult(
    IReadOnlyList<BatesChunkRange> Chunks, long Documents, long Numbers, IReadOnlyList<BatesRangeConflict> Conflicts, string? Problem = null)
{
    public bool Planned => Conflicts.Count == 0 && Problem is null;
}

/// <summary>What the planner needs from the specification.</summary>
public sealed record BatesPlanRequest(BatesNumberingLevel Level, Func<string?, ProductionOutputKind> OutputFor, int DocumentsPerChunk, int UnitsPerChunk, long MaxNumber);

/// <summary>A planned member of one chunk, read back for its assignment.</summary>
public sealed record ProductionSliceRow(long Sequence, Guid DocumentId, Guid FamilyKey, int Units, long FirstOffset);

/// <summary>The integrity check of a production's allocation (E12-T03), stored with it and shown in the API.</summary>
public sealed record BatesIntegrityReport(
    bool Passed,
    long Documents,
    long Numbers,
    long Placeholders,
    long NativeSlipSheets,
    long Gaps,
    IReadOnlyList<string> Problems)
{
    public JsonObject ToJson() => new()
    {
        ["passed"] = Passed,
        ["documents"] = Documents,
        ["numbers"] = Numbers,
        ["placeholders"] = Placeholders,
        ["nativeSlipSheets"] = NativeSlipSheets,
        ["gaps"] = Gaps,
        ["problems"] = new JsonArray([.. Problems.Select(p => (JsonNode)JsonValue.Create(p))]),
    };

    public static BatesIntegrityReport? FromJson(string? json)
    {
        if (json is null || JsonNode.Parse(json) is not JsonObject o)
        {
            return null;
        }

        return new BatesIntegrityReport(
            o["passed"]?.GetValue<bool>() ?? false,
            o["documents"]?.GetValue<long>() ?? 0,
            o["numbers"]?.GetValue<long>() ?? 0,
            o["placeholders"]?.GetValue<long>() ?? 0,
            o["nativeSlipSheets"]?.GetValue<long>() ?? 0,
            o["gaps"]?.GetValue<long>() ?? 0,
            [.. (o["problems"] as JsonArray ?? []).Select(p => p?.GetValue<string>() ?? string.Empty)]);
    }
}

/// <summary>A stored member row with its numbers, for hashing, listing and lookup.</summary>
public sealed record ProductionDocumentRow(
    long Sequence,
    Guid DocumentId,
    Guid FamilyKey,
    long FirstOffset,
    long DocumentVersion,
    ProductionOutputKind Output,
    int Units,
    long? BegNumber,
    long? EndNumber,
    string? ProdBegBates,
    string? ProdEndBates,
    string? ProdBegAttach,
    string? ProdEndAttach,
    string? ControlNumber);

/// <summary>A cross-reference hit: a document's numbers in a production.</summary>
public sealed record BatesLookupRow(
    Guid ProductionId,
    string ProductionName,
    int ProductionVersion,
    ProductionStatus ProductionStatus,
    Guid DocumentId,
    string? ControlNumber,
    string ProdBegBates,
    string ProdEndBates,
    string ProdBegAttach,
    string ProdEndAttach);

/// <summary>A Bates label format in use in the workspace (to parse a looked-up label).</summary>
public sealed record BatesFormatInUse(string Prefix, string Suffix, int Padding);

/// <summary>The latest coding event of the workspace at the freeze (the coding-state version, Q-08).</summary>
public sealed record CodingHighWater(DateTimeOffset OccurredAt, Guid EventId);

/// <summary>Keyset position of the production list (newest first).</summary>
public sealed record ProductionListCursor(DateTimeOffset CreatedAt, Guid ProductionId);

/// <summary>The initiator as PostgreSQL knows them now (display name and IdP group snapshot, ADR-015 D9.4).</summary>
public sealed record ProductionInitiator(string DisplayName, IReadOnlyList<string> Groups);

/// <summary>
/// PostgreSQL storage of productions, their members and the Bates ledger (V0038). Every call runs in the workspace's
/// RLS context; lifecycle changes and their audit events commit together.
/// </summary>
public interface IProductionStore
{
    /// <summary>Creates a draft (checking the start number against other live ranges of the prefix) with <c>Production.Created</c>.</summary>
    Task<ProductionWriteResult> CreateAsync(NewProduction request, CancellationToken cancellationToken = default);

    Task<ProductionRecord?> GetAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default);

    Task<ProductionRecord?> GetByBatesJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductionRecord>> ListAsync(Guid workspaceId, ProductionListCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a draft that is not allocating (If-Match <paramref name="expectedRowVersion"/>): name, frozen set and
    /// specification. An existing allocation is released (its members removed, its range Released, Q-54).
    /// </summary>
    Task<ProductionWriteResult> UpdateDraftAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, string name, Guid snapshotId, ProductionSpecificationRow specification,
        AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the Bates allocation of a draft: creates its Production job (Created, targeting the snapshot) and marks it
    /// Allocating. A draft already allocating returns its job; an allocated draft is first released.
    /// </summary>
    Task<(ProductionWriteResult Result, JobInfo? Job)> StartAllocationAsync(
        Guid workspaceId, Guid productionId, NewJob job, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveBatesAllocation>> GetActiveAllocationsAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    Task<bool> TryClaimAsync(Guid workspaceId, Guid productionId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    Task ReleaseClaimAsync(Guid workspaceId, Guid productionId, string owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// The planning transaction (idempotent per job): orders the frozen set into production order (families adjacent),
    /// plans units, offsets and chunks with <see cref="BatesPlanner"/>, and reserves the range after checking it
    /// overlaps no live range of the prefix, under a per-(workspace, prefix) advisory lock. A planned allocation is
    /// returned as stored.
    /// </summary>
    Task<BatesPlanResult> PlanAllocationAsync(Guid workspaceId, Guid productionId, Guid jobId, BatesPlanRequest plan, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductionSliceRow>> ReadSliceAsync(
        Guid workspaceId, Guid productionId, long sequenceFrom, long sequenceTo, CancellationToken cancellationToken = default);

    /// <summary>Writes one chunk's numbers and commits the chunk (fence F3) in one transaction.</summary>
    Task<ChunkCommitResult> ApplyChunkAsync(
        ClaimedChunk chunk, Guid productionId, IReadOnlyList<BatesAssignment> assignments, ChunkCompletion completion,
        CancellationToken cancellationToken = default);

    /// <summary>The integrity check of the production's allocation: completeness, gaps, overlaps in the matter, family adjacency.</summary>
    Task<BatesIntegrityReport> CheckIntegrityAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default);

    /// <summary>Streams the members in production order (keyset by sequence).</summary>
    Task<IReadOnlyList<ProductionDocumentRow>> ReadDocumentsAsync(
        Guid workspaceId, Guid productionId, long afterSequence, int limit, CancellationToken cancellationToken = default);

    /// <summary>Allocating → Allocated (or Failed when the check did not pass, releasing the range), with the audit events.</summary>
    Task<bool> CompleteAllocationAsync(
        Guid workspaceId, Guid productionId, Guid jobId, BatesIntegrityReport integrity, byte[]? assignmentsSha256, IReadOnlyList<AuditEvent> audit,
        CancellationToken cancellationToken = default);

    /// <summary>Allocating → Failed with a reason; the reserved range (if any) is released.</summary>
    Task<bool> FailAllocationAsync(
        Guid workspaceId, Guid productionId, Guid jobId, string reason, IReadOnlyList<AuditEvent> audit, CancellationToken cancellationToken = default);

    Task<CodingHighWater?> ReadCodingHighWaterAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Draft (Allocated) → Finalized: stores the manifest, marks the range Produced, writes the audit events.</summary>
    Task<ProductionWriteResult> FinalizeAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, string manifest, byte[] manifestSha256, Guid finalizedBy, DateTimeOffset finalizedAt,
        IReadOnlyList<AuditEvent> audit, CancellationToken cancellationToken = default);

    /// <summary>Finalized → Voided: the range becomes Voided (never reissued).</summary>
    Task<ProductionWriteResult> VoidAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, string reason, Guid voidedBy, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>Draft (not allocating) → Discarded: a reserved range is Released (Q-54).</summary>
    Task<ProductionWriteResult> DiscardAsync(
        Guid workspaceId, Guid productionId, long expectedRowVersion, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BatesFormatInUse>> GetFormatsInUseAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Members of non-discarded productions whose range of <paramref name="prefixKey"/> contains <paramref name="number"/>.</summary>
    Task<IReadOnlyList<BatesLookupRow>> LookupNumberAsync(Guid workspaceId, string prefixKey, long number, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BatesLookupRow>> LookupDocumentAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Appends an audit event in its own transaction.</summary>
    Task AuditAsync(AuditEvent audit, CancellationToken cancellationToken = default);

    Task<ProductionInitiator?> ReadInitiatorAsync(Guid userId, CancellationToken cancellationToken = default);
}
