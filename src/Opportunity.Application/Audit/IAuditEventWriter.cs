using System.Collections.Concurrent;

namespace Opportunity.Application.Audit;

/// <summary>
/// Writes one audit event in its own transaction and returns once it is durable (ADR-013 §2): access events before
/// content is returned, denied attempts, and authentication events. If the write fails, the caller must not proceed.
/// State changes (coding, jobs, ...) do not use this port: their repositories insert the event in the transaction of
/// the change, so neither can commit without the other.
/// </summary>
public interface IAuditEventWriter
{
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
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
