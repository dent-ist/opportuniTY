namespace Opportunity.Contracts.Api;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceId}/search-index/reindexes</c>: rebuild the workspace's search projection into a
/// new index (E07-T11). Every property is optional: an empty body rebuilds the current placement with the projection
/// generation of this build.
/// </summary>
/// <param name="Placement">Target tier: <c>dedicated</c> moves a shared workspace to its own index; a dedicated index never goes back.</param>
/// <param name="Generation">Target projection generation; omitted means the one this build projects.</param>
/// <param name="PrimaryShards">Primary shards of a dedicated target (1–1024); omitted derives them from the workspace size.</param>
public sealed record SearchReindexRequest(
    WorkspaceSearchPlacementKind? Placement = null, int? Generation = null, int? PrimaryShards = null);

/// <summary><c>GET …/search-index</c>: where the workspace's search projection lives and its recent reindexes.</summary>
public sealed record SearchIndexStatusResource(
    WorkspaceSearchPlacementResource? Placement, IReadOnlyList<SearchReindexResource> Reindexes);

/// <summary>One reindex, with its job (follow progress at <c>…/jobs/{jobId}</c>).</summary>
public sealed record SearchReindexResource(
    Guid JobId,
    SearchReindexPhase Phase,
    SearchIndexLocationResource? Source,
    SearchIndexLocationResource? Target,
    long? DocumentsPlanned,
    SearchReindexValidationResource? Validation,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SwitchedAt,
    DateTimeOffset? RetainUntil,
    DateTimeOffset? FinishedAt);

/// <summary>A physical location as placement facts (no index names).</summary>
public sealed record SearchIndexLocationResource(WorkspaceSearchPlacementKind Kind, int ProjectionGeneration, int Revision);

/// <summary>What validation compared before the switch (ADR-006 R13 step 5).</summary>
public sealed record SearchReindexValidationResource(
    bool Passed,
    bool FullCheck,
    long PostgresDocuments,
    long IndexDocuments,
    long DocumentsCompared,
    long Rechecked,
    long StaleDocuments,
    long MissingDocuments,
    long UnexpectedDocuments,
    long HashesCompared,
    long HashMismatches,
    string? Failure);

public enum SearchReindexPhase
{
    Pending,
    Building,
    Backfilling,
    Validating,
    Switching,
    Switched,
    Retaining,
    Completed,
    Aborting,
    Aborted,
}
