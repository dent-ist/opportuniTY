using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.Search.Indexing;

namespace Opportunity.Search.Querying;

/// <summary>
/// <see cref="ISearchSortFields"/> over the planner's field resolution: fixed names first, then sortable workspace
/// fields by query name, bound against the current projection generation.
/// </summary>
internal sealed class SearchSortFieldCatalog(ISearchFieldCatalogSource catalogs) : ISearchSortFields
{
    public async ValueTask<string?> ResolveAsync(Guid workspaceId, string field, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return null;
        }

        if (SortKey.Resolve(field, SearchSortDirection.Asc) is { } fixedKey)
        {
            return fixedKey.Field;
        }

        var catalog = await catalogs.GetAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var resolver = SearchFieldResolver.Create(catalog, ProjectionMappings.Embedded.CurrentGeneration);
        return SearchColumns.SortKeyFor(resolver, field.Trim(), descending: false)?.Field;
    }
}
