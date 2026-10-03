using System.Text.Json;

namespace Opportunity.Contracts.Messaging;

/// <summary>
/// The message envelope: exactly the fields of baseline §11 / ADR-019 §3.1, serialized as camelCase JSON by
/// <see cref="MessageSerializer"/>. <see cref="WorkspaceId"/> is a routing hint, never authority: consumers take the
/// workspace from the PostgreSQL work row (ADR-010 §9, ADR-015 D9). Payloads carry identifiers only.
/// </summary>
public sealed record MessageEnvelope
{
    /// <summary>Fresh UUIDv7 per publish attempt (ADR-010 §5.2); for tracing, not for de-duplication.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>Stable dotted name without a version, e.g. <c>indexing.indexChunkTask</c>.</summary>
    public required string MessageType { get; init; }

    public required SchemaVersion SchemaVersion { get; init; }

    public Guid? WorkspaceId { get; init; }

    public Guid? JobId { get; init; }

    /// <summary>Copied unchanged from the originating request through every message it causes.</summary>
    public required string CorrelationId { get; init; }

    /// <summary><c>messageId</c> of the message being handled when this one was published.</summary>
    public string? CausationId { get; init; }

    /// <summary>Stable across re-publish of the same work (ADR-010 §5.1).</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>UTC; serialized as ISO-8601 with millisecond precision and <c>Z</c>.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The work row's <c>AttemptCount</c> when it was published (ADR-010 §5.2).</summary>
    public int Attempt { get; init; }

    /// <summary>String headers, incl. W3C trace context (<see cref="MessageHeaderNames"/>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The payload as JSON; decoded against the registered contract of <see cref="MessageType"/>.</summary>
    public required JsonElement Payload { get; init; }
}
