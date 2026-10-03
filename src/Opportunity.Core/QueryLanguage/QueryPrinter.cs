using System.Globalization;
using System.Text;

namespace Opportunity.Core.QueryLanguage;

/// <summary>
/// Canonical printer (R16): upper-case operators, explicit <c>AND</c>, minimal escaping, and parentheses exactly where
/// the tree needs them, so that <c>Parse(Print(ast))</c> equals <c>ast</c> (ignoring spans). The UI echoes this text
/// as the normalized interpretation (R3), and stored saved-search text is regenerated with it.
/// </summary>
public static class QueryPrinter
{
    // R2 binding strength, lowest first.
    private const int OrLevel = 1;
    private const int AndLevel = 2;
    private const int ProximityLevel = 3;
    private const int NotLevel = 4;
    private const int FieldLevel = 5;
    private const int PrimaryLevel = 6;

    public static string Print(QueryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var builder = new StringBuilder();
        Write(builder, node, 0);
        return builder.ToString();
    }

    /// <summary>Escapes a term so it lexes back as one word with exactly this text.</summary>
    public static string EscapeTerm(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var b = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (NeedsEscape(c, i) || c is '*' or '?')
            {
                b.Append('\\');
            }

            b.Append(c);
        }

        return ProtectOperator(text, b);
    }

    /// <summary>A phrase in double quotes; <c>"</c>, <c>\</c>, <c>*</c> and <c>?</c> are escaped.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var b = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            if (c is '"' or '\\' or '*' or '?')
            {
                b.Append('\\');
            }

            b.Append(c);
        }

        return b.Append('"').ToString();
    }

    private static string EscapePattern(string pattern)
    {
        var b = new StringBuilder(pattern.Length + 4);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                b.Append(c).Append(pattern[++i]);
                continue;
            }

            if (NeedsEscape(c, i))
            {
                b.Append('\\');
            }

            b.Append(c);
        }

        return b.ToString();
    }

    private static bool NeedsEscape(char c, int index) =>
        QueryLexer.IsReserved(c) || char.IsWhiteSpace(c) || (index == 0 && c is '-' or '+');

    /// <summary>A word that would lex as an operator (<c>AND</c>, <c>W/5</c>, <c>PRE/3</c>) gets its first character escaped.</summary>
    private static string ProtectOperator(string text, StringBuilder escaped)
    {
        if (escaped.Length == text.Length
            && (QueryLexer.IsOperatorWord(text) || QueryLexer.IsProximityOperator(text) || QueryLexer.IsUnsupportedOperator(text)))
        {
            escaped.Insert(0, '\\');
        }

        return escaped.ToString();
    }

    private static int Level(QueryNode node) => node switch
    {
        OrNode => OrLevel,
        AndNode => AndLevel,
        ProximityNode => ProximityLevel,
        NotNode => NotLevel,
        FieldNode => FieldLevel,
        _ => PrimaryLevel,
    };

    private static void Write(StringBuilder b, QueryNode node, int minimumLevel)
    {
        var group = Level(node) < minimumLevel;
        if (group)
        {
            b.Append('(');
        }

        switch (node)
        {
            case MatchAllNode:
                break;
            case TermNode term:
                b.Append(EscapeTerm(term.Text));
                if (term.RootExpand)
                {
                    b.Append('!');
                }

                break;
            case PhraseNode phrase:
                b.Append(Quote(phrase.Text));
                break;
            case WildcardNode wildcard:
                b.Append(EscapePattern(wildcard.Pattern));
                break;
            case ExistsNode:
                b.Append('*');
                break;
            case RangeNode range:
                b.Append(range.Lower is { Inclusive: false } ? '{' : '[')
                    .Append(Bound(range.Lower))
                    .Append(" TO ")
                    .Append(Bound(range.Upper))
                    .Append(range.Upper is { Inclusive: false } ? '}' : ']');
                break;
            case FieldNode field:
                b.Append(field.Name).Append(':');

                // field:* is Exists; a lone-star wildcard under a field needs a group to stay a wildcard.
                Write(b, field.Child, field.Child is WildcardNode { Pattern: "*" } ? PrimaryLevel + 1 : PrimaryLevel);
                break;
            case NotNode not:
                b.Append("NOT ");
                Write(b, not.Child, NotLevel);
                break;
            case ProximityNode proximity:
                Write(b, proximity.Left, ProximityLevel + 1);
                b.Append(' ')
                    .Append(proximity.Ordered ? "PRE/" : "W/")
                    .Append(proximity.Distance.ToString(CultureInfo.InvariantCulture))
                    .Append(' ');
                Write(b, proximity.Right, ProximityLevel + 1);
                break;
            case AndNode and:
                Join(b, and.Children, " AND ", AndLevel + 1);
                break;
            case OrNode or:
                Join(b, or.Children, " OR ", OrLevel + 1);
                break;
            default:
                throw new ArgumentException($"Unsupported node {node.GetType().Name}.", nameof(node));
        }

        if (group)
        {
            b.Append(')');
        }
    }

    private static void Join(StringBuilder b, IReadOnlyList<QueryNode> children, string separator, int childLevel)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (i > 0)
            {
                b.Append(separator);
            }

            Write(b, children[i], childLevel);
        }
    }

    /// <summary>A bound that is not a plain word is quoted (<c>"2025-03-01T09:00:00+01:00"</c>) rather than escaped.</summary>
    private static string Bound(RangeBound? bound)
    {
        if (bound is null)
        {
            return "*";
        }

        return bound.Value.Length > 0 && EscapeTerm(bound.Value) is var bare && bare == bound.Value ? bare : Quote(bound.Value);
    }
}
