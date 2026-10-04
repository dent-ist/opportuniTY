using System.Collections.Concurrent;

using Opportunity.Application.Fields;
using Opportunity.Core.Fields;

namespace Opportunity.Search.Querying;

/// <summary>The field catalogue the planner binds against.</summary>
internal interface ISearchFieldCatalogSource
{
    ValueTask<FieldCatalog> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the catalogue through <see cref="IFieldCatalogRepository"/> and keeps it for
/// <see cref="SearchServiceOptions.FieldCatalogCacheTtl"/> (field changes reach searches within that time, like
/// placements). Without a repository (hosts that only search system fields) the catalogue is empty and the resolver
/// falls back to the structural system fields.
/// </summary>
internal sealed class SearchFieldCatalogSource(IFieldCatalogRepository? repository, OpenSearchOptions options, TimeProvider time)
    : ISearchFieldCatalogSource
{
    private const int MaxEntries = 10_000;

    private static readonly FieldCatalog Empty = new([], []);

    private readonly ConcurrentDictionary<Guid, (FieldCatalog Catalog, DateTimeOffset Expires)> _cache = new();

    public async ValueTask<FieldCatalog> GetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (repository is null)
        {
            return Empty;
        }

        var ttl = options.Search.FieldCatalogCacheTtl;
        var now = time.GetUtcNow();
        if (ttl > TimeSpan.Zero && _cache.TryGetValue(workspaceId, out var cached) && cached.Expires > now)
        {
            return cached.Catalog;
        }

        var catalog = await repository.GetCatalogAsync(workspaceId, includeDeleted: false, cancellationToken).ConfigureAwait(false);
        if (ttl > TimeSpan.Zero)
        {
            if (_cache.Count >= MaxEntries)
            {
                foreach (var expired in _cache.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
                {
                    _cache.TryRemove(expired, out _);
                }
            }

            _cache[workspaceId] = (catalog, now + ttl);
        }

        return catalog;
    }
}
