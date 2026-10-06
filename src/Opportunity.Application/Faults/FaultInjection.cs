#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;

namespace Opportunity.Application.Faults;

/// <summary>
/// Failpoint hooks for crash and fault tests (E06-T05, E18-T01). Compiled only when <c>OPPORTUNITY_FAILPOINTS</c> is
/// defined: every build except <c>dotnet publish -c Release</c> (Directory.Build.props), so no shipped artifact
/// contains them, and no configuration can reach them. Only test code registers an implementation in DI; without one
/// each failpoint is a null check.
/// </summary>
public interface IFaultInjector
{
    /// <summary>
    /// Called when a worker reaches <paramref name="failpoint"/> (<see cref="Failpoints"/>). Return to continue,
    /// throw <see cref="SimulatedCrashException"/> to simulate the process dying right here (the worker records
    /// nothing more and the transport leaves the delivery unsettled), throw anything else to inject an ordinary fault,
    /// or block to simulate a hang.
    /// </summary>
    ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a test-only behaviour switch (<see cref="FaultFlags"/>) is on, e.g. the deliberately unversioned
    /// OpenSearch write that proves the shadow-ledger oracle's sensitivity (E17-T07).
    /// </summary>
    bool IsArmed(string flag) => false;
}

/// <param name="Message">The delivery being handled; null at failpoints outside a consumer (the dispatcher's relays).</param>
/// <param name="Lease">The job chunk claim, once there is one.</param>
public sealed record FailpointContext(ReceivedMessage? Message, ChunkLease? Lease)
{
    /// <summary>The workspace the work belongs to (the envelope's, or the relay's pass).</summary>
    public Guid? WorkspaceId { get; init; } = Message?.Envelope.WorkspaceId ?? Lease?.WorkspaceId;

    /// <summary>The unit of work: an IndexChunkTask or SearchOutbox id, or the relay's work kind.</summary>
    public string? Subject { get; init; }

    /// <summary>1-based count of this failpoint within the unit of work (e.g. the n-th <c>_bulk</c> request of a task).</summary>
    public int Sequence { get; init; } = 1;
}

/// <summary>Every failpoint, by worker, in handling order.</summary>
public static class Failpoints
{
    /// <summary>Job chunk consumer: envelope validated; nothing written yet.</summary>
    public const string BeforeClaim = "job-chunk.before-claim";

    /// <summary>Job chunk consumer: the chunk is claimed (Running, leased); the executor has not run.</summary>
    public const string AfterClaim = "job-chunk.after-claim";

    /// <summary>Job chunk consumer: the executor finished; the chunk commit (fence F3) has not run.</summary>
    public const string BeforeCommit = "job-chunk.before-commit";

    /// <summary>Job chunk consumer: the chunk outcome is committed in PostgreSQL; the delivery is not acked yet.</summary>
    public const string AfterCommit = "job-chunk.after-commit";

    /// <summary>Chunk index worker: the IndexChunkTask is leased; nothing read yet.</summary>
    public const string IndexTaskAfterLease = "index-task.after-lease";

    /// <summary>Chunk index worker: a page of projections is read from PostgreSQL; its <c>_bulk</c> request is not sent.</summary>
    public const string IndexTaskBeforeBulk = "index-task.before-bulk";

    /// <summary>
    /// Chunk index worker: OpenSearch acknowledged a <c>_bulk</c> request; the task is not Applied in PostgreSQL. With
    /// <see cref="FailpointContext.Sequence"/> 1 of a task that needs several requests this is "mid-chunk".
    /// </summary>
    public const string IndexTaskAfterBulk = "index-task.after-bulk";

    /// <summary>Chunk index worker: the task is Applied in PostgreSQL; the delivery is not acked yet.</summary>
    public const string IndexTaskAfterApplied = "index-task.after-applied";

    /// <summary>Interactive index worker: the document's projection is read; its <c>_bulk</c> request is not sent.</summary>
    public const string OutboxBeforeBulk = "outbox.before-bulk";

    /// <summary>Interactive index worker: OpenSearch acknowledged the write; the outbox rows are not marked Applied.</summary>
    public const string OutboxAfterBulk = "outbox.after-bulk";

    /// <summary>Dispatcher: the broker confirmed the publish; the rows/tasks/chunks are not marked Dispatched yet.</summary>
    public const string RelayAfterPublish = "relay.after-publish";

    /// <summary>
    /// Bates chunk executor (E12-T03): the chunk's numbers are computed from the stored plan; nothing is written yet and
    /// the chunk is not committed ("mid-chunk").
    /// </summary>
    public const string BatesBeforeWrite = "production.bates-before-write";

    /// <summary>The job chunk consumer's failpoints, in handling order.</summary>
    public static IReadOnlyList<string> All { get; } = [BeforeClaim, AfterClaim, BeforeCommit, AfterCommit];

    /// <summary>Every failpoint of every worker.</summary>
    public static IReadOnlyList<string> Catalog { get; } =
    [
        .. All, IndexTaskAfterLease, IndexTaskBeforeBulk, IndexTaskAfterBulk, IndexTaskAfterApplied, OutboxBeforeBulk, OutboxAfterBulk,
        RelayAfterPublish, BatesBeforeWrite,
    ];
}

/// <summary>Test-only behaviour switches read through <see cref="IFaultInjector.IsArmed"/>.</summary>
public static class FaultFlags
{
    /// <summary>
    /// The projection writer omits <c>version_type=external</c>: OpenSearch then accepts any write, so a delayed older
    /// projection can overwrite a newer one. Exists only to prove the shadow-ledger oracle detects it (E17-T07).
    /// </summary>
    public const string UnversionedProjectionWrite = "projection-writer.unversioned";
}

/// <summary>
/// Simulates a process crash at a failpoint: the worker neither records an outcome nor releases its claim, and the
/// transport leaves the delivery unsettled, as if the process had died (the broker redelivers it when the connection
/// closes).
/// </summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("Simulated crash.")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
#endif
