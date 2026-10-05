using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Opportunity.Search.Indexing;

namespace Opportunity.Search.Freshness;

/// <summary>
/// Makes acknowledged writes searchable and reports whether that worked (ADR-001 §7.3). Ports the watermark ticker
/// needs from OpenSearch; tests substitute a model of the index.
/// </summary>
public interface ISearchIndexRefresher
{
    /// <summary>
    /// Refreshes every physical index the workspaces are read from or written to (each index once, however many
    /// workspaces share it) and returns the workspaces whose indexes all refreshed with no failed shard copy. Only writes
    /// acknowledged before this call started are guaranteed to be searchable afterwards.
    /// </summary>
    Task<IReadOnlySet<Guid>> RefreshAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISearchIndexRefresher"/> over <c>POST /{index}/_refresh</c>, which is broadcast to every shard copy; a
/// refresh counts only when the response reports <c>_shards.failed == 0</c>. A workspace without a placement has
/// nothing indexed, so there is nothing to refresh for it.
/// </summary>
internal sealed partial class OpenSearchIndexRefresher(IIndexManager indexes, OpenSearchConnection connection, ILogger<OpenSearchIndexRefresher> logger)
    : ISearchIndexRefresher
{
    public async Task<IReadOnlySet<Guid>> RefreshAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceIds);
        var refreshed = new HashSet<Guid>();
        var byIndex = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var workspaceId in workspaceIds)
        {
            Placement placement;
            try
            {
                placement = await indexes.ResolveAsync(workspaceId, IndexPurpose.Read, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkspaceNotPlacedException)
            {
                refreshed.Add(workspaceId);
                continue;
            }

            // Reads go to the read alias; while a rebuild runs, writes also go to the new target, which must be
            // refreshed before the alias switch (IIndexManager.CompleteRebuildAsync does) — refreshing it here too
            // keeps every target the work was applied to visible.
            foreach (var index in placement.WriteTargets.Select(t => t.Index).Append(placement.Read.Index).Distinct(StringComparer.Ordinal))
            {
                if (!byIndex.TryGetValue(index, out var list))
                {
                    byIndex[index] = list = [];
                }

                list.Add(workspaceId);
            }
        }

        var results = await Task.WhenAll(byIndex.Keys.Select(index => RefreshIndexAsync(index, cancellationToken))).ConfigureAwait(false);
        var failed = byIndex.Keys.Zip(results).Where(r => !r.Second).SelectMany(r => byIndex[r.First]).ToHashSet();
        foreach (var workspaceId in byIndex.Values.SelectMany(w => w).Where(w => !failed.Contains(w)))
        {
            refreshed.Add(workspaceId);
        }

        return refreshed;
    }

    private async Task<bool> RefreshIndexAsync(string index, CancellationToken cancellationToken)
    {
        try
        {
            var response = await connection.SendAsync(HttpMethod.Post, $"{JsonBodies.Escape(index)}/_refresh", null, cancellationToken)
                .ConfigureAwait(false);
            var shards = response.Body?["_shards"] as JsonObject;
            var failedShards = shards?["failed"]?.GetValue<int>() ?? -1;
            if (failedShards != 0)
            {
                LogRefreshIncomplete(logger, index, failedShards);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is OpenSearchRequestException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogRefreshFailed(logger, index, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh of search index {Index} reported {FailedShards} failed shard copies; the watermark waits")]
    private static partial void LogRefreshIncomplete(ILogger logger, string index, int failedShards);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh of search index {Index} failed; the watermark waits")]
    private static partial void LogRefreshFailed(ILogger logger, string index, Exception exception);
}
