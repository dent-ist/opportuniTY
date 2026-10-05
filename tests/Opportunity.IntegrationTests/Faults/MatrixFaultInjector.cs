#if OPPORTUNITY_FAILPOINTS
using System.Collections.Concurrent;

using Opportunity.Application.Faults;
using Opportunity.Application.Messaging;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>One trial's fault: which fault fires at which hit of which failpoint, for one workspace.</summary>
internal sealed record FaultPlan(FaultKind Fault, string Failpoint, int Hit, Guid WorkspaceId, long Seed);

/// <summary>What the world does when a planned fault fires (implemented by <see cref="FaultWorld"/>).</summary>
internal interface IFaultActions
{
    /// <summary>Kills the host running <paramref name="failpoint"/> (dispatcher for the relay, else the worker) and starts a fresh one.</summary>
    void Kill(string failpoint);

    /// <summary>Hands a copy of <paramref name="message"/> to the current worker host, like the broker would.</summary>
    Task DeliverAsync(ReceivedMessage message, bool redelivered, CancellationToken cancellationToken);

    /// <summary>Expires the lease of the work behind <paramref name="context"/> (its ack deadline has passed).</summary>
    Task ExpireLeaseAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken);

    /// <summary>Whether everything of the workspace except the work behind <paramref name="context"/> is done.</summary>
    Task<bool> OthersDrainedAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken);

    /// <summary>Starts an infrastructure fault (partition, pause, restart, OpenSearch rejections) in the background.</summary>
    void StartInfrastructureFault(FaultKind fault, Random random);
}

/// <summary>
/// The matrix's <see cref="IFaultInjector"/> (registered in the worker and dispatcher hosts by the test only): fires the
/// armed <see cref="FaultPlan"/> exactly once, at the planned hit of the planned failpoint of the trial's workspace, and
/// records every failpoint it saw so a trial can prove its fault actually happened.
/// </summary>
internal sealed class MatrixFaultInjector(IFaultActions actions) : IFaultInjector
{
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);
    private FaultPlan? _plan;
    private int _fired;
    private Random _random = new(0);

    public bool Fired => Volatile.Read(ref _fired) == 1;

    public string? FiredAt { get; private set; }

    public IReadOnlyDictionary<string, int> Hits => _hits;

    public void Arm(FaultPlan plan)
    {
        _hits.Clear();
        _random = new Random(unchecked((int)plan.Seed ^ 0x2545F491));
        Volatile.Write(ref _fired, 0);
        FiredAt = null;
        Volatile.Write(ref _plan, plan);
    }

    public void Disarm() => Volatile.Write(ref _plan, null);

    public async ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken)
    {
        var plan = Volatile.Read(ref _plan);
        if (plan is null || context.WorkspaceId != plan.WorkspaceId)
        {
            return;
        }

        var count = _hits.AddOrUpdate(failpoint, 1, (_, n) => n + 1);
        if (failpoint != plan.Failpoint || count != plan.Hit || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return;
        }

        FiredAt = $"{failpoint}#{count} ({context.Subject ?? context.Message?.Envelope.MessageType}, sequence {context.Sequence})";
        switch (plan.Fault)
        {
            case FaultKind.Crash:
                actions.Kill(failpoint);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }

                throw new SimulatedCrashException($"Killed at {failpoint}.");

            case FaultKind.DuplicateDelivery when context.Message is { } message:
                _ = Task.Run(() => actions.DeliverAsync(message, redelivered: false, CancellationToken.None), CancellationToken.None);
                return;

            case FaultKind.RedeliveryAfterAckTimeout when context.Message is { } message:
                await actions.ExpireLeaseAsync(failpoint, context, cancellationToken);
                await actions.DeliverAsync(message, redelivered: true, cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return;

            case FaultKind.OutOfOrderDelivery:
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                while (DateTime.UtcNow < until && !await actions.OthersDrainedAsync(failpoint, context, cancellationToken))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }

                return;

            default:
                actions.StartInfrastructureFault(plan.Fault, _random);
                return;
        }
    }

    public bool IsArmed(string flag) => false;
}
#endif
