using System.Globalization;
using System.Text;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.QueryLanguage;

/// <summary>Compact S-expression rendering of an AST (no spans) so grammar tests read as one line each.</summary>
internal static class Sx
{
    public static string Of(QueryNode node)
    {
        var b = new StringBuilder();
        Write(b, node);
        return b.ToString();
    }

    public static string Parse(string query)
    {
        var result = QueryParser.Parse(query);
        return result.Ast is { } ast ? Of(ast) : $"error {result.Errors[0].Code} {result.Errors[0].Span}";
    }

    private static void Write(StringBuilder b, QueryNode node)
    {
        switch (node)
        {
            case MatchAllNode:
                b.Append("(all)");
                break;
            case TermNode t:
                b.Append("(term ").Append(t.Text).Append(')');
                break;
            case PhraseNode p:
                b.Append("(phrase ").Append(p.Text).Append(')');
                break;
            case WildcardNode w:
                b.Append("(wildcard ").Append(w.Pattern).Append(')');
                break;
            case ExistsNode:
                b.Append("(exists)");
                break;
            case RangeNode r:
                b.Append("(range ").Append(Lower(r.Lower)).Append(' ').Append(Upper(r.Upper)).Append(')');
                break;
            case FieldNode f:
                b.Append("(field ").Append(f.Name).Append(' ');
                Write(b, f.Child);
                b.Append(')');
                break;
            case NotNode n:
                b.Append("(not ");
                Write(b, n.Child);
                b.Append(')');
                break;
            case ProximityNode x:
                b.Append("(w/").Append(x.Distance.ToString(CultureInfo.InvariantCulture)).Append(' ');
                Write(b, x.Left);
                b.Append(' ');
                Write(b, x.Right);
                b.Append(')');
                break;
            case AndNode a:
                List(b, "and", a.Children);
                break;
            case OrNode o:
                List(b, "or", o.Children);
                break;
            default:
                throw new ArgumentException(node.GetType().Name);
        }
    }

    private static void List(StringBuilder b, string name, IReadOnlyList<QueryNode> children)
    {
        b.Append('(').Append(name);
        foreach (var child in children)
        {
            b.Append(' ');
            Write(b, child);
        }

        b.Append(')');
    }

    private static string Lower(RangeBound? bound) =>
        bound is null ? "*" : (bound.Inclusive ? "[" : "{") + bound.Value;

    private static string Upper(RangeBound? bound) =>
        bound is null ? "*" : bound.Value + (bound.Inclusive ? "]" : "}");
}
