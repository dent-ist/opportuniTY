namespace Opportunity.Application.Audit;

/// <summary>
/// An audit event (ADR-013 §4 envelope, the fields known at write time; <c>RecordedAt</c>, <c>Sequence</c> and the hash
/// chain are assigned by the store). <see cref="Details"/> carries IDs and enums only, never tokens, raw session IDs
/// or protected content (ADR-013 §7, ADR-015 D10.5).
/// </summary>
public sealed record AuditEvent
{
    public const short CurrentSchemaVersion = 1;

    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public short SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Null for installation-level events (the system chain), e.g. every <c>Auth.*</c> event.</summary>
    public Guid? WorkspaceId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string Category { get; init; }

    public required string Action { get; init; }

    public required AuditActorType ActorType { get; init; }

    public required string ActorId { get; init; }

    public required string ActorDisplay { get; init; }

    /// <summary>The initiating user of a worker or system action (the job's <c>InitiatedBy</c>).</summary>
    public Guid? OnBehalfOf { get; init; }

    public AuditAccessPath AccessPath { get; init; } = AuditAccessPath.Normal;

    public string? ClientIp { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>HMAC-SHA-256 of the session ID with an installation secret; never the raw ID.</summary>
    public ReadOnlyMemory<byte>? SessionIdHash { get; init; }

    public string? ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public required AuditOutcome Outcome { get; init; }

    /// <summary>Required for <see cref="AuditOutcome.Denied"/> and <see cref="AuditOutcome.Failure"/>.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>From the request or message envelope; the store falls back to the current trace ID, then a new ID.</summary>
    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public Guid? JobId { get; init; }

    public int? ChunkSequence { get; init; }

    /// <summary>The materialized snapshot the action used (§22).</summary>
    public Guid? SnapshotId { get; init; }

    /// <summary>The index generation a search or report ran against (§28).</summary>
    public long? SearchGeneration { get; init; }

    /// <summary>Action-specific IDs and enums, at most <see cref="AuditEventRules.MaxDetailsBytes"/> as JSON.</summary>
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// <c>Search.*</c> only: the full executed query text and parsed AST (Q-16), readable only with
    /// <c>Audit.ReadSearchText</c>. At most <see cref="AuditEventRules.MaxRestrictedDetailsBytes"/> as JSON.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? RestrictedDetails { get; init; }
}

public enum AuditActorType
{
    User,
    Service,
    System,
}

public enum AuditAccessPath
{
    Normal,
    BreakGlass,

    /// <summary>Reserved by ADR-013 §4; not offered in the MVP.</summary>
    Impersonation,
}

public enum AuditOutcome
{
    Success,
    Denied,
    Failure,
}

