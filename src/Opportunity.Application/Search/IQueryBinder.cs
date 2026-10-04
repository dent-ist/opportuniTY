using Opportunity.Core.QueryLanguage;

namespace Opportunity.Application.Search;

/// <summary>
/// Binds a parsed query against a workspace (ADR-008 §4, R15): field names, capabilities, choice names, value types and
/// planner limits. Implemented by the search planner (E07-T07) so the query bar sees the same positioned errors
/// (<c>UNKNOWN_FIELD</c>, <c>LEADING_WILDCARD</c>, …) a search would return.
/// </summary>
public interface IQueryBinder
{
    /// <summary>Positioned binding errors; empty when the query binds.</summary>
    Task<IReadOnlyList<QueryDiagnostic>> BindAsync(Guid workspaceId, QueryNode ast, CancellationToken cancellationToken = default);
}
