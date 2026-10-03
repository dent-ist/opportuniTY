namespace Opportunity.Core.Jobs;

/// <summary>
/// The O(1) progress counters of a job (ADR-010 §1), maintained in each chunk's settling transaction, never derived by
/// counting chunk rows.
/// </summary>
public sealed record JobCounters
{
    public long ChunksTotal { get; init; }

    public long ChunksCommitted { get; init; }

    public long ChunksFailed { get; init; }

    public long ChunksCancelled { get; init; }

    public long ItemsApplied { get; init; }

    public long ItemsUnchanged { get; init; }

    public long ItemsSkippedConcurrentEdit { get; init; }

    public long ItemsExcludedNoAccess { get; init; }

    public long ItemsFailed { get; init; }

    public long IndexTasksTotal { get; init; }

    public long IndexTasksApplied { get; init; }

    /// <summary>Chunks not yet Committed, Failed or Cancelled.</summary>
    public long ChunksPending => ChunksTotal - ChunksCommitted - ChunksFailed - ChunksCancelled;

    public bool AllChunksSettled => ChunksPending == 0;
}

/// <summary>Which automatic job transition follows a change of chunk state (ADR-010 §2, §7.5).</summary>
public static class JobSettlement
{
    /// <param name="status">Current job status (the job row is locked by the caller).</param>
    /// <param name="counters">Counters after the change.</param>
    /// <param name="consecutiveFailures">Consecutive chunk failures of the same error class.</param>
    public static JobTrigger? Next(JobStatus status, JobCounters counters, int consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(counters);
        return status switch
        {
            JobStatus.Running when counters.AllChunksSettled =>
                counters.ChunksFailed > 0 || counters.ItemsFailed > 0 ? JobTrigger.CompleteWithErrors : JobTrigger.Complete,
            JobStatus.Running when JobCircuitBreaker.ShouldPause(consecutiveFailures, counters.ChunksCommitted, counters.ChunksFailed) =>
                JobTrigger.Pause,
            // Cancel settles every chunk that is not Running at once, so unsettled chunks of a Cancelling job are Running.
            JobStatus.Cancelling when counters.AllChunksSettled => JobTrigger.FinishCancelling,
            _ => null,
        };
    }
}
