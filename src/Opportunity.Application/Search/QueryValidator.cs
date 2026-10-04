using System.Text.Json;

using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Application.Search;

/// <summary>
/// The validate use case behind the query bar (E07-T06): parse, then bind against the workspace when an
/// <see cref="IQueryBinder"/> is available (E07-T07: <c>UNKNOWN_FIELD</c>, capability and value errors with spans).
/// Raw search DSL is never accepted: the input is query text.
/// </summary>
public sealed class QueryValidator(QueryLimits limits)
{
    public QueryLimits Limits { get; } = limits;

    public QueryValidationResult Validate(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ToResult(QueryParser.Parse(query, Limits));
    }

    /// <summary>Parses, then binds a parsable query against <paramref name="workspaceId"/>; binding errors make it invalid.</summary>
    public Task<QueryValidationResult> ValidateAsync(
        string query, Guid workspaceId, IQueryBinder? binder, CancellationToken cancellationToken = default) =>
        ValidateAsync(query, workspaceId, binder, null, null, cancellationToken);

    /// <summary>
    /// Parses, expands saved-search references (<c>savedsearch:&lt;id&gt;</c>, E07-T09: each must be visible to
    /// <paramref name="caller"/>, nesting without cycles) when <paramref name="savedSearches"/> is given, then binds the
    /// expanded query. The returned AST is the query as written; errors inside a referenced search point at the reference.
    /// </summary>
    public async Task<QueryValidationResult> ValidateAsync(
        string query, Guid workspaceId, IQueryBinder? binder, ISavedSearchQueries? savedSearches, SecurityPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parsed = QueryParser.Parse(query, Limits);
        var result = ToResult(parsed);
        if (parsed.Ast is not { } ast)
        {
            return result;
        }

        var expansion = SavedSearchExpansion.Unchanged(ast);
        if (savedSearches is not null)
        {
            expansion = await savedSearches.ExpandAsync(new SavedSearchExpansionRequest(workspaceId, ast, caller), cancellationToken).ConfigureAwait(false);
            if (!expansion.Success)
            {
                return Invalid(result, expansion.Errors);
            }
        }

        if (binder is null)
        {
            return result;
        }

        var errors = await binder.BindAsync(workspaceId, expansion.Ast, cancellationToken).ConfigureAwait(false);
        return errors.Count == 0 ? result : Invalid(result, expansion.Annotate(errors));
    }

    private static QueryValidationResult Invalid(QueryValidationResult result, IReadOnlyList<QueryDiagnostic> errors) =>
        result with { Valid = false, Ast = null, Normalized = null, Errors = [.. errors.Select(ToContract)] };

    private static QueryValidationResult ToResult(QueryParseResult result) => new()
    {
        Valid = result.Success,
        AstVersion = QueryNode.AstVersion,
        Ast = result.Ast is { } ast ? ToElement(ast) : null,
        Normalized = result.Ast is { } tree ? QueryPrinter.Print(tree) : null,
        Errors = [.. result.Errors.Select(ToContract)],
        Warnings = [.. result.Warnings.Select(ToContract)],
    };

    private static JsonElement ToElement(QueryNode ast)
    {
        using var document = JsonDocument.Parse(QueryAstJson.Serialize(ast));
        return document.RootElement.Clone();
    }

    private static QueryValidationDiagnostic ToContract(QueryDiagnostic d) =>
        new(d.Code, d.Message, new TextSpan(d.Span.Start, d.Span.End), d.Expected);
}
