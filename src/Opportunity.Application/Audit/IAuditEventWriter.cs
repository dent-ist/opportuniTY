using System.Collections.Concurrent;

namespace Opportunity.Application.Audit;

/// <summary>
/// Writes audit events (ADR-013). Security-relevant actions must write their event in the same transaction as the
/// action or through the outbox (ADR-015 D13.1); the PostgreSQL store that provides this arrives with E14-T01 (#115).
/// Until then hosts register <see cref="NullAuditEventWriter"/>.
/// </summary>
public interface IAuditEventWriter
{
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>Interim writer that drops events, used until the audit store (E14-T01, #115) exists.</summary>
public sealed class NullAuditEventWriter : IAuditEventWriter
{
    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Keeps events in memory, for tests and local diagnostics. Not a durable audit store.</summary>
public sealed class InMemoryAuditEventWriter : IAuditEventWriter
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public IReadOnlyList<AuditEvent> Events => [.. _events];

    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        _events.Enqueue(auditEvent);
        return ValueTask.CompletedTask;
    }

    public void Clear() => _events.Clear();
}
