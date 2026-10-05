namespace Opportunity.Application.Search;

/// <summary>How far the search projection of a workspace trails its committed changes (Q-10, ADR-001 §7, baseline §28).</summary>
public enum SearchFreshnessLevel
{
    /// <summary>Every committed change is applied and refreshed (searchable).</summary>
    Current,

    /// <summary>Committed changes are still being indexed; the oldest is at most <see cref="SearchFreshnessReading.DelayedAfter"/> old.</summary>
    Updating,

    /// <summary>The oldest change search does not reflect yet is older than <see cref="SearchFreshnessReading.DelayedAfter"/>.</summary>
    Delayed,
}

/// <summary>
/// One read of a workspace's search generations, taken in one PostgreSQL snapshot (ADR-001 §7).
/// </summary>
/// <param name="LatestGeneration">The SearchGeneration counter: the last committed transaction that created search work.</param>
/// <param name="IndexedThroughGeneration">
/// The refresh-aware (visible) watermark: every change with a generation at or below it was applied to OpenSearch and
/// a refresh made it searchable (ADR-001 §7.3). Never moves backwards.
/// </param>
/// <param name="AppliedGeneration">
/// The applied watermark (ADR-001 §7.2): every change at or below it is acknowledged by OpenSearch, not necessarily
/// refreshed. Only the watermark ticker uses it; nothing user-facing may treat it as searchable.
/// </param>
/// <param name="OldestUnreflectedCommittedAt">CommittedAt of the oldest work record above the visible watermark; null when current.</param>
/// <param name="ReadAt">The database clock at the read (lag is measured on the clock that stamped CommittedAt).</param>
public sealed record SearchFreshnessReading(
    long LatestGeneration,
    long IndexedThroughGeneration,
    long AppliedGeneration,
    DateTimeOffset? OldestUnreflectedCommittedAt,
    DateTimeOffset ReadAt)
{
    /// <summary>Work older than this that search does not reflect makes the freshness <see cref="SearchFreshnessLevel.Delayed"/>.</summary>
    public static TimeSpan DelayedAfter { get; } = TimeSpan.FromMinutes(2);

    /// <summary>A workspace that never created search work.</summary>
    public static SearchFreshnessReading Empty(DateTimeOffset at) => new(0, 0, 0, null, at);

    public bool IsCurrent => IndexedThroughGeneration >= LatestGeneration;

    /// <summary>Committed work-creating transactions (an interactive save, a job chunk) search does not reflect yet.</summary>
    public long PendingChanges => Math.Max(0, LatestGeneration - IndexedThroughGeneration);

    /// <summary><c>search.index_lag_seconds</c> (ADR-001 §7.4): now − CommittedAt of the oldest unreflected work; zero when current.</summary>
    public TimeSpan Lag => IsCurrent || OldestUnreflectedCommittedAt is not { } at || at >= ReadAt ? TimeSpan.Zero : ReadAt - at;

    public SearchFreshnessLevel Level => IsCurrent
        ? SearchFreshnessLevel.Current
        : Lag > DelayedAfter ? SearchFreshnessLevel.Delayed : SearchFreshnessLevel.Updating;
}

/// <summary>
/// Reads a workspace's search freshness (<see cref="SearchFreshnessReading"/>) for the search service, the job monitor
/// and <c>GET …/search-freshness</c>. Implemented by <c>Opportunity.Data</c>.
/// </summary>
public interface ISearchFreshnessReader
{
    Task<SearchFreshnessReading> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The visible watermark's store (ADR-001 §7.3), used by the watermark ticker only: reads the generations (including
/// the applied watermark) and raises the visible watermark after an observed refresh. Implemented by <c>Opportunity.Data</c>.
/// </summary>
public interface ISearchWatermarkStore : ISearchFreshnessReader
{
    /// <summary>
    /// Raises the visible watermark to <paramref name="indexedThroughGeneration"/> (never lowers it, never past the
    /// counter) and returns the reading after the change. Call it only with an applied watermark read before a refresh
    /// that then succeeded on every shard copy.
    /// </summary>
    Task<SearchFreshnessReading> AdvanceAsync(Guid workspaceId, long indexedThroughGeneration, CancellationToken cancellationToken = default);
}
