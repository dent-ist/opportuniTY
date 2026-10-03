#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;

namespace Opportunity.Jobs.Faults;

/// <summary>
/// Failpoint hooks for crash and fault tests (E06-T05). Compiled only when <c>OPPORTUNITY_FAILPOINTS</c> is defined:
/// every build except <c>dotnet publish -c Release</c> (Directory.Build.props), so no shipped artifact contains them.
/// Register an implementation in DI to activate them; without one each failpoint is a null check.
/// </summary>
public interface IFaultInjector
{
    /// <summary>
    /// Called when the consumer reaches <paramref name="failpoint"/> (<see cref="Failpoints"/>). Return to continue,
    /// throw <see cref="SimulatedCrashException"/> to simulate the process dying right here (the consumer records
    /// nothing more), throw anything else to inject an ordinary fault, or block to simulate a hang.
    /// </summary>
    ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken);
}

/// <param name="Lease">The claim, once there is one.</param>
public sealed record FailpointContext(ReceivedMessage Message, ChunkLease? Lease);

/// <summary>The failpoints of the job chunk consumer, in handling order.</summary>
public static class Failpoints
{
    /// <summary>Envelope validated; nothing written yet.</summary>
    public const string BeforeClaim = "job-chunk.before-claim";

    /// <summary>The chunk is claimed (Running, leased); the executor has not run.</summary>
    public const string AfterClaim = "job-chunk.after-claim";

    /// <summary>The executor finished; the chunk commit (fence F3) has not run.</summary>
    public const string BeforeCommit = "job-chunk.before-commit";

    /// <summary>The chunk outcome is committed in PostgreSQL; the delivery is not acked yet.</summary>
    public const string AfterCommit = "job-chunk.after-commit";

    public static IReadOnlyList<string> All { get; } = [BeforeClaim, AfterClaim, BeforeCommit, AfterCommit];
}

/// <summary>
/// Simulates a process crash at a failpoint: the consumer neither records an outcome nor releases the chunk, and the
/// exception escapes to the transport like an unhandled crash would end the delivery.
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
