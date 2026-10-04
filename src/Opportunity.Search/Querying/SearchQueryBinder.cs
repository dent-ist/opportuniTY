using System.Data.Common;

using Microsoft.Extensions.Logging;

using Opportunity.Application.Search;
using Opportunity.Core.QueryLanguage;
using Opportunity.Search.Indexing;

namespace Opportunity.Search.Querying;

/// <summary>
/// <see cref="IQueryBinder"/> for the validate endpoint: runs the same translator a search runs, against the current
/// projection generation, and returns its positioned errors. Validation is advisory: when the catalogue or OpenSearch
/// cannot be reached, it answers with the parse result only (the search itself then reports the failure).
/// </summary>
internal sealed partial class SearchQueryBinder(
    ISearchQueryTranslator translator, QueryLimits limits, ProjectionMappings mappings, ILogger<SearchQueryBinder> logger) : IQueryBinder
{
    public async Task<IReadOnlyList<QueryDiagnostic>> BindAsync(Guid workspaceId, QueryNode ast, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ast);
        try
        {
            var translation = await translator.TranslateAsync(
                ast, new SearchTranslationContext(workspaceId, mappings.CurrentGeneration, limits), cancellationToken).ConfigureAwait(false);
            return translation.Errors;
        }
        catch (Exception e) when (e is OpenSearchRequestException or HttpRequestException or DbException or TimeoutException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogBindingUnavailable(logger, workspaceId, e.GetType().Name);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query binding for workspace {WorkspaceId} skipped: {Failure}")]
    private static partial void LogBindingUnavailable(ILogger logger, Guid workspaceId, string failure);
}
