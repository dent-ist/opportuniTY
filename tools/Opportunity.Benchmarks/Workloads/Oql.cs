using System.Globalization;
using System.Text;

namespace Opportunity.Benchmarks.Workloads;

/// <summary>
/// The ADR-008 M1 subset as a small AST the query generator builds, prints and evaluates. It mirrors the node kinds of
/// ADR-008 R13 (without spans): the benchmark never parses user text, it only emits canonical OQL.
/// </summary>
public abstract record OqlNode
{
    public override string ToString() => OqlPrinter.Print(this);
}

/// <summary>Bare term on the default field (or the value of a field filter). Multi-token terms are phrases (ADR-008 §2).</summary>
public sealed record OqlTerm(string Text) : OqlNode;

public sealed record OqlPhrase(string Text) : OqlNode;

/// <summary><c>*</c> = zero or more characters, <c>?</c> = exactly one (ADR-008 R7).</summary>
public sealed record OqlWildcard(string Pattern) : OqlNode;

/// <summary>Inclusive bounds; null = open (<c>*</c>).</summary>
public sealed record OqlRange(string? Lower, string? Upper) : OqlNode;

public sealed record OqlField(string Name, OqlNode Child) : OqlNode;

public sealed record OqlAnd(IReadOnlyList<OqlNode> Children, bool Explicit = true) : OqlNode;

public sealed record OqlOr(IReadOnlyList<OqlNode> Children) : OqlNode;

public sealed record OqlNot(OqlNode Child) : OqlNode;

/// <summary><c>left W/n right</c>: within n words, either order, at most n-1 intervening words (ADR-008 §2).</summary>
public sealed record OqlProximity(OqlNode Left, OqlNode Right, int Distance) : OqlNode;

/// <summary>Canonical OQL text: upper-case operators, minimal parentheses per ADR-008 R2 precedence.</summary>
public static class OqlPrinter
{
    // R2, lowest first: OR < AND < W/n < NOT < field < primary.
    private const int OrLevel = 1;
    private const int AndLevel = 2;
    private const int ProximityLevel = 3;
    private const int NotLevel = 4;
    private const int PrimaryLevel = 6;

    public static string Print(OqlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var builder = new StringBuilder();
        Write(builder, node, 0);
        return builder.ToString();
    }

    /// <summary>A term that needs no quoting: letters/digits with inner hyphens, apostrophes or dots, not an operator.</summary>
    public static bool IsBareTerm(string text) =>
        text.Length > 0
        && char.IsLetterOrDigit(text[0])
        && text.All(c => char.IsLetterOrDigit(c) || c is '-' or '\'' or '.')
        && !IsReservedWord(text)
        && !IsProximityOperator(text);

    private static bool IsReservedWord(string text) => text is "AND" or "OR" or "NOT" or "TO";

    private static bool IsProximityOperator(string text) =>
        text.Length > 2 && (text[0] is 'w' or 'W') && text[1] == '/' && text[2..].All(char.IsAsciiDigit);

    private static int Level(OqlNode node) => node switch
    {
        OqlOr => OrLevel,
        OqlAnd => AndLevel,
        OqlProximity => ProximityLevel,
        OqlNot => NotLevel,
        _ => PrimaryLevel,
    };

    private static void Write(StringBuilder b, OqlNode node, int minimumLevel)
    {
        bool group = Level(node) < minimumLevel;
        if (group)
        {
            b.Append('(');
        }

        switch (node)
        {
            case OqlTerm term:
                b.Append(IsBareTerm(term.Text) ? term.Text : Quote(term.Text));
                break;
            case OqlPhrase phrase:
                b.Append(Quote(phrase.Text));
                break;
            case OqlWildcard wildcard:
                b.Append(EscapeWildcard(wildcard.Pattern));
                break;
            case OqlRange range:
                b.Append('[').Append(Bound(range.Lower)).Append(" TO ").Append(Bound(range.Upper)).Append(']');
                break;
            case OqlField field:
                b.Append(field.Name).Append(':');
                Write(b, field.Child, PrimaryLevel);
                break;
            case OqlAnd and:
                Join(b, and.Children, and.Explicit ? " AND " : " ", AndLevel);
                break;
            case OqlOr or:
                Join(b, or.Children, " OR ", OrLevel + 1);
                break;
            case OqlNot not:
                b.Append("NOT ");
                Write(b, not.Child, NotLevel);
                break;
            case OqlProximity proximity:
                Write(b, proximity.Left, NotLevel + 1);
                b.Append(CultureInfo.InvariantCulture, $" W/{proximity.Distance} ");
                Write(b, proximity.Right, NotLevel + 1);
                break;
            default:
                throw new ArgumentException($"Unsupported node {node.GetType().Name}.", nameof(node));
        }

        if (group)
        {
            b.Append(')');
        }
    }

    private static void Join(StringBuilder b, IReadOnlyList<OqlNode> children, string separator, int childLevel)
    {
        for (int i = 0; i < children.Count; i++)
        {
            if (i > 0)
            {
                // Juxtaposition before NOT reads badly ("a NOT b"); spell the AND out.
                b.Append(separator == " " && children[i] is OqlNot ? " AND " : separator);
            }

            Write(b, children[i], childLevel);
        }
    }

    private static string Bound(string? value) => value is null ? "*" : IsBareTerm(value) ? value : Quote(value);

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string EscapeWildcard(string pattern)
    {
        var b = new StringBuilder(pattern.Length + 4);
        foreach (char c in pattern)
        {
            if (c is '(' or ')' or '"' or ':' or '[' or ']' or '{' or '}' or '\\' or '!' or '~' or '^' or ' ')
            {
                b.Append('\\');
            }

            b.Append(c);
        }

        return b.ToString();
    }
}

/// <summary>Search options that are not syntax (ADR-008 §7): expansion, grid sort and facets.</summary>
public sealed record SearchOptions
{
    public static SearchOptions Default { get; } = new();

    /// <summary>null, "family" or "duplicates".</summary>
    public string? Expand { get; init; }

    public IReadOnlyList<SortKey> Sort { get; init; } = [];

    public IReadOnlyList<string> Facets { get; init; } = [];
}

public sealed record SortKey(string Field, string Direction);

/// <summary>
/// Executable form of the Simple/Complex definition (docs/benchmarks/query-taxonomy.md §2). A query is Simple when its
/// content is at most two terms or exactly one phrase, joined only by AND, with at most two metadata filters, no
/// wildcard, proximity, OR, NOT or coding-field reference, no expansion, no facets and the default sort; everything
/// else is Complex.
/// </summary>
public static class QueryClassifier
{
    public const int MaxSimpleTerms = 2;
    public const int MaxSimpleFilters = 2;

    public static GateClass Classify(OqlNode query, SearchOptions options, IReadOnlySet<string> codingFields)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codingFields);
        if (options.Expand is not null || options.Facets.Count > 0 || options.Sort.Count > 0)
        {
            return GateClass.Complex;
        }

        int terms = 0, phrases = 0, filters = 0;
        foreach (OqlNode node in Conjuncts(query))
        {
            switch (node)
            {
                case OqlTerm:
                    terms++;
                    break;
                case OqlPhrase:
                    phrases++;
                    break;
                case OqlField { Child: OqlTerm or OqlPhrase or OqlRange } field when !codingFields.Contains(field.Name):
                    filters++;
                    break;
                default:
                    return GateClass.Complex;
            }
        }

        bool content = (phrases == 0 && terms <= MaxSimpleTerms) || (phrases == 1 && terms == 0);
        return content && filters <= MaxSimpleFilters ? GateClass.Simple : GateClass.Complex;
    }

    /// <summary>AND is associative: nested conjunctions count as one flat list of clauses.</summary>
    private static IEnumerable<OqlNode> Conjuncts(OqlNode node) => node is OqlAnd and ? and.Children.SelectMany(Conjuncts) : [node];
}
