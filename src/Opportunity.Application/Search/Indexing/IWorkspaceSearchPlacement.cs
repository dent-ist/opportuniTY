namespace Opportunity.Application.Search.Indexing;

/// <summary>
/// The use-case view of index management (ADR-006 R1): application code places a workspace and reads where it stands,
/// but never sees a physical index, alias or routing value.
/// </summary>
public interface IWorkspaceSearchPlacement
{
    /// <summary>
    /// Places the workspace if it has no placement yet (idempotent: an existing placement is returned unchanged).
    /// Shared or dedicated follows the configured thresholds, or <see cref="WorkspacePlacementRequest.DedicatedIndex"/>.
    /// </summary>
    Task<WorkspaceSearchPlacementInfo> PlaceAsync(
        Guid workspaceId, WorkspacePlacementRequest request, CancellationToken cancellationToken = default);

    Task<WorkspaceSearchPlacementInfo?> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

/// <param name="DedicatedIndex">Admin flag for a large or sensitive matter.</param>
/// <param name="ExpectedDocuments">Expected document count, e.g. from import planning (ADR-006 R4a).</param>
/// <param name="ExpectedIndexedBytes">Uncalibrated estimate: sum of min(TextLength, cap) plus metadata bytes (ADR-006 R5).</param>
public sealed record WorkspacePlacementRequest(bool DedicatedIndex = false, long ExpectedDocuments = 0, long ExpectedIndexedBytes = 0)
{
    public static WorkspacePlacementRequest Default { get; } = new();
}

public sealed record WorkspaceSearchPlacementInfo(
    Guid WorkspaceId, IndexPlacementKind Kind, int ProjectionGeneration, IndexPlacementState State);
