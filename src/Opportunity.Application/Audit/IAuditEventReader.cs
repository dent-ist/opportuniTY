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

    /// <summary>Return <see cref="AuditEvent.RestrictedDetails"/> (search text). Requires <c>Audit.ReadSearchText</c>.</summary>
    public bool IncludeRestrictedDetails { get; init; }

    public AuditCursor? After { get; init; }

    public int Limit { get; init; } = 100;
}

/// <summary>Keyset position: events are returned in (OccurredAt, EventId) order.</summary>
public readonly record struct AuditCursor(DateTimeOffset OccurredAt, Guid EventId);

/// <param name="RecordedAt">Database time of the insert.</param>
/// <param name="Sequence">Chain position, null until sealed (E14-T03).</param>
public sealed record StoredAuditEvent(AuditEvent Event, DateTimeOffset RecordedAt, long? Sequence, DateTimeOffset? SealedAt);

public sealed record AuditEventPage(IReadOnlyList<StoredAuditEvent> Events, AuditCursor? Next);
