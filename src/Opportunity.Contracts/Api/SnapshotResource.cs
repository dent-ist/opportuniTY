namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/snapshots</c>: freeze a document set ("Frozen set") for a bulk
/// operation, export, production or report. Give exactly one source: a query, explicit document IDs (at most 10,000;
/// select larger sets with a query) or another snapshot (a saved selection).
/// </summary>
/// <param name="Purpose">What the set is frozen for; decides the permission needed and how long it is kept.</param>
/// <param name="Name">Display name; default e.g. "Mass Edit 2026-10-03 10:42 UTC".</param>
/// <param name="Query">Query-language text (empty selects every document you may see).</param>
/// <param name="DocumentIds">Explicit documents.</param>
/// <param name="SnapshotId">A Ready snapshot to re-freeze for you.</param>
/// <param name="SavedSearchId">A saved search you may see: its criteria are run now, filtered for you.</param>
public sealed record CreateSnapshotRequest(
    SnapshotResourcePurpose Purpose,
    string? Name = null,
    string? Query = null,
    IReadOnlyList<Guid>? DocumentIds = null,
    Guid? SnapshotId = null,
    Guid? SavedSearchId = null);

/// <summary>
/// A materialized document set (ADR-002). Once <see cref="SnapshotResourceStatus.Ready"/>, <see cref="DocumentCount"/>
/// and the membership behind <see cref="RootSha256"/> never change, whatever is coded, reindexed or deleted later.
/// </summary>
/// <param name="DocumentCount">The frozen count to confirm before a bulk operation; null until Ready.</param>
/// <param name="SearchGeneration">
/// Index generation the selection reflects at least (Q-10: show raw generations to admins and support only; reviewers see
/// <see cref="SelectedAt"/>). Null for sources that do not read the index.
/// </param>
/// <param name="SelectedWhileIndexing">The index was still catching up when the set was selected.</param>
/// <param name="ExpiresAt">When an unused set is removed (no job refers to it); null once used or expired.</param>
public sealed record SnapshotResource(
    Guid SnapshotId,
    string Name,
    SnapshotResourcePurpose Purpose,
    SnapshotResourceStatus Status,
    string? StatusReason,
    SnapshotSourceResource Source,
    long? DocumentCount,
    IReadOnlyDictionary<string, long> InclusionCounts,
    long? SearchGeneration,
    int? ProjectionGeneration,
    bool? SelectedWhileIndexing,
    DateTimeOffset? SelectedAt,
    string MaterializationStrategy,
    int PageSize,
    int? PageCount,
    string? RootSha256,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MaterializedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ExpiredAt);

/// <param name="Query">The query text; shown to the snapshot's creator only.</param>
/// <param name="NormalizedQuery">The normalized interpretation; shown to the snapshot's creator only.</param>
/// <param name="RequestedCount">Explicit-ID sources: how many distinct IDs were asked for.</param>
public sealed record SnapshotSourceResource(
    SnapshotResourceSourceKind Kind,
    string? Query,
    string? NormalizedQuery,
    Guid? SnapshotId,
    int? RequestedCount);

public enum SnapshotResourcePurpose
{
    BulkCoding,
    Export,
    Production,
    Report,
}

public enum SnapshotResourceStatus
{
    Materializing,
    Ready,
    Failed,
    Expired,
}

public enum SnapshotResourceSourceKind
{
    Query,
    DocumentIds,
    Snapshot,
}
