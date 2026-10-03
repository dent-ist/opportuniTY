using Opportunity.Application.Messaging;

namespace Opportunity.Application.SearchWork;

/// <summary>An interactive transaction committed SearchOutbox rows of <paramref name="Lane"/> in a workspace.</summary>
public readonly record struct SearchWorkWakeUp(Guid WorkspaceId, MessageLane Lane);

/// <summary>Receives what <see cref="ISearchWorkWakeUpListener"/> hears. Called on the listener's task; keep it short.</summary>
public interface ISearchWorkWakeUpObserver
{
    /// <summary>
    /// The listener is (again) subscribed. Commits that happened while it was not are not announced, so the observer
    /// must look for due work everywhere.
    /// </summary>
    void OnListening();

    void OnWakeUp(SearchWorkWakeUp wakeUp);

    /// <summary>The subscription broke; it is re-established with backoff. Until then only polling finds work.</summary>
    void OnLost(Exception exception);
}

/// <summary>
/// The dispatcher's wake-up channel (ADR-001 §6.2): interactive outbox inserts notify at commit, so the dispatcher can
/// claim immediately instead of waiting for its polling fallback. Notifications are hints: they may be lost (e.g. on a
/// reconnect) and never carry work, only where to look.
/// </summary>
public interface ISearchWorkWakeUpListener
{
    /// <summary>Listens until <paramref name="cancellationToken"/> is cancelled, reconnecting after every failure.</summary>
    Task ListenAsync(ISearchWorkWakeUpObserver observer, CancellationToken cancellationToken);
}
