using System.Text.Json.Nodes;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.Search.Querying;

/// <summary>
/// Plans a parsed query (ADR-008 AST) into the OpenSearch clause for the user's part of a search. This is the seam the
/// search planner (E07-T07) replaces: field resolution against FieldDefinitions, proximity, wildcard policy and cost
/// limits all live behind it. <see cref="BasicSearchQueryTranslator"/> is the first-slice implementation.
/// <para>
/// The search service owns everything security-relevant: the returned clause is placed only inside the outer
/// <c>bool.must</c>, below the workspace and security filters, so whatever a translator returns can narrow a search
/// but never widen it past the workspace (ADR-006 R7). Translators must not try to address <c>workspaceId</c>,
/// <c>securityTags</c>, <c>documentId</c> or <c>projectionVersion</c> (<see cref="ProjectionFields.NotAddressable"/>):
/// those answer as unknown fields.
/// </para>
/// </summary>
public interface ISearchQueryTranslator
{
    ValueTask<SearchTranslation> TranslateAsync(QueryNode ast, SearchTranslationContext context, CancellationToken cancellationToken = default);
}

/// <summary>What a translator may consult. <see cref="ProjectionGeneration"/> selects the mapping the clause must fit.</summary>
public sealed record SearchTranslationContext(Guid WorkspaceId, int ProjectionGeneration, QueryLimits Limits);

/// <param name="Query">The user clause; null when <see cref="Errors"/> is not empty.</param>
/// <param name="Errors">User-facing, positioned errors (e.g. <c>UNKNOWN_FIELD</c>); never a silent match-none.</param>
/// <param name="QueryClass"><c>simple</c> or <c>complex</c>, the <c>opportunity.search.class</c> metric attribute.</param>
public sealed record SearchTranslation(JsonObject? Query, IReadOnlyList<QueryDiagnostic> Errors, string QueryClass)
{
    public const string Simple = "simple";
    public const string Complex = "complex";

    public bool Success => Query is not null && Errors.Count == 0;

    public static SearchTranslation Ok(JsonObject query, string queryClass) => new(query, [], queryClass);

    public static SearchTranslation Failed(IReadOnlyList<QueryDiagnostic> errors) => new(null, errors, Simple);
}

/// <summary>Binder/planner diagnostic codes of the first slice (ADR-008: stable, add-only).</summary>
public static class SearchQueryErrorCodes
{
    public const string UnknownField = "UNKNOWN_FIELD";
    public const string LeadingWildcard = "LEADING_WILDCARD";
    public const string InvalidFieldValue = "INVALID_FIELD_VALUE";
    public const string UnsupportedForField = "UNSUPPORTED_FOR_FIELD";
}
