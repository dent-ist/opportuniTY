using System.Text.Json;

using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Application.Search;

/// <summary>
/// The validate use case behind the query bar (E07-T06): parse only, no workspace binding yet (binding errors such as
/// <c>UNKNOWN_FIELD</c> arrive with the planner, E07-T07). Raw search DSL is never accepted: the input is query text.
/// </summary>
public sealed class QueryValidator(QueryLimits limits)
{
    public QueryLimits Limits { get; } = limits;

    public QueryValidationResult Validate(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = QueryParser.Parse(query, Limits);
        return new QueryValidationResult
        {
            Valid = result.Success,
            AstVersion = QueryNode.AstVersion,
            Ast = result.Ast is { } ast ? ToElement(ast) : null,
            Normalized = result.Ast is { } tree ? QueryPrinter.Print(tree) : null,
            Errors = [.. result.Errors.Select(ToContract)],
            Warnings = [.. result.Warnings.Select(ToContract)],
        };
    }

    private static JsonElement ToElement(QueryNode ast)
    {
        using var document = JsonDocument.Parse(QueryAstJson.Serialize(ast));
        return document.RootElement.Clone();
    }

    private static QueryValidationDiagnostic ToContract(QueryDiagnostic d) =>
        new(d.Code, d.Message, new TextSpan(d.Span.Start, d.Span.End), d.Expected);
}
