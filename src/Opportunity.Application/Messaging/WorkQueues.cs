namespace Opportunity.Application.Messaging;

/// <summary>Priority lanes of ADR-001 §5.3 (Q-10). Lanes are separate queues with their own consumers, never <c>x-max-priority</c>.</summary>
public enum MessageLane
{
    /// <summary>Queues outside the indexing lanes (job chunk work).</summary>
    None,

    /// <summary>L0: SearchOutbox rows with <c>ChangeMask.Security</c>; reserved consumers, ≤ 5 s p95 (Q-10).</summary>
    Security,

    /// <summary>L1: other SearchOutbox rows.</summary>
    Interactive,

    /// <summary>L2: IndexChunkTasks of jobs writing a security-affecting field; strict preference over L3.</summary>
    SecurityBulk,

    /// <summary>L3: all other IndexChunkTasks.</summary>
    Bulk,
}

/// <summary>
/// A logical work queue: the destination a producer names and the unit a worker type consumes. The transport derives
/// its exchanges, retry and dead-letter queues from it; <see cref="Area"/> (the prefix before the first dot) is the
/// per-worker-type permission scope of ADR-015 D9.5 (e.g. <c>index.*</c>).
/// </summary>
/// <param name="Name">Queue name, <c>{area}.{name}</c>.</param>
/// <param name="WorkerType">The worker type that consumes it (the <c>Workers:Enabled</c> name).</param>
/// <param name="Lane">Priority lane, for indexing queues.</param>
/// <param name="Prefetch">Default unacknowledged-delivery limit per consumer; chunk work is heavy, so it is small.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "A message queue, not a collection type.")]
public sealed record WorkQueue(string Name, string WorkerType, MessageLane Lane, ushort Prefetch)
{
    public string Area => Name[..Name.IndexOf('.', StringComparison.Ordinal)];

    public override string ToString() => Name;
}

/// <summary>Every work queue of the platform. Topology is declared from this list by the migrator.</summary>
public static class WorkQueues
{
    public static WorkQueue IndexSecurity { get; } = new("index.security", "indexing", MessageLane.Security, 16);

    public static WorkQueue IndexInteractive { get; } = new("index.interactive", "indexing", MessageLane.Interactive, 16);

    public static WorkQueue IndexSecurityBulk { get; } = new("index.security-bulk", "indexing", MessageLane.SecurityBulk, 2);

    public static WorkQueue IndexBulk { get; } = new("index.bulk", "indexing", MessageLane.Bulk, 2);

    public static WorkQueue Import { get; } = new("import.chunks", "import", MessageLane.None, 1);

    public static WorkQueue BulkCoding { get; } = new("bulkcoding.chunks", "bulk-coding", MessageLane.None, 1);

    public static WorkQueue Rendering { get; } = new("render.chunks", "rendering", MessageLane.None, 2);

    public static WorkQueue Export { get; } = new("export.chunks", "export", MessageLane.None, 1);

    public static WorkQueue Production { get; } = new("production.chunks", "production", MessageLane.None, 1);

    public static IReadOnlyList<WorkQueue> All { get; } =
        [IndexSecurity, IndexInteractive, IndexSecurityBulk, IndexBulk, Import, BulkCoding, Rendering, Export, Production];
}
