using Opportunity.Application.Snapshots;

namespace Opportunity.Application.Search;

/// <summary>
/// Reads a workspace's applied search watermark and generation counter (ADR-001 §7.2) for the search service's Q-10
/// freshness: a result page is current when every committed change was applied before its reader opened. Not
/// refresh-aware until E07-T08. Implemented by <c>Opportunity.Data</c>.
/// </summary>
public interface ISearchWatermarkReader
{
    Task<SearchWatermark> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
