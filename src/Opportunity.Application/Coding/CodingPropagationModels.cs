using System.Text.Json.Nodes;

using Opportunity.Application.Jobs;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Coding;

/// <summary>Which related documents of the source a propagation reaches (E09-T05). Stored as smallint.</summary>
public enum CodingPropagationScope : short
{
    /// <summary>The source's family (every document with its family id).</summary>
    Family = 1,

    /// <summary>The source's duplicate group.</summary>
    Duplicates = 2,

    /// <summary>Both: the union of the source's family and its duplicate group.</summary>
    FamilyAndDuplicates = 3,
}

/// <summary>Interactive (one coding write) up to the threshold; a bulk coding job above it (Q-14). Stored as smallint.</summary>
public enum CodingPropagationMode : short
{
    Interactive = 1,
    Job = 2,
}

/// <summary>Settings of "Apply to family / duplicates" (section <c>Coding:Propagation</c>).</summary>
public sealed class CodingPropagationOptions
{
    public const string SectionName = "Coding:Propagation";

    /// <summary>Q-14: above this many targets a propagation runs as a bulk job. 1 to 2,000 (one interactive write).</summary>
    public int InteractiveThreshold { get; set; } = 1_000;

    /// <summary>How long a preview may be applied.</summary>
    public TimeSpan PreviewLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The largest related set a propagation may target; larger ones are refused (use Mass Edit).</summary>
    public int MaxTargets { get; set; } = 100_000;

    public void Validate()
    {
        if (InteractiveThreshold is < 1 or > CodingWriteRequest.MaxDocuments)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(InteractiveThreshold)} must be 1–{CodingWriteRequest.MaxDocuments}.");
        }

        if (PreviewLifetime < TimeSpan.FromMinutes(1) || PreviewLifetime > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(PreviewLifetime)} must be between 1 minute and 1 hour.");
        }

        if (MaxTargets < InteractiveThreshold || MaxTargets > 1_000_000)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxTargets)} must be at least the threshold and at most 1,000,000.");
        }
    }
}

/// <summary>A related document a propagation may reach, with its current version and control number.</summary>
public readonly record struct PropagationCandidate(Guid DocumentId, long DocumentVersion, string ControlNumber);

/// <summary>The source document's relationship keys and its related documents (live, the source excluded).</summary>
/// <param name="Truncated">More than the requested limit exist; <see cref="Candidates"/> holds limit + 1 of them.</param>
public sealed record PropagationCandidates(IReadOnlyList<PropagationCandidate> Candidates, bool Truncated);

/// <summary>One propagated field as the preview read it on the source document.</summary>
/// <param name="Value">The source's canonical value (null: none; the targets are cleared).</param>
/// <param name="ChangedAtVersion">The source's version of that value (0 when never coded): a later change makes the preview stale.</param>
/// <param name="OriginEventId">The CodingEvent that set the value (null when never coded).</param>
public sealed record PropagationFieldValue(int FieldId, JsonNode? Value, long ChangedAtVersion, Guid? OriginEventId);

/// <summary>A stored preview: exactly what an apply writes.</summary>
public sealed record CodingPropagationPreview
{
    public required Guid WorkspaceId { get; init; }

    public required Guid PreviewId { get; init; }

    public required Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public required Guid SourceDocumentId { get; init; }

    public required CodingPropagationScope Scope { get; init; }

    public required IReadOnlyList<PropagationFieldValue> Fields { get; init; }

    public required bool SecurityAffecting { get; init; }

    public required IReadOnlyList<Guid> TargetIds { get; init; }

    /// <summary>Interactive mode: each target's version when previewed (the Q-07 baseline of the apply); empty for jobs.</summary>
    public required IReadOnlyList<long> TargetVersions { get; init; }

    public required CodingPropagationMode Mode { get; init; }

    public required int Threshold { get; init; }
}

/// <summary>Storage of propagation candidates and previews (PostgreSQL, V0038).</summary>
public interface ICodingPropagationRepository
{
    /// <summary>
    /// Live documents related to <paramref name="sourceDocumentId"/> by <paramref name="scope"/> (the source excluded),
    /// by DocumentId, at most <paramref name="limit"/> + 1. Null when the source does not exist or is deleted.
    /// </summary>
    Task<PropagationCandidates?> GetCandidatesAsync(
        Guid workspaceId, Guid sourceDocumentId, CodingPropagationScope scope, int limit, CancellationToken cancellationToken = default);

    /// <summary>Stores a preview (and removes this workspace's previews older than a day).</summary>
    Task SaveAsync(CodingPropagationPreview preview, CancellationToken cancellationToken = default);

    Task<CodingPropagationPreview?> GetAsync(Guid workspaceId, Guid previewId, CancellationToken cancellationToken = default);
}

/// <summary>One conflicting (document, field) pair of a preview, values canonical.</summary>
public sealed record PropagationConflict(Guid DocumentId, string ControlNumber, FieldDefinition Field, JsonNode? Current, JsonNode? New);

public enum CodingPropagationStatus
{
    Ok,

    /// <summary>The source (or the preview) does not exist or is hidden from the caller: 404.</summary>
    NotFound,

    /// <summary>Coding.Write, or Coding.WritePrivilege for a security-affecting field, is missing: 403.</summary>
    Forbidden,

    Invalid,

    /// <summary>The preview is older than its lifetime or the source's coding of its fields changed since: 409 PREVIEW_STALE.</summary>
    Stale,

    /// <summary>The related set is larger than <see cref="CodingPropagationOptions.MaxTargets"/>: 422.</summary>
    TooLarge,
}

public sealed record CodingPropagationPreviewOutcome
{
    public required CodingPropagationStatus Status { get; init; }

    public CodingPropagationPreview? Preview { get; init; }

    public int TargetCount { get; init; }

    public int ConflictCount { get; init; }

    /// <summary>The first <see cref="CodingPropagationService.MaxListedConflicts"/> conflicts.</summary>
    public IReadOnlyList<PropagationConflict> Conflicts { get; init; } = [];

    public int SkippedCount { get; init; }

    public IReadOnlyList<FieldError> Errors { get; init; } = [];

    /// <summary>The catalogue the values were read with (to render choice names).</summary>
    public FieldCatalog? Catalog { get; init; }

    internal static CodingPropagationPreviewOutcome Of(CodingPropagationStatus status, params FieldError[] errors) => new() { Status = status, Errors = errors };
}

public sealed record CodingPropagationApplyOutcome
{
    public required CodingPropagationStatus Status { get; init; }

    public CodingPropagationMode Mode { get; init; }

    public int Applied { get; init; }

    public int Skipped { get; init; }

    public JobInfo? Job { get; init; }

    public IReadOnlyList<FieldError> Errors { get; init; } = [];

    internal static CodingPropagationApplyOutcome Of(CodingPropagationStatus status, params FieldError[] errors) => new() { Status = status, Errors = errors };
}

/// <summary>Values as reviewers read them: choice names (in choice order), Yes/No, dates and numbers as stored.</summary>
public static class CodingValueText
{
    public static IReadOnlyList<string> Format(FieldDefinition field, JsonNode? value, IReadOnlyList<Choice> choices)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(choices);
        if (value is null)
        {
            return [];
        }

        if (field.IsChoice)
        {
            var ids = FieldValues.ChoiceIds(value).ToHashSet();
            return [.. choices.Where(c => ids.Contains(c.ChoiceId)).OrderBy(c => c.SortOrder).ThenBy(c => c.ChoiceId).Select(c => c.Name)];
        }

        return value is JsonArray array ? [.. array.OfType<JsonNode>().Select(Scalar).OfType<string>()] : Scalar(value) is { } text ? [text] : [];
    }

    private static string? Scalar(JsonNode node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => "Yes",
            System.Text.Json.JsonValueKind.False => "No",
            System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        }
        : node.ToJsonString();
}
