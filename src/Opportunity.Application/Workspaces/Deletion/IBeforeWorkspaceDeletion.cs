namespace Opportunity.Application.Workspaces.Deletion;

/// <summary>
/// Integration seam (E20-T02): runs once the workspace is fenced and drained and before anything is purged, from the
/// coordinator's Inventory step. Intended for the audit hash chain (#117) to take a checkpoint of the workspace's
/// audit chain before deletion (<c>IAuditChainSealer.CheckpointAsync(BeforeDeletion, workspaceId)</c>), so the sealed
/// head is recorded before the purge starts. Optional: when no implementation is registered nothing runs.
/// </summary>
/// <remarks>
/// Must be idempotent: a step that fails or loses its lease is retried, so the hook may run more than once for the
/// same deletion. An exception keeps the run at Inventory and is retried on the next pass (nothing is purged yet).
/// </remarks>
public interface IBeforeWorkspaceDeletion
{
    /// <summary>Called before the purge of <paramref name="workspaceId"/> starts.</summary>
    Task BeforePurgeAsync(Guid deletionId, Guid workspaceId, CancellationToken cancellationToken);
}
