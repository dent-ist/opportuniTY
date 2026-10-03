namespace Opportunity.Core.Jobs;

/// <summary>
/// Retry rules of ADR-010 §7: transient errors wait 5 s × 2ⁿ ± 20 % jitter (capped at 5 min) up to
/// <see cref="DefaultMaxAttempts"/> attempts; PostgreSQL is the retry ledger, never the broker.
/// </summary>
public static class ChunkRetryPolicy
{
    public const int DefaultMaxAttempts = 5;

    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    public const double Jitter = 0.2;

    /// <summary>Delay before the next attempt after attempt number <paramref name="attempt"/> (1-based) failed.</summary>
    /// <param name="jitterSample">Uniform sample in [0, 1); 0.5 means no jitter.</param>
    public static TimeSpan Backoff(int attempt, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        if (jitterSample is < 0 or >= 1 || double.IsNaN(jitterSample))
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSample), jitterSample, "Expected a sample in [0, 1).");
        }

        var exponent = Math.Min(attempt - 1, 16);
        var baseSeconds = Math.Min(BaseDelay.TotalSeconds * Math.Pow(2, exponent), MaxDelay.TotalSeconds);
        var factor = 1 + (Jitter * ((2 * jitterSample) - 1));
        return TimeSpan.FromSeconds(Math.Min(baseSeconds * factor, MaxDelay.TotalSeconds));
    }

    /// <summary>
    /// The outcome of a failed attempt: retry while the error is transient and attempts remain, otherwise fail.
    /// </summary>
    public static JobChunkTrigger OnError(ChunkErrorClass errorClass, int attemptCount, int maxAttempts) =>
        errorClass == ChunkErrorClass.Transient && attemptCount < maxAttempts
            ? JobChunkTrigger.RetryLater
            : JobChunkTrigger.FailPermanently;
}

/// <summary>
/// The job circuit breaker of ADR-010 §7.5: 5 consecutive chunk failures of the same error class, or more than 10 %
/// failed chunks once at least 20 chunks have finished, pause the job.
/// </summary>
public static class JobCircuitBreaker
{
    public const int ConsecutiveFailureLimit = 5;

    public const int MinimumFinishedForRatio = 20;

    /// <summary>Failed share above which the job pauses, as a fraction.</summary>
    public const double FailureRatio = 0.10;

    public static bool ShouldPause(int consecutiveFailuresOfSameClass, long chunksCommitted, long chunksFailed)
    {
        var finished = chunksCommitted + chunksFailed;
        return consecutiveFailuresOfSameClass >= ConsecutiveFailureLimit
            || (finished >= MinimumFinishedForRatio && chunksFailed > finished * FailureRatio);
    }
}
