using System.Text.Json.Nodes;

using Opportunity.Application.Jobs;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Coding;

/// <summary>
/// A bulk coding request (E10-T04): the same state-based operations applied to every member of a Ready
/// <c>BulkCoding</c> snapshot (ADR-002), one field at most once.
/// </summary>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>; a retry with it returns the job it created.</param>
public sealed record BulkCodingSubmission(Guid SnapshotId, IReadOnlyList<CodingChange> Operations, string? IdempotencyKey);

public enum BulkCodingSubmitStatus
{
    /// <summary>The job exists (created now, or by an earlier request with the same key) and runs in chunks.</summary>
    Accepted,

    /// <summary>The workspace or snapshot is unknown or not visible to the caller: 404.</summary>
    NotFound,

    /// <summary>Coding.Bulk is missing, or Coding.WritePrivilege for a security-affecting field: 403.</summary>
    Forbidden,

    Invalid,

    /// <summary>The snapshot is still materializing, failed or expired: 409.</summary>
    SnapshotNotReady,

    /// <summary>The Idempotency-Key created a different job before: 422.</summary>
    IdempotencyKeyReuse,
}

public sealed record BulkCodingSubmitOutcome
{
    public required BulkCodingSubmitStatus Status { get; init; }

    public JobInfo? Job { get; init; }

    /// <summary>False when an earlier request with the same Idempotency-Key created <see cref="Job"/>.</summary>
    public bool Created { get; init; }

    public IReadOnlyList<FieldError> Errors { get; init; } = [];

    internal static BulkCodingSubmitOutcome Of(BulkCodingSubmitStatus status, params FieldError[] errors) => new() { Status = status, Errors = errors };
}

/// <summary>Per-document outcome of a bulk coding job, as its report lists it.</summary>
public enum BulkCodingOutcome
{
    /// <summary>At least one value changed (the document's CodingEvents carry the job id).</summary>
    Applied,

    /// <summary>
    /// Q-07: someone else changed a requested field after the snapshot froze the document's baseline version, or the
    /// document was deleted since; the document was left unchanged for those fields.
    /// </summary>
    SkippedChanged,

    /// <summary>ADR-015 D9.4 / Q-15: the initiator can no longer access the document; it was excluded (reason AccessChanged).</summary>
    SkippedHidden,

    /// <summary>The item could not be applied; the rest of the chunk still committed.</summary>
    Failed,
}

/// <param name="ReasonCode"><c>Changed</c> (applied), <c>ConcurrentEdit</c>, <c>DocumentDeleted</c>, <c>AccessChanged</c> or an error code.</param>
/// <param name="FieldIds">Fields the outcome concerns (the skipped fields of a Q-07 skip); empty otherwise.</param>
public sealed record BulkCodingReportItem(Guid DocumentId, BulkCodingOutcome Outcome, string ReasonCode, IReadOnlyList<int> FieldIds);

/// <param name="Next">Opaque position of the next page (to be wrapped in a cursor by the caller); null on the last page.</param>
public sealed record BulkCodingReportPage(IReadOnlyList<BulkCodingReportItem> Items, string? Next);

/// <summary>
/// The validated operations of a bulk coding job as stored in <c>Job.Parameters</c> (identifiers and canonical values
/// only, ADR-010 §1), read back by every chunk.
/// </summary>
public static class BulkCodingParameters
{
    private const string OperationsKey = "operations";
    private const string SecurityKey = "securityAffecting";

    public static JsonObject ToJson(IReadOnlyList<CodingFieldOperation> operations, bool securityAffecting)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return new JsonObject
        {
            [OperationsKey] = new JsonArray([.. operations.Select(o => (JsonNode)new JsonObject
            {
                ["fieldId"] = o.FieldId,
                ["operation"] = o.Kind.ToString(),
                ["value"] = o.Value?.DeepClone(),
            })]),
            [SecurityKey] = securityAffecting,
        };
    }

    /// <summary>The operations of <paramref name="parameters"/>; throws <see cref="FormatException"/> when malformed.</summary>
    public static IReadOnlyList<CodingFieldOperation> Parse(JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters[OperationsKey] is not JsonArray { Count: > 0 and <= CodingWriteRequest.MaxOperations } array)
        {
            throw new FormatException("The job names no coding operations.");
        }

        var operations = new List<CodingFieldOperation>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonObject item
                || item["fieldId"] is not JsonValue fieldValue || !fieldValue.TryGetValue<int>(out var fieldId)
                || item["operation"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kindName)
                || !Enum.TryParse<CodingOperationKind>(kindName, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
            {
                throw new FormatException("A coding operation of the job is malformed.");
            }

            operations.Add(new CodingFieldOperation(fieldId, kind, item["value"]?.DeepClone()));
        }

        if (operations.Select(o => o.FieldId).Distinct().Count() != operations.Count)
        {
            throw new FormatException("The job changes a field more than once.");
        }

        return operations;
    }

    public static bool SecurityAffecting(JsonObject parameters) =>
        parameters?[SecurityKey] is JsonValue value && value.TryGetValue<bool>(out var security) && security;
}
