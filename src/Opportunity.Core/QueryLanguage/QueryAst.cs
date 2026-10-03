namespace Opportunity.Core.QueryLanguage;

/// <summary>Half-open range <c>[Start, End)</c> in UTF-16 code units of the original query text (ADR-008 R13).</summary>
public readonly record struct SourceSpan(int Start, int End)
{
    public int Length => End - Start;

    public static SourceSpan Cover(SourceSpan first, SourceSpan last) => new(first.Start, last.End);

    public override string ToString() => $"[{Start}, {End})";
}

/// <summary>AST node kinds of ADR-008 R13. <c>Fuzzy</c> and <c>Regex</c> are reserved (R14) and not produced in M1.</summary>
public enum QueryNodeKind
{
    MatchAll,
    Or,
    And,
    Not,
    Proximity,
    Field,
    Term,
    Phrase,
    Wildcard,
    Range,
    Exists,
}

/// <summary>
/// Syntax-only query AST (ADR-008 §4): produced by <see cref="QueryParser"/>, workspace-independent, and the input of
/// the binder/planner. Compare trees with <see cref="QueryAstComparer"/> (spans optional), not record equality.
/// </summary>
public abstract record QueryNode(SourceSpan Span)
{
    /// <summary>Version of the canonical AST JSON schema; any shape change bumps it (R14).</summary>
    public const int AstVersion = 1;

    public abstract QueryNodeKind Kind { get; }

    public override string ToString() => QueryPrinter.Print(this);
}

/// <summary>Blank query: every document in scope (R5).</summary>
public sealed record MatchAllNode(SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.MatchAll;
}

public sealed record OrNode(IReadOnlyList<QueryNode> Children, SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Or;
}

/// <summary>Explicit or implicit (juxtaposition) AND (R3); the AST does not distinguish them.</summary>
public sealed record AndNode(IReadOnlyList<QueryNode> Children, SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.And;
}

public sealed record NotNode(QueryNode Child, SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Not;
}

/// <summary>
/// <c>left W/n right</c>: both within <see cref="Distance"/> words, either order, at most n−1 intervening words
/// (ADR-008 §2). <see cref="Ordered"/> is the reserved <c>PRE/n</c> extension point (R14); the M1 parser never sets it.
/// </summary>
public sealed record ProximityNode(QueryNode Left, QueryNode Right, int Distance, SourceSpan Span, bool Ordered = false) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Proximity;
}

/// <summary><c>name:child</c>. <see cref="Name"/> is as written; the binder resolves it case-insensitively (R10–R11).</summary>
public sealed record FieldNode(string Name, QueryNode Child, SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Field;
}

/// <summary>
/// A bare term, unescaped. <see cref="RootExpand"/> is the reserved <c>!</c> extension point (R14); the M1 parser never
/// sets it.
/// </summary>
public sealed record TermNode(string Text, SourceSpan Span, bool RootExpand = false) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Term;
}

/// <summary>A quoted phrase, unescaped.</summary>
public sealed record PhraseNode(string Text, SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Phrase;
}

/// <summary>
/// A term with at least one unescaped <c>*</c> (zero or more characters) or <c>?</c> (exactly one) (ADR-008 R7).
/// <see cref="Pattern"/> uses the OpenSearch <c>wildcard</c> escaping: a literal <c>*</c>, <c>?</c> or <c>\</c> is
/// written <c>\*</c>, <c>\?</c>, <c>\\</c>; every other character is literal.
/// </summary>
public sealed record WildcardNode(string Pattern, SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Wildcard;

    /// <summary>Literal characters before the first wildcard (R7: ≥ 3 on full-text fields, ≥ 1 on keyword fields).</summary>
    public int LiteralPrefixLength
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Pattern.Length; i++)
            {
                var c = Pattern[i];
                if (c is '*' or '?')
                {
                    return count;
                }

                if (c == '\\')
                {
                    i++;
                }

                count++;
            }

            return count;
        }
    }
}

/// <summary>One end of a range. A <c>null</c> bound on <see cref="RangeNode"/> means open (<c>*</c>).</summary>
public sealed record RangeBound(string Value, bool Inclusive);

/// <summary><c>[a TO b]</c> inclusive, <c>{a TO b}</c> exclusive, mixable. Only valid under a field.</summary>
public sealed record RangeNode(RangeBound? Lower, RangeBound? Upper, SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Range;
}

/// <summary><c>field:*</c>: the field has a value. Only valid under a field.</summary>
public sealed record ExistsNode(SourceSpan Span) : QueryNode(Span)
{
    public override QueryNodeKind Kind => QueryNodeKind.Exists;
}
