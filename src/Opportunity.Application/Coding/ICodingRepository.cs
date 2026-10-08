using System.Text.Json.Nodes;

using Opportunity.Application.Audit;
using Opportunity.Application.Jobs;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Coding;

/// <summary>
/// Authoritative coding store. Independent of the physical schema: the interim PostgreSQL model (E04-T04) can be
/// replaced after the coding spike (E18-T03 / ADR-004a) without touching callers. Values are canonical ADR-003 JSON.
/// </summary>
/// <remarks>
/// Every applied mutation writes, in one transaction: the current state, one CodingEvent per changed field, and one
/// DocumentVersion increment per changed document (ADR-001 §2). A write that changes nothing bumps nothing.
/// </remarks>
public interface ICodingRepository
{
    /// <summary>
    /// The workspace's latest coding event: the coding-state version a production records at finalization (Q-08,
    /// E12-T02). Null when nothing was ever coded.
    /// </summary>
    Task<CodingHighWater?> GetHighWaterAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<CodingHighWater?>(null);

    /// <summary>
    /// Applies <paramref name="request"/> (one interactive save, or one bulk chunk). Re-applying a request with the same
    /// idempotency key changes nothing and returns <see cref="CodingWriteOutcome.Replayed"/>.
    /// </summary>
    /// <remarks>
    /// An applied interactive write (no job) that changes documents also inserts one SearchOutbox row per changed document
    /// in the same transaction (ADR-001 R1). A job-originated write here creates no search work: bulk chunks use
    /// <see cref="ApplyChunkAsync"/>.
    /// </remarks>
    Task<CodingWriteResult> ApplyAsync(CodingWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// One bulk coding chunk in one transaction (§21): the coding writes, exactly one IndexChunkTask (none when nothing
    /// changed) and no SearchOutbox rows, and fence F3 that commits the chunk. <paramref name="request"/> must be a
    /// job-originated write of the leased chunk's job. When F3 refuses, nothing is written and the chunk is released.
    /// </summary>
    Task<CodingChunkResult> ApplyChunkAsync(ClaimedChunk chunk, CodingWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="ApplyChunkAsync(ClaimedChunk, CodingWriteRequest, CancellationToken)"/> that also records
    /// <paramref name="additionalItems"/> (outcomes the caller decided before the write, such as documents excluded because
    /// the initiator lost access, ADR-015 D9.4) with the chunk. <paramref name="request"/> may then name no document at
    /// all: the chunk still commits, with its item results and its audit event, and creates no index task.
    /// </summary>
    Task<CodingChunkResult> ApplyChunkAsync(
        ClaimedChunk chunk, CodingWriteRequest request, IReadOnlyList<JobItemResult> additionalItems, CancellationToken cancellationToken = default);

    /// <summary>
    /// Documents whose coding a job changed (at least one <see cref="CodingEventKind.ValueChanged"/> event of the job),
    /// in DocumentId order after <paramref name="after"/>: the "applied" list of a bulk coding report.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetJobChangedDocumentsAsync(
        Guid workspaceId, Guid jobId, Guid? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current coding of live documents with per-field change tracking (ChangedAtVersion / ChangedByJobId, ADR-010 §8).
    /// Missing or deleted documents are omitted; fields of deleted definitions are invisible.
    /// </summary>
    Task<IReadOnlyList<DocumentCoding>> GetCurrentAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Coding values of a document as of <paramref name="documentVersion"/>, rebuilt from provenance (Q-08). Fields
    /// without a value at that version are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<int, JsonNode>> GetValuesAsOfVersionAsync(
        Guid workspaceId, Guid documentId, long documentVersion, CancellationToken cancellationToken = default);

    /// <summary>Provenance events in commit order, filtered (e.g. by actor type for reports), keyset-paged.</summary>
    Task<CodingEventPage> GetEventsAsync(CodingEventQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The CodingEvent that set the current value of each of <paramref name="fieldIds"/> on a document (its latest
    /// <see cref="CodingEventKind.ValueChanged"/> event): the origin a propagation refers to (E09-T05). Fields never
    /// changed are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<int, Guid>> GetLatestChangeEventIdsAsync(
        Guid workspaceId, Guid documentId, IReadOnlyCollection<int> fieldIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current state of <paramref name="fieldIds"/> only, per live document (documents without any of them map to an
    /// empty dictionary; missing or deleted documents are omitted): the light read of a propagation preview.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FieldCodingState>>> GetFieldStatesAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> documentIds, IReadOnlyCollection<int> fieldIds, CancellationToken cancellationToken = default);
}

public sealed record CodingActor(Guid ActorId, CodingActorType Type);

/// <param name="Coding">The write as planned; it took effect only when <paramref name="Commit"/> is committed.</param>
/// <param name="Commit">Fence F3; null when the write itself was refused (invalid, replayed) and nothing was attempted.</param>
/// <param name="IndexTaskId">The chunk's IndexChunkTask; null when the chunk did not commit or changed nothing.</param>
public sealed record CodingChunkResult(CodingWriteResult Coding, ChunkCommitResult? Commit, Guid? IndexTaskId)
{
    public bool Committed => Commit?.Committed == true;
}

/// <param name="DocumentId">Document to code.</param>
/// <param name="BaselineVersion">
/// Bulk jobs: the DocumentVersion frozen in the job's snapshot (ADR-010 §8.1). A field changed after it by anyone else
/// is skipped (Q-07). Null for interactive writes, except interactive propagation (actor SystemRule), whose baseline is
/// the version its preview read: a field changed since is skipped without a skip event (those belong to jobs).
/// </param>
public sealed record CodingTarget(Guid DocumentId, long? BaselineVersion = null);

/// <summary>One state-based operation on one coding field.</summary>
public sealed record CodingFieldOperation(int FieldId, CodingOperationKind Kind, JsonNode? Value)
{
    public static CodingFieldOperation Set(int fieldId, JsonNode? value) => new(fieldId, CodingOperationKind.Set, value);

    public static CodingFieldOperation Clear(int fieldId) => new(fieldId, CodingOperationKind.Set, null);

    public static CodingFieldOperation AddChoices(int fieldId, params int[] choiceIds) =>
        new(fieldId, CodingOperationKind.AddChoices, new JsonArray([.. choiceIds.Select(id => (JsonNode)JsonValue.Create(id))]));

    public static CodingFieldOperation RemoveChoices(int fieldId, params int[] choiceIds) =>
        new(fieldId, CodingOperationKind.RemoveChoices, new JsonArray([.. choiceIds.Select(id => (JsonNode)JsonValue.Create(id))]));
}

/// <summary>A coding mutation: the same operations applied to every target document.</summary>
public sealed record CodingWriteRequest
{
    public const int MaxDocuments = 2_000;
    public const int MaxOperations = 100;
    public const int MaxIdempotencyKeyLength = 200;

    public required Guid WorkspaceId { get; init; }

    /// <summary>
    /// Interactive: derived from the HTTP <c>Idempotency-Key</c>. Bulk: the chunk key of ADR-010 §5.1, so a re-applied
    /// chunk writes no duplicate events.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    public required CodingActor Actor { get; init; }

    /// <summary>Required for <see cref="CodingActorType.BulkHuman"/>; must be null for <see cref="CodingActorType.Human"/>.</summary>
    public Guid? JobId { get; init; }

    public required IReadOnlyList<CodingTarget> Documents { get; init; }

    public required IReadOnlyList<CodingFieldOperation> Operations { get; init; }

    /// <summary>If-Match for a single-document interactive write: nothing is written when the version differs.</summary>
    public long? ExpectedVersion { get; init; }

    /// <summary>
    /// Propagation (E09-T05): per field, the CodingEvent of the originating edit; every event this write records for
    /// that field references it (<see cref="CodingEvent.OriginEventId"/>).
    /// </summary>
    public IReadOnlyDictionary<int, Guid>? OriginEventIds { get; init; }

    /// <summary>
    /// Who, from where and why (actor, client, correlation, access path). When set, an applied write stores its
    /// <c>Coding.Changed</c> / <c>Coding.BulkChunkApplied</c> audit event in the same transaction as the change
    /// (ADR-013 §2.1); the store fills category, action, resource and details. Not part of the idempotency hash.
    /// </summary>
    public AuditEvent? Audit { get; init; }
}

public enum CodingWriteOutcome
{
    /// <summary>The write committed (possibly changing nothing).</summary>
    Applied,

    /// <summary>The idempotency key was already applied with the same request; nothing was written again.</summary>
    Replayed,

    /// <summary>Request or field-level validation failed; nothing was written.</summary>
    Invalid,

    /// <summary><see cref="CodingWriteRequest.ExpectedVersion"/> did not match; nothing was written.</summary>
    VersionConflict,

    /// <summary>The single document of an interactive write does not exist or is deleted.</summary>
    NotFound,

    /// <summary>The idempotency key was used before for a different request.</summary>
    IdempotencyKeyReuse,
}

public enum DocumentCodingOutcome
{
    Changed,
    Unchanged,

    /// <summary>At least one field was skipped under Q-07; other fields may still have changed.</summary>
    Skipped,

    /// <summary>Missing or deleted; skipped.</summary>
    NotFound,

    /// <summary>
    /// The write would leave the document invalid (e.g. Withhold without a Privilege Basis, E13-T01); nothing of it was
    /// written. Only in writes over several documents: a single interactive save is <see cref="CodingWriteOutcome.Invalid"/>.
    /// </summary>
    Rejected,
}

/// <param name="DocumentVersion">Version after the write (current version when unchanged), null when not found.</param>
/// <param name="SkippedFieldIds">Fields left unchanged because of a concurrent edit (Q-07).</param>
public sealed record DocumentCodingResult(
    Guid DocumentId, DocumentCodingOutcome Outcome, long? DocumentVersion, IReadOnlyList<int> SkippedFieldIds)
{
    /// <summary>Why the document was <see cref="DocumentCodingOutcome.Rejected"/>.</summary>
    public FieldError? Error { get; init; }
}

/// <param name="TouchesSecurityAffectingField">
/// A security-affecting field (Q-11) changed: search work for this write belongs in the security lanes (ADR-001 §5).
/// </param>
public sealed record CodingWriteResult(
    CodingWriteOutcome Outcome,
    IReadOnlyList<DocumentCodingResult> Documents,
    IReadOnlyList<FieldError> Errors,
    int EventsWritten,
    bool TouchesSecurityAffectingField)
{
    /// <summary>
    /// Restriction classes this write added or removed (ADR-015 D6.1), committed with it. Empty unless a bound
    /// security-affecting field changed (<see cref="IRestrictionClassBinding"/>).
    /// </summary>
    public IReadOnlyList<RestrictionClassChange> RestrictionChanges { get; init; } = [];

    public static CodingWriteResult Failed(CodingWriteOutcome outcome, params FieldError[] errors) =>
        new(outcome, [], errors, 0, false);
}

/// <param name="Value">Canonical value, or null when cleared.</param>
/// <param name="ChangedAtVersion">DocumentVersion produced by the last change of this field (Q-07).</param>
/// <param name="ChangedByJobId">Job of the last change; null for interactive.</param>
public sealed record FieldCodingState(
    int FieldId, JsonNode? Value, long ChangedAtVersion, Guid? ChangedByJobId, Guid ChangedBy, DateTimeOffset ChangedAt);

public sealed record DocumentCoding(Guid DocumentId, long DocumentVersion, IReadOnlyList<FieldCodingState> Fields);

public sealed record CodingEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid DocumentId,
    int FieldId,
    CodingEventKind Kind,
    JsonNode? PriorValue,
    JsonNode? NewValue,
    long DocumentVersion,
    Guid ActorId,
    CodingActorType ActorType,
    Guid? JobId,
    string IdempotencyKey,
    Guid? OriginEventId = null);

public readonly record struct CodingEventCursor(DateTimeOffset OccurredAt, Guid EventId);

public sealed record CodingEventQuery(Guid WorkspaceId)
{
    public Guid? DocumentId { get; init; }

    public int? FieldId { get; init; }

    public CodingActorType? ActorType { get; init; }

    public Guid? JobId { get; init; }

    public CodingEventKind? Kind { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public CodingEventCursor? After { get; init; }

    public int Limit { get; init; } = 100;
}

public sealed record CodingEventPage(IReadOnlyList<CodingEvent> Events, CodingEventCursor? Next);

/// <summary>The latest coding event of a workspace (its time and id).</summary>
public sealed record CodingHighWater(DateTimeOffset OccurredAt, Guid EventId);
