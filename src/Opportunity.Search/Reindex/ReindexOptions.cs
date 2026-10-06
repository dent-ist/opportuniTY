using Opportunity.Core.Jobs;

namespace Opportunity.Search.Reindex;

/// <summary>Settings of the reindex coordinator (E07-T11), section <c>Search:Reindex</c>.</summary>
public sealed class ReindexOptions
{
    public const string SectionName = "Search:Reindex";

    /// <summary>How often the coordinator looks at the runs it drives.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The coordinator lease on a run; another indexing worker takes over a run whose lease expired.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// ADR-001 §7.5: after dual-target writes start, the backfill waits until no process can still write from a placement
    /// cached before that (placement cache TTL, ≤ 5 s) plus the longest a worker may hold a PostgreSQL read before writing
    /// it (<c>MaxReadToWriteAge</c>, 30 s). The same delay separates the alias switch from write-blocking the old index
    /// and an abort from dropping its target once more.
    /// </summary>
    public TimeSpan WriterSettleDelay { get; set; } = TimeSpan.FromSeconds(35);

    /// <summary>Current documents per backfill chunk and its reindex task (ADR-010 §6 bound of reindex chunks).</summary>
    public int DocumentsPerTask { get; set; } = ChunkBounds.For(JobType.Reindex).MaxItems;

    /// <summary>Reindex index tasks of one job that may be un-applied at once (backpressure on the bulk lane).</summary>
    public int TaskWindow { get; set; } = 8;

    /// <summary>R13 step 7: the replaced generation stays read-only this long (at least the longest point-in-time age).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Documents compared per validation page (one PostgreSQL snapshot and one index range scan each).</summary>
    public int ValidationPageSize { get; set; } = 1_000;

    /// <summary>Up to this many documents every document is compared and hashed (ADR-006 R13 step 5); above, sampled pages.</summary>
    public long FullCheckMaxDocuments { get; set; } = 1_000_000;

    /// <summary>Pages compared when the workspace is above <see cref="FullCheckMaxDocuments"/>.</summary>
    public int SampledPages { get; set; } = 200;

    /// <summary>How long validation waits for concurrent work to be applied before it re-checks a disagreeing document.</summary>
    public TimeSpan RecheckTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Disagreeing documents validation re-checks before it gives up as failed.</summary>
    public int MaxRechecks { get; set; } = 10_000;

    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero || LeaseDuration < TimeSpan.FromSeconds(5) || WriterSettleDelay < TimeSpan.Zero
            || TaskWindow < 1 || DocumentsPerTask is < 1 or > 100_000 || Retention < TimeSpan.Zero
            || ValidationPageSize is < 10 or > 5_000 || FullCheckMaxDocuments < 0
            || SampledPages < 1 || RecheckTimeout <= TimeSpan.Zero || MaxRechecks < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}: PollInterval, RecheckTimeout must be positive, LeaseDuration at least 5 s, TaskWindow, SampledPages and " +
                "MaxRechecks at least 1, DocumentsPerTask 1 to 100,000, ValidationPageSize 10 to 5,000, WriterSettleDelay, Retention and " +
                "FullCheckMaxDocuments not negative.");
        }
    }
}
