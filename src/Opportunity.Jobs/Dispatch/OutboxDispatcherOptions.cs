using System.Globalization;

using Opportunity.Application.Jobs;

namespace Opportunity.Jobs.Dispatch;

/// <summary>Timing and limits of the outbox dispatcher (ADR-001 §6, ADR-010 §6; initial values). Section <c>Dispatcher</c>.</summary>
public sealed record OutboxDispatcherOptions
{
    public const string SectionName = "Dispatcher";

    /// <summary>This instance's name in claim columns. Must be unique per process: N instances run without a leader.</summary>
    public string Owner { get; init; } = string.Create(
        CultureInfo.InvariantCulture, $"dispatcher:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");

    /// <summary>SearchOutbox rows or IndexChunkTasks claimed per pass (ADR-001 §6.1).</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>A crashed dispatcher's claims are taken over after this.</summary>
    public TimeSpan ClaimDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Passes running at once (each over one kind of work in one workspace).</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Polling fallback for SearchOutbox while the LISTEN session is healthy (ADR-001 §6.2).</summary>
    public TimeSpan OutboxPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Polling for SearchOutbox while the LISTEN session is down.</summary>
    public TimeSpan OutboxPollIntervalWithoutListener { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>IndexChunkTasks never notify (ADR-001 §6.2), so they are only polled.</summary>
    public TimeSpan IndexTaskPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan JobChunkPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The list of workspaces polled is re-read this often (a wake-up names new workspaces sooner).</summary>
    public TimeSpan WorkspaceRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the <c>opportunity.outbox.*</c> gauges are sampled.</summary>
    public TimeSpan BacklogSampleInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>ADR-017 §6 outbox-age alert: the oldest undispatched SearchOutbox row is older than this.</summary>
    public TimeSpan OutboxAgeAlertThreshold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>On shutdown, passes already running get this long to finish before they are cancelled.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Delay before an unconfirmed IndexChunkTask or job chunk is offered again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public JobChunkDispatchLimits JobChunkLimits { get; init; } = new() { Operations = JobChunkRelay.DispatchedOperations };

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Owner);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Owner.Length, 200, nameof(Owner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrency);
        foreach (var interval in new[]
                 {
                     ClaimDuration, OutboxPollInterval, OutboxPollIntervalWithoutListener, IndexTaskPollInterval, JobChunkPollInterval,
                     WorkspaceRefreshInterval, BacklogSampleInterval, OutboxAgeAlertThreshold,
                 })
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        }
    }
}
