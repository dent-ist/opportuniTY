using System.Text.Json.Nodes;

using Opportunity.Core.Fields;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Search.Querying;

/// <summary>
/// Plans a parsed query (ADR-008 AST) into the OpenSearch clause for the user's part of a search. This is the seam the
/// search planner (E07-T07, <see cref="SearchQueryPlanner"/>) implements: field resolution against the workspace's
/// FieldDefinitions, proximity, wildcard policy and cost limits all live behind it.
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
/// <param name="Zone">ADR-008 R8: the zone date-only literals on DateTime fields are read in; null means UTC.</param>
/// <param name="FieldOptions">Field-binding options of the search (E09-T02 custodian expansion); null means none.</param>
public sealed record SearchTranslationContext(
    Guid WorkspaceId, int ProjectionGeneration, QueryLimits Limits, TimeZoneInfo? Zone = null, SearchFieldOptions? FieldOptions = null);

/// <param name="Query">The user clause; null when <see cref="Errors"/> is not empty.</param>
/// <param name="Errors">User-facing, positioned errors (e.g. <c>UNKNOWN_FIELD</c>); never a silent match-none.</param>
/// <param name="QueryClass"><c>simple</c> or <c>complex</c>, the <c>opportunity.search.class</c> metric attribute.</param>
public sealed record SearchTranslation(JsonObject? Query, IReadOnlyList<QueryDiagnostic> Errors, string QueryClass)
{
    public const string Simple = "simple";
    public const string Complex = "complex";

    public bool Success => Query is not null && Errors.Count == 0;

    /// <summary>
    /// Spans of the wildcard terms whose expansion OpenSearch bounds (inside <c>W/n</c>, ADR-008 R7). When the search
    /// fails because an expansion exceeded the clause limit, the service reports <c>WILDCARD_TOO_BROAD</c> at these.
    /// </summary>
    public IReadOnlyList<SourceSpan> BoundedExpansions { get; init; } = [];

    public static SearchTranslation Ok(JsonObject query, string queryClass) => new(query, [], queryClass);

    public static SearchTranslation Failed(IReadOnlyList<QueryDiagnostic> errors) => new(null, errors, Simple);
}

/// <summary>Binder/planner diagnostic codes (ADR-008: stable, add-only).</summary>
public static class SearchQueryErrorCodes
{
    /// <summary>No field of the workspace has this query name; <see cref="QueryDiagnostic.Expected"/> holds suggestions.</summary>
    public const string UnknownField = "UNKNOWN_FIELD";

    /// <summary>Too few literal characters before the first wildcard for the field (ADR-008 R7).</summary>
    public const string LeadingWildcard = "LEADING_WILDCARD";

    /// <summary>The value does not fit the field's type (date, number, boolean, choice name, user id).</summary>
    public const string InvalidFieldValue = "INVALID_FIELD_VALUE";

    /// <summary>The field lacks the capability the construct needs (range, wildcard, exists, proximity, search at all).</summary>
    public const string UnsupportedForField = "UNSUPPORTED_FOR_FIELD";

    /// <summary>A term or phrase that analysis reduces to no searchable token (e.g. only punctuation).</summary>
    public const string NoSearchableTerms = "NO_SEARCHABLE_TERMS";

    /// <summary>More leaf clauses after field expansion than <see cref="QueryLimits.MaxClauses"/>.</summary>
    public const string TooManyClauses = "TOO_MANY_CLAUSES";

    /// <summary>A wildcard inside <c>W/n</c> matches more index terms than the expansion limit; never truncated (R7).</summary>
    public const string WildcardTooBroad = "WILDCARD_TOO_BROAD";

    /// <summary>The search did not finish within the server-side timeout; partial results are never served as complete.</summary>
    public const string QueryTimeout = "QUERY_TIMEOUT";
}
