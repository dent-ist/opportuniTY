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

    public string? CorrelationId { get; init; }

    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
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
}

public enum AuditOutcome
{
    Success,
    Denied,
    Failure,
}

/// <summary>The ADR-013 §5 taxonomy entries written so far. The full closed list is enforced by the store (E14-T01).</summary>
public static class AuditTaxonomy
{
    public static class Auth
    {
        public const string Category = "Auth";
        public const string SignIn = "SignIn";
        public const string SignInFailed = "SignInFailed";
        public const string SignOut = "SignOut";
        public const string SessionExpired = "SessionExpired";
        public const string SessionRevoked = "SessionRevoked";
        public const string StepUp = "StepUp";
    }

    public static class AuthZ
    {
        public const string Category = "AuthZ";
        public const string Denied = "Denied";
    }

    public static class Integrity
    {
        public const string Category = "Integrity";

        /// <summary>A worker rejected a message whose envelope disagrees with PostgreSQL (ADR-015 D9.3).</summary>
        public const string MessageRejected = "MessageRejected";
    }
}
