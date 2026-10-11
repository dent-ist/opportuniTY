using Opportunity.Application.Audit.Chain;
using Opportunity.Core.Security;

namespace Opportunity.Application.Compliance;

/// <summary>How an access attempt by a walled user to walled content ended (E14-T06; legal finding 12).</summary>
public enum WallProofOutcome
{
    /// <summary>Refused: the wall held (the expected result).</summary>
    Denied,

    /// <summary>Allowed under an active break-glass activation (Q-45): read access with reason and MFA, audited as such.</summary>
    BreakGlass,

    /// <summary>Allowed on the normal path: the wall did not hold.</summary>
    Allowed,

    /// <summary>The action failed for another reason after authorization (nothing was served).</summary>
    Failed,
}

/// <summary>A wall as the report shows it: its current members and the number of documents it covers now.</summary>
public sealed record WallProofWall(
    Guid WallId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WallProofMember> Members,
    long DocumentsCovered);

/// <summary>A user (<see cref="UserId"/>) or IdP group (<see cref="GroupName"/>) behind a wall, and since when.</summary>
public sealed record WallProofMember(Guid? UserId, string? GroupName, string? DisplayName, DateTimeOffset AddedAt);

/// <summary>One audited attempt by a walled user on a document a wall keeps from them.</summary>
public sealed record WallProofAttempt(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    string ActorId,
    string ActorDisplay,
    Guid DocumentId,
    IReadOnlyList<Guid> WallIds,
    string Outcome,
    string? ReasonCode,
    string AccessPath,
    string CorrelationId)
{
    public WallProofOutcome Result => Outcome switch
    {
        "Denied" => WallProofOutcome.Denied,
        "Failure" => WallProofOutcome.Failed,
        _ when AccessPath == "BreakGlass" => WallProofOutcome.BreakGlass,
        _ => WallProofOutcome.Allowed,
    };
}

/// <summary>A change to a wall's definition during the period (from the audit trail).</summary>
public sealed record WallProofChange(Guid EventId, DateTimeOffset OccurredAt, string Action, string ActorDisplay, string? WallId);

public sealed record WallProofCounts(long Attempts, long Denied, long BreakGlass, long Allowed, long Failed);

/// <summary>What the store reads for a wall proof: the walls, the counted attempts, the first attempts and the wall changes.</summary>
public sealed record WallProofData(
    IReadOnlyList<WallProofWall> Walls,
    WallProofCounts Counts,
    IReadOnlyList<WallProofAttempt> Attempts,
    bool Truncated,
    IReadOnlyList<WallProofChange> Changes);

/// <summary>
/// The ethical-wall proof for a period: every audited access attempt by a walled user (directly or through an IdP group)
/// on a document one of their walls covers, plus every attempt a wall refused. The wall held when nothing was
/// <see cref="WallProofOutcome.Allowed"/>.
/// </summary>
public sealed record WallProofReport(Guid WorkspaceId, DateTimeOffset From, DateTimeOffset To, DateTimeOffset GeneratedAt, WallProofData Data)
{
    public bool Held => Data.Counts.Allowed == 0;
}

public sealed record AccessReviewRole(WorkspaceRole Role, string? ViaGroup, DateTimeOffset AssignedAt, Guid? AssignedBy);

public sealed record AccessReviewWall(Guid WallId, string Name, string? ViaGroup);

public enum AccessReviewPrincipal
{
    User,
    Group,
}

/// <summary>
/// One principal of the access review: a user (with roles held directly or through a group the IdP last reported, walls,
/// last workspace access and last sign-in) or an IdP group (with its roles, walls and the number of known members).
/// </summary>
public sealed record AccessReviewEntry(
    AccessReviewPrincipal Principal,
    Guid? UserId,
    string? GroupName,
    string? DisplayName,
    string? Email,
    IReadOnlyList<AccessReviewRole> Roles,
    IReadOnlyList<AccessReviewWall> Walls,
    DateTimeOffset? LastAccessAt,
    DateTimeOffset? LastSignInAt,
    int? KnownMembers);

public sealed record AccessReviewReport(Guid WorkspaceId, DateTimeOffset GeneratedAt, IReadOnlyList<AccessReviewEntry> Entries)
{
    public int Users => Entries.Count(e => e.Principal == AccessReviewPrincipal.User);

    public int Groups => Entries.Count(e => e.Principal == AccessReviewPrincipal.Group);
}

/// <summary>One sealed row of a chain as stored: the envelope plus the chain columns.</summary>
public sealed record AuditChainRow(AuditChainEnvelope Envelope, long? Sequence, byte[]? PrevHash, byte[]? EventHash);

/// <summary>
/// The checkpoint-aligned chain range of an audit export: it starts right after the newest checkpoint before the first
/// requested event (or at 1) and ends at the first checkpoint at or after the last one (or at that event).
/// </summary>
public sealed record AuditExportPlan(
    long FromSequence,
    long ToSequence,
    byte[] AnchorHash,
    AuditCheckpoint? Anchor,
    IReadOnlyList<AuditCheckpoint> Checkpoints,
    IReadOnlyList<AuditChainGap> Gaps,
    long Events,
    long UnsealedEvents)
{
    public long EventsAfterLastCheckpoint =>
        Events == 0 ? 0 : Math.Max(0, ToSequence - (Checkpoints.Count > 0 ? Checkpoints[^1].Sequence : FromSequence - 1));
}

/// <summary>Reads for the compliance reports (E14-T06), all inside the workspace's RLS context.</summary>
public interface IComplianceReportStore
{
    Task<WallProofData> ReadWallProofAsync(
        Guid workspaceId, DateTimeOffset periodStart, DateTimeOffset periodEnd, int maxAttempts, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccessReviewEntry>> ReadAccessReviewAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<AuditExportPlan> PlanAuditExportAsync(Guid workspaceId, DateTimeOffset? periodStart, DateTimeOffset? periodEnd, CancellationToken cancellationToken = default);

    /// <summary>The sealed rows of positions <paramref name="fromSequence"/>..<paramref name="toSequence"/> in sequence order.</summary>
    IAsyncEnumerable<AuditChainRow> ReadChainAsync(Guid workspaceId, long fromSequence, long toSequence, CancellationToken cancellationToken = default);
}
