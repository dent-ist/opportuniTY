using System.Collections.Frozen;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Search;
using Opportunity.Application.Telemetry;

namespace Opportunity.Search.Freshness;

/// <summary>
/// The visible-watermark ticker of ADR-001 §7.3. One <see cref="TickAsync"/>:
/// <list type="number">
/// <item>reads each workspace's generations in one PostgreSQL snapshot at t₀, among them the applied watermark A(t₀):
/// every work record with a generation ≤ A(t₀) was marked Applied, which index workers do only after OpenSearch
/// acknowledged the write;</item>
/// <item>for the workspaces with A(t₀) above their visible watermark, refreshes their indexes (started after t₀, so it
/// covers every write acknowledged before t₀);</item>
/// <item>only for workspaces whose indexes refreshed on every shard copy, raises the visible watermark to A(t₀).</item>
/// </list>
/// The watermark therefore never passes a generation that is unapplied or applied-but-unrefreshed, never moves backwards
/// (the store takes the maximum) and is never set from an acknowledgement alone. Concurrent tickers (several dispatcher
/// replicas) are safe for the same reasons; they only add refreshes. Each tick's readings are the per-workspace sample
/// of the generation and lag gauges (ADR-017 §5): <c>opportunity.search.generation.committed</c>, <c>.indexed</c>,
/// <c>.lag</c> and <c>opportunity.search.index_lag</c>.
/// </summary>
public sealed partial class SearchWatermarkAdvancer
{
    private readonly ISearchWatermarkStore _store;
    private readonly ISearchIndexRefresher _refresher;
    private readonly ILogger<SearchWatermarkAdvancer> _logger;
    private readonly OpportunityMetrics? _metrics;
    private volatile FrozenDictionary<Guid, SearchFreshnessReading> _current = FrozenDictionary<Guid, SearchFreshnessReading>.Empty;

    public SearchWatermarkAdvancer(
        ISearchWatermarkStore store, ISearchIndexRefresher refresher, ILogger<SearchWatermarkAdvancer> logger, OpportunityMetrics? metrics = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
        if (metrics is not null)
        {
            metrics.Gauge(OpportunityMetricCatalog.SearchGenerationCommitted, () => Observe(r => r.LatestGeneration));
            metrics.Gauge(OpportunityMetricCatalog.SearchGenerationIndexed, () => Observe(r => r.IndexedThroughGeneration));
            metrics.Gauge(OpportunityMetricCatalog.SearchGenerationLag, () => Observe(r => r.PendingChanges));
            metrics.Gauge(OpportunityMetricCatalog.SearchIndexLag, () => Observe(r => r.Lag.TotalSeconds));
        }
    }

    /// <summary>The last tick's reading per workspace (after any advance).</summary>
    public IReadOnlyDictionary<Guid, SearchFreshnessReading> Current => _current;

    /// <summary>Runs one tick over <paramref name="workspaces"/>; returns the readings after it.</summary>
    public async Task<IReadOnlyDictionary<Guid, SearchFreshnessReading>> TickAsync(IReadOnlyCollection<Guid> workspaces, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var readings = new Dictionary<Guid, SearchFreshnessReading>(workspaces.Count);
        foreach (var workspaceId in workspaces.Distinct())
        {
            try
            {
                readings[workspaceId] = await _store.ReadAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogReadFailed(_logger, workspaceId, ex);
            }
        }

        // A(t0) is taken from the readings above, all completed before the refresh below starts.
        var behind = readings.Where(r => r.Value.AppliedGeneration > r.Value.IndexedThroughGeneration).ToDictionary(r => r.Key, r => r.Value.AppliedGeneration);
        if (behind.Count > 0)
        {
            var refreshed = await _refresher.RefreshAsync(behind.Keys, cancellationToken).ConfigureAwait(false);
            foreach (var (workspaceId, applied) in behind)
            {
                if (!refreshed.Contains(workspaceId))
                {
                    continue;
                }

                try
                {
                    readings[workspaceId] = await _store.AdvanceAsync(workspaceId, applied, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogAdvanceFailed(_logger, workspaceId, ex);
                }
            }
        }

        _current = readings.ToFrozenDictionary();
        return _current;
    }

    private IEnumerable<Measurement<T>> Observe<T>(Func<SearchFreshnessReading, T> value)
        where T : struct =>
        _current.Select(r => new Measurement<T>(value(r.Value), _metrics!.WorkspaceAttribute(r.Key)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search freshness of workspace {WorkspaceId} could not be read; its watermark waits")]
    private static partial void LogReadFailed(ILogger logger, Guid workspaceId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search watermark of workspace {WorkspaceId} could not be raised; the next tick retries")]
    private static partial void LogAdvanceFailed(ILogger logger, Guid workspaceId, Exception exception);
}
