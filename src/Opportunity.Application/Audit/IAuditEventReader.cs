namespace Opportunity.Application.Audit;

/// <summary>
/// Reads the audit store (ADR-013 §8). Scoped by the database: a workspace query sees only that workspace's events, a
/// query without a workspace only the system chain. Every query is itself audited as <c>Audit.Queried</c> in the same
/// transaction (§8.4). The caller authorizes first: <c>Audit.Read</c> for envelopes, and additionally
/// <c>Audit.ReadSearchText</c> before setting <see cref="AuditQuery.IncludeRestrictedDetails"/> (Q-16).
/// </summary>
public interface IAuditEventReader
{
    Task<AuditEventPage> QueryAsync(AuditQuery query, AuditEvent queriedBy, CancellationToken cancellationToken = default);
}

/// <param name="WorkspaceId">The workspace whose chain is read; null for the installation-level system chain.</param>
public sealed record AuditQuery(Guid? WorkspaceId)
{
    public const int MaxLimit = 1000;

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Category { get; init; }

    public string? Action { get; init; }

    public string? ActorId { get; init; }

    public string? ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>Events of this job: its <see cref="AuditEvent.JobId"/>, or the job as the event's resource.</summary>
    public Guid? JobId { get; init; }

    public AuditOutcome? Outcome { get; init; }

    /// <summary>
    /// Newest first: (OccurredAt, EventId) descending, and <see cref="After"/> is the last event of the previous page.
    /// </summary>
    public bool NewestFirst { get; init; }

    /// <summary>
    /// The reader's document visibility (ADR-013 §8.3): events whose resource is a document the reader may not see are
    /// left out, as if they did not exist. Null applies none (the installation chain, operator tools).
    /// </summary>
    public AuditDocumentVisibility? DocumentVisibility { get; init; }

    /// <summary>Return <see cref="AuditEvent.RestrictedDetails"/> (search text). Requires <c>Audit.ReadSearchText</c>.</summary>
    public bool IncludeRestrictedDetails { get; init; }

    public AuditCursor? After { get; init; }

    public int Limit { get; init; } = 100;
}

/// <summary>
/// Which document events a reader may see: documents carrying a denied restriction class or covered by one of the
/// reader's walls are hidden, and with <see cref="HideAllDocuments"/> every document event is (a reader without the
/// search visibility filter).
/// </summary>
public sealed record AuditDocumentVisibility(IReadOnlyList<string> DeniedClasses, IReadOnlyList<Guid> WallIds, bool HideAllDocuments = false)
{
    public static AuditDocumentVisibility Unrestricted { get; } = new([], []);

    public bool HidesAny => HideAllDocuments || DeniedClasses.Count > 0 || WallIds.Count > 0;
}

/// <summary>Keyset position: events are returned in (OccurredAt, EventId) order.</summary>
public readonly record struct AuditCursor(DateTimeOffset OccurredAt, Guid EventId);

/// <param name="RecordedAt">Database time of the insert.</param>
/// <param name="Sequence">Chain position, null until sealed (E14-T03).</param>
public sealed record StoredAuditEvent(AuditEvent Event, DateTimeOffset RecordedAt, long? Sequence, DateTimeOffset? SealedAt)
{
    /// <summary>
    /// The Control Number of the event's document resource, resolved at read time (ADR-013 §7.2: audit stores IDs) when
    /// the query applied a <see cref="AuditQuery.DocumentVisibility"/>; null otherwise or when the document is gone.
    /// </summary>
    public string? DocumentControlNumber { get; init; }
}

public sealed record AuditEventPage(IReadOnlyList<StoredAuditEvent> Events, AuditCursor? Next);
