using System.Globalization;

namespace Opportunity.Search.Workers;

/// <summary>Settings of the chunk index worker (E07-T04); section <see cref="SectionName"/>.</summary>
public sealed class ChunkIndexWorkerOptions
{
    public const string SectionName = "Indexing:Chunks";
    public const int MaxWorkerIdLength = 200;

    /// <summary>Lease owner recorded on leased tasks (diagnostics only; the fencing token is what counts).</summary>
    public string WorkerId { get; set; } = string.Create(
        CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");

    /// <summary>Lease length; the heartbeat extends it while the task runs (ADR-010 §3).</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Watchdog: an attempt running longer is stopped and retried later (ADR-010 §3.2).</summary>
    public TimeSpan MaxTaskRuntime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Documents resolved and read from PostgreSQL per snapshot. Text is streamed one document at a time, so this bounds
    /// the read, not the memory held for texts.
    /// </summary>
    public int ReadPageSize { get; set; } = 250;

    /// <summary>ADR-001 §4 R3: a write is never sent from a PostgreSQL read older than this; such documents are re-read.</summary>
    public TimeSpan MaxReadToWriteAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>In-task retry rounds for documents whose write failed transiently (re-read, then re-sent alone).</summary>
    public int MaxRetryRounds { get; set; } = 4;

    /// <summary>Backoff before retry round n: base × 2ⁿ⁻¹ ± 20 %, capped; at least <see cref="ThrottleDelay"/> after a 429.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ThrottleDelay { get; set; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        Require(!string.IsNullOrWhiteSpace(WorkerId) && WorkerId.Length <= MaxWorkerIdLength, nameof(WorkerId));
        Require(LeaseDuration >= TimeSpan.FromSeconds(1), nameof(LeaseDuration));
        Require(HeartbeatInterval > TimeSpan.Zero && HeartbeatInterval < LeaseDuration, nameof(HeartbeatInterval));
        Require(MaxTaskRuntime > TimeSpan.Zero, nameof(MaxTaskRuntime));
        Require(ReadPageSize is >= 1 and <= 5_000, nameof(ReadPageSize));
        Require(MaxReadToWriteAge > TimeSpan.Zero, nameof(MaxReadToWriteAge));
        Require(MaxRetryRounds >= 0, nameof(MaxRetryRounds));
        Require(RetryBaseDelay >= TimeSpan.Zero && RetryMaxDelay >= RetryBaseDelay && ThrottleDelay >= TimeSpan.Zero, nameof(RetryBaseDelay));
    }

    private static void Require(bool condition, string setting)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{SectionName}:{setting} is out of range.");
        }
    }
}
