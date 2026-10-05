using Opportunity.Core.Snapshots;

namespace Opportunity.Application.Snapshots;

/// <summary>
/// PostgreSQL storage of materialized snapshots (ADR-002 §5–§6, V0020): header, staging and immutable member pages.
/// Every call runs in the given workspace's RLS context. Callers go through <see cref="DocumentSetSnapshotService"/>,
/// which owns authorization and audit; consumers (bulk coding, export) read members by ordinal range.
/// </summary>
public interface IDocumentSetSnapshotStore
{
    /// <summary>
    /// Inserts the header (Materializing, claimed by <see cref="NewSnapshot.ClaimOwner"/>). A retry with the same
    /// creator and Idempotency-Key returns the existing snapshot with <see cref="SnapshotCreation.Created"/> false.
    /// </summary>
    Task<SnapshotCreation> CreateAsync(NewSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<SnapshotRecord?> GetAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>The snapshot an earlier request of <paramref name="createdBy"/> created with this Idempotency-Key.</summary>
    Task<SnapshotRecord?> FindByClientKeyAsync(
        Guid workspaceId, Guid createdBy, string clientIdempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Headers newest first; only <paramref name="createdBy"/>'s when given.</summary>
    Task<IReadOnlyList<SnapshotRecord>> ListAsync(
        Guid workspaceId, Guid? createdBy, SnapshotListCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>The applied search watermark and the generation counter (ADR-001 §7.2), read in one snapshot.</summary>
    Task<SearchWatermark> ReadWatermarkAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes (or renews) the materialization claim of a Materializing snapshot that has no live claim of another
    /// owner. A new claim counts an attempt. False when the snapshot is not Materializing or someone else holds it.
    /// </summary>
    Task<bool> TryClaimAsync(Guid workspaceId, Guid snapshotId, string owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Gives up a claim (the snapshot stays Materializing for the background materializer).</summary>
    Task ReleaseClaimAsync(Guid workspaceId, Guid snapshotId, string owner, CancellationToken cancellationToken = default);

    /// <summary>Materializing snapshots without a live claim, oldest first.</summary>
    Task<IReadOnlyList<Guid>> GetUnclaimedAsync(Guid workspaceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Workspaces that are not deleted (installation-level registry read), for the background loops.</summary>
    Task<IReadOnlyList<Guid>> GetWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes every staged candidate of the snapshot (a restarted selection starts empty).</summary>
    Task ResetStageAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Stages candidates (duplicates are ignored); returns how many were new.</summary>
    Task<int> StageAsync(
        Guid workspaceId, Guid snapshotId, IReadOnlyCollection<Guid> documentIds, SnapshotInclusionReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>Stages every member of a Ready source snapshot with its inclusion reason; returns how many were staged.</summary>
    Task<long> StageFromSnapshotAsync(Guid workspaceId, Guid snapshotId, Guid sourceSnapshotId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The freeze (ADR-002 §5.2), one <c>REPEATABLE READ</c> transaction: joins the staged candidates to live documents
    /// (reading each <c>DocumentVersion</c> as the member's Q-07 baseline), asks <paramref name="authorize"/> about every
    /// remaining candidate and drops the excluded ones, adds the header's relationship expansion of what remains (E09-T03,
    /// authorized the same way), orders the rest by (family sort key, family, family sequence,
    /// document), writes dense ordinals 1…N in pages with their SHA-256, the root hash and the header counts, moves the
    /// header to Ready and clears the stage. Membership becomes visible at commit, all or nothing.
    /// </summary>
    Task<SnapshotFreezeResult> FreezeAsync(
        SnapshotFreezeRequest request, SnapshotMemberAuthorizer authorize, CancellationToken cancellationToken = default);

    /// <summary>Materializing → Failed with a reason; clears the stage. False when the snapshot was not Materializing.</summary>
    Task<bool> FailAsync(Guid workspaceId, Guid snapshotId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Members with ordinals <paramref name="fromOrdinal"/>…<paramref name="toOrdinal"/> (inclusive) of a Ready snapshot, in
    /// ordinal order; empty when the snapshot is not Ready.
    /// </summary>
    Task<IReadOnlyList<SnapshotMember>> ReadMembersAsync(
        Guid workspaceId, Guid snapshotId, long fromOrdinal, long toOrdinal, CancellationToken cancellationToken = default);

    /// <summary>Stored pages <paramref name="fromPage"/>… (at most <paramref name="limit"/>) with their hashes.</summary>
    Task<IReadOnlyList<SnapshotPage>> ReadPagesAsync(
        Guid workspaceId, Guid snapshotId, int fromPage, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retention (ADR-002 §9): expires Ready or Failed snapshots that no job references once
    /// <paramref name="unreferencedLifetime"/> has passed since creation, and referenced bulk-coding snapshots once every
    /// referencing job finished more than <paramref name="jobRetention"/> ago. Export, production and report snapshots
    /// that are referenced never expire here. Member pages and staging are deleted; headers stay as tombstones.
    /// Returns the expired snapshots.
    /// </summary>
    Task<IReadOnlyList<Guid>> ExpireAsync(
        Guid workspaceId, TimeSpan unreferencedLifetime, TimeSpan jobRetention, int limit, CancellationToken cancellationToken = default);
}
