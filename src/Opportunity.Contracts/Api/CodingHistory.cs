using System.Text.Json.Nodes;

namespace Opportunity.Contracts.Api;

/// <summary>
/// One entry of <c>GET /api/v1/workspaces/{workspaceId}/documents/{documentId}/coding-history</c> (E10-T05): a field
/// change of the document, newest first. Fields you may not see are never listed.
/// </summary>
/// <param name="EventId">The CodingEvent (provenance) ID.</param>
/// <param name="FieldName">The field's display name now.</param>
/// <param name="Kind">
/// <c>changed</c>: the value changed. <c>skippedConcurrentEdit</c>: a bulk job left the field unchanged because someone
/// else changed it after the job started (Q-07): <see cref="OldValue"/> is the value kept, <see cref="NewValue"/> the value
/// the job did not write.
/// </param>
/// <param name="OldValue">Canonical value before the change (see <see cref="CodingFieldValueResource.Value"/>); null when empty.</param>
/// <param name="NewValue">Canonical value after the change; null when cleared.</param>
/// <param name="DocumentVersion">The document version the change produced (for a skip: the version when skipped).</param>
/// <param name="Actor">Who made the change (for a job: the person who started it).</param>
/// <param name="ActorType">Human review, a bulk job a person started, a system rule (e.g. propagation) or a model.</param>
/// <param name="Source"><c>interactive</c> when no job made the change, otherwise <c>job</c> with <see cref="JobId"/>.</param>
/// <param name="JobId">The job that made the change; null for interactive changes.</param>
/// <param name="OriginEventId">For a propagated change (Apply to Family / Duplicates): the CodingEvent of the originating edit.</param>
public sealed record CodingHistoryEntryResource(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int FieldId,
    string FieldName,
    CodingHistoryKindResource Kind,
    JsonNode? OldValue,
    JsonNode? NewValue,
    long DocumentVersion,
    CodingHistoryActorResource Actor,
    CodingActorTypeResource ActorType,
    CodingChangeSourceResource Source,
    Guid? JobId,
    Guid? OriginEventId);

/// <param name="DisplayName">Null when the user has no display name on record.</param>
public sealed record CodingHistoryActorResource(Guid UserId, string? DisplayName);

public enum CodingHistoryKindResource
{
    Changed,
    SkippedConcurrentEdit,
}

/// <summary>Who made a coding change (legal finding 15: automated coding stays distinguishable from human review).</summary>
public enum CodingActorTypeResource
{
    Human,
    BulkHuman,
    SystemRule,
    Model,
}

public enum CodingChangeSourceResource
{
    Interactive,
    Job,
}
