using Opportunity.Application.Audit;
using Opportunity.Core.Pages;
using Opportunity.Core.Redactions;

namespace Opportunity.Application.Redactions;

/// <summary>A user as shown next to a redaction, set or reason.</summary>
public sealed record RedactionActor(Guid UserId, string DisplayName);

public sealed record RedactionSetRecord(
    Guid RedactionSetId, string Name, string? Description, bool Retired, RedactionActor? ModifiedBy, DateTimeOffset ModifiedAt, long Version);

public sealed record RedactionReasonRecord(
    string Code, string Name, RedactionReasonCategory Category, string BoxLabel, bool Active, int SortOrder, long Version);

/// <summary>A page of the document's active page set, as far as redaction needs it.</summary>
/// <param name="HasImage">A review (or inline original) raster exists: the page can be redacted (ADR-012 §3.6).</param>
public sealed record RedactionPage(int Ordinal, decimal WidthPt, decimal HeightPt, bool HasImage);

/// <summary>One active redaction: its newest revision plus the author and time of its Add.</summary>
public sealed record RedactionRecord(
    Guid RedactionId,
    Guid PageSetId,
    int Ordinal,
    NormalizedRect Rect,
    RedactionType Type,
    string ReasonCode,
    string? Note,
    RedactionActor CreatedBy,
    DateTimeOffset CreatedAt,
    RedactionActor ModifiedBy,
    DateTimeOffset ModifiedAt,
    long ChangedAtVersion);

/// <summary>One revision row (ADR-012 §3.3).</summary>
public sealed record RedactionRevisionRecord(
    long Version,
    Guid RedactionId,
    RedactionOperation Operation,
    Guid PageSetId,
    int Ordinal,
    NormalizedRect Rect,
    RedactionType Type,
    string ReasonCode,
    string? Note,
    RedactionActor Actor,
    DateTimeOffset At);

/// <summary>Who saved the newest version.</summary>
public sealed record RedactionVersionChange(RedactionActor Actor, DateTimeOffset At, long Version);

/// <summary>
/// A document's redactions in one set as of <see cref="Version"/>, with the active page set they are drawn on.
/// </summary>
public sealed record DocumentRedactionState
{
    public required Guid DocumentId { get; init; }

    public required RedactionSetRecord Set { get; init; }

    public required long Version { get; init; }

    public required long CurrentVersion { get; init; }

    public Guid? ActivePageSetId { get; init; }

    public PageSetStatus? ActivePageSetStatus { get; init; }

    /// <summary>The pages of the active page set by ordinal.</summary>
    public required IReadOnlyDictionary<int, RedactionPage> Pages { get; init; }

    public required IReadOnlyList<RedactionRecord> Redactions { get; init; }

    /// <summary>The workspace's reasons by code (names and categories of the redactions).</summary>
    public required IReadOnlyDictionary<string, RedactionReasonRecord> Reasons { get; init; }

    public RedactionVersionChange? LastChange { get; init; }

    /// <summary>New redactions can be drawn: the active page set is rendered (ADR-012 §3.6).</summary>
    public bool Redactable => ActivePageSetId is not null && ActivePageSetStatus == PageSetStatus.Ready && Pages.Values.Any(p => p.HasImage);
}

/// <summary>One revision a save writes (its version is the state's current version + 1).</summary>
public sealed record PlannedRevision(
    Guid RedactionId,
    RedactionOperation Operation,
    Guid PageSetId,
    int Ordinal,
    NormalizedRect Rect,
    RedactionType Type,
    string ReasonCode,
    string? Note);

public enum RedactionWriteStatus
{
    Ok,
    NotFound,

    /// <summary>The expected version is not the current one (If-Match).</summary>
    VersionConflict,

    /// <summary>A unique name or code is taken.</summary>
    NameTaken,

    /// <summary>The document's active page set changed or is no longer rendered since the request was validated.</summary>
    PageSetChanged,
}

public sealed record RedactionSetWriteResult(RedactionWriteStatus Status, RedactionSetRecord? Set = null);

public sealed record RedactionReasonWriteResult(RedactionWriteStatus Status, RedactionReasonRecord? Reason = null);

/// <summary>
/// Redaction Sets, the reason picklist and the insert-only redaction revisions (implemented by <c>Opportunity.Data</c>
/// under RLS). Every write inserts its audit events in its own transaction. Listing sets or reasons creates the
/// workspace defaults when it has none yet (<see cref="RedactionDefaults"/>).
/// </summary>
public interface IRedactionStore
{
    Task<IReadOnlyList<RedactionSetRecord>> ListSetsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<RedactionSetRecord?> GetSetAsync(Guid workspaceId, Guid redactionSetId, CancellationToken cancellationToken = default);

    Task<RedactionSetWriteResult> CreateSetAsync(
        Guid workspaceId, Guid redactionSetId, Guid actorId, string name, string? description, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<RedactionSetWriteResult> UpdateSetAsync(
        Guid workspaceId, Guid redactionSetId, long expectedVersion, Guid actorId, string name, string? description, bool retired, AuditEvent audit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RedactionReasonRecord>> ListReasonsAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<RedactionReasonWriteResult> CreateReasonAsync(
        Guid workspaceId, RedactionReasonRecord reason, Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<RedactionReasonWriteResult> UpdateReasonAsync(
        Guid workspaceId, RedactionReasonRecord reason, long expectedVersion, Guid actorId, AuditEvent audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The document's redactions in the set as of <paramref name="asOfVersion"/> (null: the current version); null when
    /// the set does not exist. The caller has authorized the document.
    /// </summary>
    Task<DocumentRedactionState?> GetDocumentAsync(
        Guid workspaceId, Guid documentId, Guid redactionSetId, long? asOfVersion, CancellationToken cancellationToken = default);

    /// <summary>The revisions of the document in the set, newest first (at most <paramref name="limit"/>).</summary>
    Task<IReadOnlyList<RedactionRevisionRecord>> HistoryAsync(
        Guid workspaceId, Guid documentId, Guid redactionSetId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one redaction version (ADR-012 §3.4) under the lock of the document's state row: checks that the current
    /// version is <paramref name="expectedVersion"/> and, when <paramref name="activePageSetId"/> is given, that it is
    /// still the document's rendered active page set; inserts the revisions as version + 1, refreshes the cached state
    /// and inserts <paramref name="audits"/> (built for that version), all in one transaction.
    /// </summary>
    Task<RedactionWriteStatus> SaveAsync(
        Guid workspaceId,
        Guid documentId,
        Guid redactionSetId,
        long expectedVersion,
        Guid? activePageSetId,
        Guid actorId,
        IReadOnlyList<PlannedRevision> revisions,
        IReadOnlyList<AuditEvent> audits,
        CancellationToken cancellationToken = default);
}
