namespace Opportunity.Core.QueryLanguage;

/// <summary>
/// Stable diagnostic codes of the query language (ADR-008). Part of the public API (validate endpoint, query bar):
/// add, never rename. Codes produced later in the pipeline (<c>UNKNOWN_FIELD</c>, <c>LEADING_WILDCARD</c>,
/// <c>WILDCARD_TOO_BROAD</c>, <c>QUERY_TIMEOUT</c>) belong to the binder/planner.
/// </summary>
public static class QueryErrorCodes
{
    /// <summary>Unexpected token; <see cref="QueryDiagnostic.Expected"/> lists what would be valid there.</summary>
    public const string SyntaxError = "SYNTAX_ERROR";

    /// <summary>Unmatched <c>(</c> or <c>)</c>.</summary>
    public const string UnbalancedParenthesis = "UNBALANCED_PARENTHESIS";

    /// <summary>A phrase without its closing quote.</summary>
    public const string UnterminatedPhrase = "UNTERMINATED_PHRASE";

    /// <summary><c>""</c> or a phrase of only whitespace.</summary>
    public const string EmptyPhrase = "EMPTY_PHRASE";

    /// <summary>A <c>\</c> at the end of the query.</summary>
    public const string DanglingEscape = "DANGLING_ESCAPE";

    /// <summary>A leading <c>-</c> or <c>+</c> (R4): use <c>NOT</c>/<c>AND</c>, or escape it.</summary>
    public const string ReservedPrefix = "RESERVED_PREFIX";

    /// <summary>Syntax planned for later (R4, R6, §7): <c>PRE/n</c>, <c>!</c>, <c>~</c>, <c>^</c>, <c>W/s</c>, <c>W/p</c>, chained or nested proximity, wildcards in phrases.</summary>
    public const string UnsupportedSyntax = "UNSUPPORTED_SYNTAX";

    /// <summary>A <c>W/n</c> operand that can never be valid, e.g. a field filter (R6: put the field around the proximity).</summary>
    public const string InvalidProximityOperand = "INVALID_PROXIMITY_OPERAND";

    /// <summary><c>n</c> of <c>W/n</c> outside 1–<see cref="QueryLimits.MaxProximityDistance"/>.</summary>
    public const string ProximityDistanceOutOfRange = "PROXIMITY_DISTANCE_OUT_OF_RANGE";

    /// <summary>A field inside a field value: <c>a:b:c</c> or <c>a:(b:c)</c>.</summary>
    public const string NestedField = "NESTED_FIELD";

    /// <summary>A malformed range (wildcard bound, missing field).</summary>
    public const string InvalidRange = "INVALID_RANGE";

    public const string QueryTooLong = "QUERY_TOO_LONG";
    public const string QueryTooDeep = "QUERY_TOO_DEEP";
    public const string TooManyNodes = "TOO_MANY_NODES";
    public const string TooManyTerms = "TOO_MANY_TERMS";
    public const string TooManyWildcards = "TOO_MANY_WILDCARDS";
}

public static class QueryWarningCodes
{
    /// <summary><c>and</c>/<c>or</c>/<c>not</c> in lower or mixed case is a search term, not an operator (R1).</summary>
    public const string LowercaseOperator = "LOWERCASE_OPERATOR";
}

/// <summary>A positioned error or warning. <see cref="Expected"/> names the tokens that would have been valid at the error.</summary>
public sealed record QueryDiagnostic(string Code, string Message, SourceSpan Span, IReadOnlyList<string> Expected)
{
    public QueryDiagnostic(string code, string message, SourceSpan span)
        : this(code, message, span, [])
    {
    }
}

/// <summary>Token names used in <see cref="QueryDiagnostic.Expected"/>.</summary>
public static class QueryTokens
{
    public const string Term = "term";
    public const string Phrase = "phrase";
    public const string Field = "field:";
    public const string OpenParen = "(";
    public const string CloseParen = ")";
    public const string Not = "NOT";
    public const string And = "AND";
    public const string Or = "OR";
    public const string Proximity = "W/n";
    public const string To = "TO";
    public const string CloseInclusive = "]";
    public const string CloseExclusive = "}";
    public const string OpenRange = "[";
    public const string OpenExclusiveRange = "{";
    public const string Star = "*";
    public const string Quote = "\"";
    public const string EndOfInput = "end of input";
}

/// <summary>
/// Query complexity limits (ADR-008 §5). The parser enforces length, nodes, depth, terms, wildcard terms and the
/// proximity distance; <see cref="MinWildcardPrefix"/>, <see cref="MaxWildcardExpansion"/>, <see cref="MaxClauses"/> and
/// <see cref="MaxProximityWildcards"/> depend on the fields and the index, so the binder and planner enforce them.
/// </summary>
public sealed record QueryLimits
{
    public static QueryLimits Default { get; } = new();

    /// <summary>UTF-16 code units.</summary>
    public int MaxLength { get; init; } = 10_000;

    public int MaxNodes { get; init; } = 1_024;

    /// <summary>Applies to both the AST depth and the nesting of parentheses and <c>NOT</c> in the text.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Leaf clauses: terms, phrases, wildcards, ranges and exists checks.</summary>
    public int MaxTerms { get; init; } = 512;

    /// <summary>Wildcard terms per query; each may expand to <see cref="MaxWildcardExpansion"/> index terms.</summary>
    public int MaxWildcardTerms { get; init; } = 32;

    public int MaxProximityDistance { get; init; } = 1_000;

    /// <summary>Per wildcard term inside proximity (<c>Search:MaxWildcardExpansion</c>); enforced by the planner, never truncated.</summary>
    public int MaxWildcardExpansion { get; init; } = 4_096;

    /// <summary>Literal characters before the first wildcard on full-text fields (<c>Search:MinWildcardPrefix</c>); enforced by the binder.</summary>
    public int MinWildcardPrefix { get; init; } = 3;

    /// <summary>
    /// Leaf clauses after binding (<c>Search:MaxQueryClauses</c>): field expansion (e.g. custodian → All Custodians) and
    /// choice-name resolution can multiply the leaves the parser counted; enforced by the planner.
    /// </summary>
    public int MaxClauses { get; init; } = 1_024;

    /// <summary>
    /// Wildcard terms inside <c>W/n</c> per query (<c>Search:MaxProximityWildcards</c>). Each one expands to up to
    /// <see cref="MaxWildcardExpansion"/> span terms, so this bounds the span work of one query; enforced by the planner.
    /// </summary>
    public int MaxProximityWildcards { get; init; } = 8;
}

/// <summary>Outcome of <see cref="QueryParser.Parse(string, QueryLimits?)"/>: an AST, or at least one positioned error.</summary>
public sealed record QueryParseResult(QueryNode? Ast, IReadOnlyList<QueryDiagnostic> Errors, IReadOnlyList<QueryDiagnostic> Warnings)
{
    public bool Success => Ast is not null;
}
