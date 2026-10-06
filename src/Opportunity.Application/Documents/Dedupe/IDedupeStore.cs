using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Core.Documents;

namespace Opportunity.Application.Documents.Dedupe;

/// <summary>A workspace's saved dedupe policy; <see cref="Version"/> 0 means none was saved (the default applies).</summary>
public sealed record DedupePolicyRecord(DedupePolicy Policy, long Version, Guid? ModifiedBy, DateTimeOffset? ModifiedAt, DedupeRunSummary? LastRun);

/// <summary>
/// What a dedupe run did (E09-T04). Counts are of live documents and families when the run committed.
/// </summary>
/// <param name="Policy">The policy the run applied (with the custodian field it resolved).</param>
/// <param name="FamiliesCompared">Families (top-level parents) that had the policy's hash (and, Custodial, a custodian).</param>
/// <param name="FamiliesWithoutHash">Families whose parent lacks the policy's hash: never grouped.</param>
/// <param name="FamiliesWithoutCustodian">Custodial scope: families whose parent has no custodian value: never grouped.</param>
/// <param name="FamiliesWithUpstreamGroup">Families whose parent carries an upstream group: left as imported (Q-63).</param>
/// <param name="Groups">Computed groups (two or more families sharing a key).</param>
/// <param name="DocumentsGrouped">Documents (parents and attachments) in computed groups.</param>
/// <param name="DocumentsChanged">Documents whose group or primary flag the run changed; each was reindexed.</param>
public sealed record DedupeRunSummary(
    Guid JobId,
    DateTimeOffset RanAt,
    DedupePolicy Policy,
    long FamiliesCompared,
    long FamiliesWithoutHash,
    long FamiliesWithoutCustodian,
    long FamiliesWithUpstreamGroup,
    long Groups,
    long DocumentsGrouped,
    long DocumentsChanged);

public enum DedupePolicyWriteOutcome
{
    Saved,

    /// <summary>The stored version is not the expected one (412).</summary>
    VersionConflict,
}

/// <param name="Commit">Fence F3 of the run's chunk; nothing was written unless it is Committed.</param>
public sealed record DedupeRunResult(ChunkCommitResult Commit, DedupeRunSummary? Summary);

/// <summary>Persistence of computed duplicate grouping (E09-T04): the policy, and the run that applies it.</summary>
public interface IDedupeStore
{
    Task<DedupePolicyRecord> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the policy when the stored version is <paramref name="expectedVersion"/> (0: none saved yet), with a
    /// <c>Workspace.SettingsChanged</c> audit event in the same transaction. Saving changes no document: a run does.
    /// </summary>
    Task<(DedupePolicyWriteOutcome Outcome, DedupePolicyRecord Record)> SaveAsync(
        Guid workspaceId, long expectedVersion, DedupePolicy policy, SecurityPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Live documents of the workspace (a run's item count).</summary>
    Task<long> CountDocumentsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="policy"/> to the whole workspace in the leased chunk's transaction: regroups every
    /// family without an upstream group, recounts and re-elects the touched groups, removes groups nothing references,
    /// records the run on the policy row, commits the chunk (fence F3) and adds Relationship IndexChunkTasks for every
    /// document whose group or primary flag changed. Serialized with import chunks and family resolution.
    /// </summary>
    Task<DedupeRunResult> RunAsync(ClaimedChunk chunk, DedupePolicy policy, CancellationToken cancellationToken = default);
}
