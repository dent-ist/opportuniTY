using System.Text.Json.Nodes;

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
    /// Applies <paramref name="request"/> (one interactive save, or one bulk chunk). Re-applying a request with the same
    /// idempotency key changes nothing and returns <see cref="CodingWriteOutcome.Replayed"/>.
    /// </summary>
    Task<CodingWriteResult> ApplyAsync(CodingWriteRequest request, CancellationToken cancellationToken = default);

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
}

public sealed record CodingActor(Guid ActorId, CodingActorType Type);

/// <param name="DocumentId">Document to code.</param>
/// <param name="BaselineVersion">
/// Bulk jobs: the DocumentVersion frozen in the job's snapshot (ADR-010 §8.1). A field changed after it by anyone else
/// is skipped (Q-07). Null for interactive writes.
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
}

/// <param name="DocumentVersion">Version after the write (current version when unchanged), null when not found.</param>
/// <param name="SkippedFieldIds">Fields left unchanged because of a concurrent edit (Q-07).</param>
public sealed record DocumentCodingResult(
    Guid DocumentId, DocumentCodingOutcome Outcome, long? DocumentVersion, IReadOnlyList<int> SkippedFieldIds);

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
    string IdempotencyKey);

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
