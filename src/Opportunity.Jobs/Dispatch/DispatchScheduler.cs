namespace Opportunity.Jobs.Dispatch;

/// <summary>The kinds of work the dispatcher publishes.</summary>
public enum DispatchWork
{
    /// <summary>SearchOutbox rows (lanes L0 security, L1 interactive).</summary>
    Outbox,

    /// <summary>IndexChunkTasks (lanes L2 security-bulk, L3 bulk).</summary>
    IndexTasks,

    /// <summary>Job chunks of Running jobs.</summary>
    JobChunks,
}

/// <summary>One unit the dispatcher schedules: one kind of work in one workspace.</summary>
public readonly record struct DispatchItem(Guid WorkspaceId, DispatchWork Work);

/// <summary>
/// Fair scheduling of dispatcher passes. Every (workspace, kind) is queued at most once and runs on at most one pass at a
/// time; a pass that claimed a full batch goes to the back of the queue, so a workspace with a large backlog gets one
/// batch per round and cannot starve the others. Urgent items (a security-lane wake-up, Q-10) are taken before all
/// others. A signal for an item that is running is remembered and the item is queued again when its pass ends, so no
/// wake-up is lost.
/// </summary>
public sealed class DispatchScheduler : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<DispatchItem> _urgent = new();
    private readonly Queue<DispatchItem> _normal = new();
    private readonly HashSet<DispatchItem> _queued = [];
    private readonly HashSet<DispatchItem> _urgentQueued = [];
    private readonly HashSet<DispatchItem> _running = [];
    private readonly Dictionary<DispatchItem, bool> _again = [];
    private readonly SemaphoreSlim _available = new(0);

    /// <summary>Items queued and not yet taken.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count;
            }
        }
    }

    public void Signal(DispatchItem item, bool urgent = false)
    {
        lock (_gate)
        {
            if (_running.Contains(item))
            {
                _again[item] = urgent || (_again.TryGetValue(item, out var wasUrgent) && wasUrgent);
                return;
            }

            EnqueueLocked(item, urgent);
        }
    }

    /// <summary>Waits for the next item (urgent first, then round-robin) and marks it running.</summary>
    public async ValueTask<DispatchItem> TakeAsync(CancellationToken cancellationToken)
    {
        await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            // Every release matches one queued item; an item queued urgently also sits in the normal queue, and
            // whichever copy comes second is stale and skipped.
            while (true)
            {
                var item = _urgent.Count > 0 ? _urgent.Dequeue() : _normal.Dequeue();
                _urgentQueued.Remove(item);
                if (_queued.Remove(item))
                {
                    _running.Add(item);
                    return item;
                }
            }
        }
    }

    /// <summary>The pass for <paramref name="item"/> ended; <paramref name="more"/> queues it again at the back.</summary>
    public void Complete(DispatchItem item, bool more)
    {
        lock (_gate)
        {
            _running.Remove(item);
            var again = _again.Remove(item, out var urgent);
            if (more || again)
            {
                EnqueueLocked(item, urgent);
            }
        }
    }

    public void Dispose() => _available.Dispose();

    private void EnqueueLocked(DispatchItem item, bool urgent)
    {
        if (_queued.Add(item))
        {
            _normal.Enqueue(item);
            if (urgent && _urgentQueued.Add(item))
            {
                _urgent.Enqueue(item);
            }

            _available.Release();
        }
        else if (urgent && _urgentQueued.Add(item))
        {
            _urgent.Enqueue(item);
        }
    }
}
