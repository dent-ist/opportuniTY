using System.Text;
using System.Text.RegularExpressions;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.Core.Highlighting;

public enum HitUnitKind
{
    Term,
    Phrase,
    Wildcard,
    Proximity,
}

/// <summary>One alternative of a hit unit operand: a word, a sequence of words (phrase) or a wildcard pattern.</summary>
public sealed record HitPattern
{
    private HitPattern(IReadOnlyList<string> words, Regex? wildcard, string prefix)
    {
        Words = words;
        Wildcard = wildcard;
        Prefix = prefix;
    }

    /// <summary>Folded tokens to match in sequence (one for a term); empty for a wildcard.</summary>
    public IReadOnlyList<string> Words { get; }

    /// <summary>The folded wildcard pattern as an anchored, non-backtracking expression; null for words.</summary>
    public Regex? Wildcard { get; }

    /// <summary>The folded literal characters before the first wildcard (a cheap pre-check); empty for words.</summary>
    public string Prefix { get; }

    public int Length => Wildcard is null ? Words.Count : 1;

    public static HitPattern ForWords(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentOutOfRangeException.ThrowIfZero(words.Count);
        return new HitPattern([.. words], null, string.Empty);
    }

    /// <summary>An OpenSearch-escaped wildcard pattern (<see cref="WildcardNode.Pattern"/>), folded first.</summary>
    public static HitPattern ForWildcard(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var folded = HitTextAnalyzer.FoldPattern(pattern);
        var regex = new StringBuilder("^");
        var prefix = new StringBuilder();
        var literal = true;
        for (var i = 0; i < folded.Length; i++)
        {
            var c = folded[i];
            if (c == '\\' && i + 1 < folded.Length)
            {
                c = folded[++i];
                regex.Append(Regex.Escape(c.ToString()));
            }
            else if (c is '*' or '?')
            {
                regex.Append(c == '*' ? ".*" : ".");
                literal = false;
                continue;
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }

            if (literal)
            {
                prefix.Append(c);
            }
        }

        regex.Append('$');
        return new HitPattern(
            [],
            new Regex(regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking),
            prefix.ToString());
    }

    public bool MatchesToken(string folded) => Wildcard is { } w
        ? folded.StartsWith(Prefix, StringComparison.Ordinal) && w.IsMatch(folded)
        : Words.Count == 1 && Words[0] == folded;
}

/// <summary>
/// A term-hit unit of ADR-008 R17: a term, phrase or wildcard, or a <c>W/n</c> proximity as a whole (its operands are
/// not units of their own). A term that analyzes into several words is a phrase of them.
/// </summary>
/// <param name="Label">What the reviewer reads: the term, the phrase text, the pattern or <c>a W/n b</c>.</param>
/// <param name="Left">The alternatives of the unit (the left operand of a proximity).</param>
/// <param name="Right">The right operand's alternatives (proximity only).</param>
/// <param name="Distance">The <c>n</c> of <c>W/n</c>: at most n − 1 words between the operands, either order.</param>
public sealed record HitUnit(
    HitUnitKind Kind,
    string Label,
    IReadOnlyList<HitPattern> Left,
    IReadOnlyList<HitPattern>? Right = null,
    int Distance = 0)
{
    /// <summary>Words a match can span before the proximity window: the longest alternative.</summary>
    public int MaxPatternLength => Math.Max(Left.Max(p => p.Length), Right?.Max(p => p.Length) ?? 0);
}

/// <summary>A unit that could not be built from an expression, with the position in the expression.</summary>
public sealed record HitUnitProblem(string Code, string Message, SourceSpan Span);

/// <summary>
/// Extracts the hit units of a query (ADR-008 R17) for highlighting the extracted text: every term, phrase and wildcard
/// on the default field (<c>text</c>, bare or written <c>text:</c>) and every proximity as one unit. Clauses under
/// <c>NOT</c> and on other fields are not highlighted. Units with the same meaning are kept once.
/// </summary>
public static class HitUnits
{
    public const string TextField = "text";

    public const string NoTermsCode = "NO_HIGHLIGHT_TERMS";
    public const string UnsupportedCode = "UNSUPPORTED_HIGHLIGHT_TERM";
    public const string LeadingWildcardCode = "LEADING_WILDCARD";

    /// <summary>Literal characters before the first wildcard on full-text fields (ADR-008 R7).</summary>
    public const int MinWildcardPrefix = 3;

    public static IReadOnlyList<HitUnit> FromQuery(QueryNode ast)
    {
        ArgumentNullException.ThrowIfNull(ast);
        var units = new List<HitUnit>();
        Collect(ast, units);
        return Distinct(units);
    }

    /// <summary>
    /// The units of one Highlight Set term: a word, <c>"phrase"</c>, wildcard, <c>a W/n b</c> or an <c>OR</c> of them.
    /// Several unquoted words without operators are one phrase (a term list line is highlighted as written).
    /// </summary>
    public static (IReadOnlyList<HitUnit> Units, IReadOnlyList<HitUnitProblem> Problems) FromHighlightTerm(string expression, QueryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var text = expression.Trim();
        var parsed = QueryParser.Parse(text, limits);
        if (parsed.Ast is not { } ast)
        {
            return ([], [.. parsed.Errors.Select(e => new HitUnitProblem(e.Code, e.Message, e.Span))]);
        }

        if (ast is AndNode and && and.Children.All(c => c is TermNode) && !ContainsOperatorWord(text, "AND"))
        {
            ast = new PhraseNode(string.Join(' ', and.Children.Cast<TermNode>().Select(t => t.Text)), ast.Span);
        }

        var problems = new List<HitUnitProblem>();
        Check(ast, problems, topLevel: true);
        if (problems.Count > 0)
        {
            return ([], problems);
        }

        var units = new List<HitUnit>();
        Collect(ast, units);
        return units.Count == 0
            ? ([], [new HitUnitProblem(NoTermsCode, "The term has no words to highlight.", ast.Span)])
            : (Distinct(units), []);
    }

    private static bool ContainsOperatorWord(string text, string word) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(word, StringComparer.Ordinal);

    private static void Check(QueryNode node, List<HitUnitProblem> problems, bool topLevel)
    {
        switch (node)
        {
            case OrNode alternatives when topLevel:
                foreach (var child in alternatives.Children)
                {
                    Check(child, problems, topLevel: false);
                }

                break;
            case TermNode or PhraseNode or ProximityNode:
                break;
            case WildcardNode w when w.LiteralPrefixLength < MinWildcardPrefix:
                problems.Add(new HitUnitProblem(LeadingWildcardCode,
                    $"A wildcard needs at least {MinWildcardPrefix} letters before the first * or ?.", w.Span));
                break;
            case WildcardNode:
                break;
            default:
                problems.Add(new HitUnitProblem(UnsupportedCode,
                    "A highlight term is a word, a \"phrase\", a wildcard (abc*), a W/n proximity or an OR of these.", node.Span));
                break;
        }
    }

    private static void Collect(QueryNode node, List<HitUnit> units)
    {
        switch (node)
        {
            case AndNode and:
                foreach (var child in and.Children)
                {
                    Collect(child, units);
                }

                break;
            case OrNode alternatives:
                foreach (var child in alternatives.Children)
                {
                    Collect(child, units);
                }

                break;
            case FieldNode field when string.Equals(field.Name, TextField, StringComparison.OrdinalIgnoreCase):
                Collect(field.Child, units);
                break;
            case ProximityNode p:
                var left = Operand(p.Left);
                var right = Operand(p.Right);
                if (left.Count > 0 && right.Count > 0)
                {
                    units.Add(new HitUnit(HitUnitKind.Proximity, $"{Label(p.Left)} W/{p.Distance} {Label(p.Right)}", left, right, p.Distance));
                }

                break;
            case TermNode or PhraseNode or WildcardNode:
                if (Leaf(node) is { } pattern)
                {
                    var kind = node switch
                    {
                        WildcardNode => HitUnitKind.Wildcard,
                        _ when pattern.Words.Count > 1 => HitUnitKind.Phrase,
                        _ => HitUnitKind.Term,
                    };
                    units.Add(new HitUnit(kind, Label(node), [pattern]));
                }

                break;

                // NOT, other fields, ranges, exists and match-all are not highlighted.
        }
    }

    private static List<HitPattern> Operand(QueryNode node) => node switch
    {
        OrNode alternatives => [.. alternatives.Children.SelectMany(Operand)],
        FieldNode field when string.Equals(field.Name, TextField, StringComparison.OrdinalIgnoreCase) => Operand(field.Child),
        TermNode or PhraseNode or WildcardNode => Leaf(node) is { } p ? [p] : [],
        _ => [],
    };

    private static HitPattern? Leaf(QueryNode node)
    {
        switch (node)
        {
            case WildcardNode w:
                return HitPattern.ForWildcard(w.Pattern);
            case TermNode t:
                var words = HitTextAnalyzer.Tokenize(t.Text).Select(x => x.Folded).ToList();
                return words.Count == 0 ? null : HitPattern.ForWords(words);
            case PhraseNode ph:
                var phrase = HitTextAnalyzer.Tokenize(ph.Text).Select(x => x.Folded).ToList();
                return phrase.Count == 0 ? null : HitPattern.ForWords(phrase);
            default:
                return null;
        }
    }

    private static string Label(QueryNode node) => node switch
    {
        TermNode t => t.Text,
        PhraseNode p => p.Text,
        WildcardNode w => w.Pattern,
        OrNode alternatives => "(" + string.Join(" OR ", alternatives.Children.Select(Label)) + ")",
        FieldNode f => Label(f.Child),
        _ => QueryPrinter.Print(node),
    };

    private static List<HitUnit> Distinct(List<HitUnit> units) =>
        [.. units.GroupBy(u => (u.Kind, Key: u.Label.ToLowerInvariant())).Select(g => g.First())];
}
