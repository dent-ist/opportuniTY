namespace Opportunity.Application.Search;

/// <summary>
/// Which fields a search can sort by (E16-T09): the fixed sort names and the workspace fields whose catalogue
/// capabilities include <c>sortable</c>. Saved searches and grid views check their sort with it when they are saved; a
/// search checks it again when it runs.
/// </summary>
public interface ISearchSortFields
{
    /// <summary>The canonical spelling of <paramref name="field"/> as a sort field, or null when it cannot be sorted.</summary>
    ValueTask<string?> ResolveAsync(Guid workspaceId, string field, CancellationToken cancellationToken = default);
}
