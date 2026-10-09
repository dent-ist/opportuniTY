using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Workspaces.Deletion;

namespace Opportunity.Data.Audit;

/// <summary>
/// Takes a signed audit checkpoint of the workspace's chain before its purge starts (ADR-013 §3.4, E14-T03 × E20-T02):
/// the deletion and its certificate then rest on a chain whose last event before the purge is provably covered.
/// Safe to repeat on a resumed deletion: each call adds one checkpoint (a requested checkpoint is itself audited).
/// </summary>
public sealed class AuditCheckpointBeforeDeletion(IAuditChainSealer sealer) : IBeforeWorkspaceDeletion
{
    public Task BeforePurgeAsync(Guid deletionId, Guid workspaceId, CancellationToken cancellationToken) =>
        sealer.CheckpointAsync(AuditCheckpointReason.BeforeDeletion, workspaceId, cancellationToken);
}
