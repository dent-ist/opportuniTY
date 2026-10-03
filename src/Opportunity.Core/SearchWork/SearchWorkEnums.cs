namespace Opportunity.Core.SearchWork;

/// <summary>
/// What a search work record asks the index worker to refresh (ADR-001 §1 R3; the "EventType" of baseline §7). Stored as
/// smallint flags; values are fixed forever.
/// </summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The ADR name of the column.")]
public enum SearchChangeMask : short
{
    None = 0,
    Content = 1,
    Metadata = 2,
    Coding = 4,

    /// <summary>A security-affecting field (Q-11) changed: the work belongs in a security lane (Q-10).</summary>
    Security = 8,
    Relationships = 16,
    Delete = 32,
}

/// <summary>SearchOutbox lifecycle (ADR-010 §2). Stored as smallint; values are fixed forever.</summary>
public enum SearchOutboxStatus : short
{
    Pending = 1,

    /// <summary>Held by one dispatcher while it publishes; the claim expires if the dispatcher dies.</summary>
    Claimed = 2,

    /// <summary>Published with a broker confirm; awaiting the index worker.</summary>
    Dispatched = 3,

    /// <summary>Indexed (directly, or coalesced by a newer write of the same document).</summary>
    Applied = 4,

    /// <summary>Out of attempts; holds the watermark back until replayed.</summary>
    Failed = 5,
}

/// <summary>IndexChunkTask lifecycle (ADR-010 §2). Stored as smallint; values are fixed forever. Never cancelled.</summary>
public enum IndexChunkTaskStatus : short
{
    Pending = 1,
    Dispatched = 2,
    Running = 3,
    RetryWait = 4,
    Applied = 5,
    Failed = 6,
}

/// <summary>Why an IndexChunkTask exists (ADR-001 §1, ADR-010 §4). Stored as smallint; values are fixed forever.</summary>
public enum IndexTaskKind : short
{
    Import = 1,
    BulkCoding = 2,

    /// <summary>Family, duplicate-group or thread fix-ups.</summary>
    Relationship = 3,

    /// <summary>Builds a new projection generation; carries no SearchGeneration (ADR-001 §7.5).</summary>
    Reindex = 4,

    /// <summary>Reconciliation repairs.</summary>
    Repair = 5,
}

/// <summary>
/// Retry rules for interactive outbox rows (ADR-001 §6.4): at most <see cref="MaxAttempts"/> attempts, backoff
/// 0.5 s × 2ⁿ capped at 30 s, then Failed (and an alert).
/// </summary>
public static class SearchOutboxRetryPolicy
{
    public const int MaxAttempts = 10;

    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>Delay before the next attempt after attempt number <paramref name="attempt"/> (1-based) failed.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        var exponent = Math.Min(attempt - 1, 16);
        return TimeSpan.FromMilliseconds(Math.Min(BaseDelay.TotalMilliseconds * Math.Pow(2, exponent), MaxDelay.TotalMilliseconds));
    }
}
