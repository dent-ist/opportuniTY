using System.Text.Json;

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
    public async Task<QueryValidationResult> ValidateAsync(
        string query, Guid workspaceId, IQueryBinder? binder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parsed = QueryParser.Parse(query, Limits);
        var result = ToResult(parsed);
        if (parsed.Ast is not { } ast || binder is null)
        {
            return result;
        }

        var errors = await binder.BindAsync(workspaceId, ast, cancellationToken).ConfigureAwait(false);
        return errors.Count == 0
            ? result
            : result with { Valid = false, Ast = null, Normalized = null, Errors = [.. errors.Select(ToContract)] };
    }

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
