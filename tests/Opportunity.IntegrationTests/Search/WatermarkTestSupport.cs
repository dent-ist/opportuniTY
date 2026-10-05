using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Opportunity.Application.Search;
using Opportunity.Data.Search;
using Opportunity.Search.Freshness;
using Opportunity.Search.Indexing;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// The visible-watermark ticker of the dispatcher host (E07-T08) over a harness's database and OpenSearch: tests run a
/// tick where the dispatcher would run one every second.
/// </summary>
internal static class WatermarkTestSupport
{
    /// <summary>A ticker over <paramref name="services"/>' index management and connection (search-module internals).</summary>
    public static SearchWatermarkAdvancer Advancer(IServiceProvider services, NpgsqlDataSource appDataSource, ISearchIndexRefresher? refresher = null) =>
        new(
            new SearchWatermarkStore(appDataSource),
            refresher ?? Refresher(services),
            NullLogger<SearchWatermarkAdvancer>.Instance);

    public static ISearchIndexRefresher Refresher(IServiceProvider services) => new OpenSearchIndexRefresher(
        services.GetRequiredService<IIndexManager>(),
        services.GetRequiredService<OpenSearchConnection>(),
        NullLogger<OpenSearchIndexRefresher>.Instance);

    /// <summary>One tick for one workspace; returns its reading afterwards.</summary>
    public static async Task<SearchFreshnessReading> TickAsync(IServiceProvider services, NpgsqlDataSource appDataSource, Guid workspaceId)
    {
        var readings = await Advancer(services, appDataSource).TickAsync([workspaceId], TestContext.Current.CancellationToken);
        return readings[workspaceId];
    }
}
