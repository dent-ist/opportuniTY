using System.Text.Encodings.Web;
using System.Text.Json;

namespace Opportunity.Core.QueryLanguage;

/// <summary>
/// Canonical AST JSON (ADR-008 R13, <c>astVersion: 1</c>): <c>{"astVersion":1,"root":node}</c>, each node
/// <c>{"kind", "span":{"start","end"}, …}</c> with camelCase kinds and properties in a fixed order. Golden files and the
/// validate endpoint use this form.
/// </summary>
public static class QueryAstJson
{
    public static string Serialize(QueryNode root, bool indented = false, bool includeSpans = true)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            Write(writer, root, includeSpans);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void Write(Utf8JsonWriter writer, QueryNode root, bool includeSpans = true)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(root);
        writer.WriteStartObject();
        writer.WriteNumber("astVersion", QueryNode.AstVersion);
        writer.WritePropertyName("root");
        WriteNode(writer, root, includeSpans);
        writer.WriteEndObject();
    }

    public static string KindName(QueryNodeKind kind) => kind switch
    {
        QueryNodeKind.MatchAll => "matchAll",
        QueryNodeKind.Or => "or",
        QueryNodeKind.And => "and",
        QueryNodeKind.Not => "not",
        QueryNodeKind.Proximity => "proximity",
        QueryNodeKind.Field => "field",
        QueryNodeKind.Term => "term",
        QueryNodeKind.Phrase => "phrase",
        QueryNodeKind.Wildcard => "wildcard",
        QueryNodeKind.Range => "range",
        QueryNodeKind.Exists => "exists",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static void WriteNode(Utf8JsonWriter w, QueryNode node, bool spans)
    {
        w.WriteStartObject();
        w.WriteString("kind", KindName(node.Kind));
        if (spans)
        {
            w.WriteStartObject("span");
            w.WriteNumber("start", node.Span.Start);
            w.WriteNumber("end", node.Span.End);
            w.WriteEndObject();
        }

        switch (node)
        {
            case OrNode or:
                WriteChildren(w, or.Children, spans);
                break;
            case AndNode and:
                WriteChildren(w, and.Children, spans);
                break;
            case NotNode not:
                w.WritePropertyName("child");
                WriteNode(w, not.Child, spans);
                break;
            case ProximityNode proximity:
                w.WriteNumber("distance", proximity.Distance);
                w.WriteBoolean("ordered", proximity.Ordered);
                w.WritePropertyName("left");
                WriteNode(w, proximity.Left, spans);
                w.WritePropertyName("right");
                WriteNode(w, proximity.Right, spans);
                break;
            case FieldNode field:
                w.WriteString("name", field.Name);
                w.WritePropertyName("child");
                WriteNode(w, field.Child, spans);
                break;
            case TermNode term:
                w.WriteString("text", term.Text);
                if (term.RootExpand)
                {
                    w.WriteBoolean("rootExpand", true);
                }

                break;
            case PhraseNode phrase:
                w.WriteString("text", phrase.Text);
                break;
            case WildcardNode wildcard:
                w.WriteString("pattern", wildcard.Pattern);
                break;
            case RangeNode range:
                WriteBound(w, "lower", range.Lower);
                WriteBound(w, "upper", range.Upper);
                break;
        }

        w.WriteEndObject();
    }

    private static void WriteChildren(Utf8JsonWriter w, IReadOnlyList<QueryNode> children, bool spans)
    {
        w.WriteStartArray("children");
        foreach (var child in children)
        {
            WriteNode(w, child, spans);
        }

        w.WriteEndArray();
    }

    private static void WriteBound(Utf8JsonWriter w, string name, RangeBound? bound)
    {
        if (bound is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteString("value", bound.Value);
        w.WriteBoolean("inclusive", bound.Inclusive);
        w.WriteEndObject();
    }
}

/// <summary>Structural equality of ASTs, optionally ignoring spans (for <c>Parse(Print(ast)) == ast</c>).</summary>
public sealed class QueryAstComparer(bool compareSpans) : IEqualityComparer<QueryNode>
{
    public static QueryAstComparer IgnoringSpans { get; } = new(compareSpans: false);

    public static QueryAstComparer WithSpans { get; } = new(compareSpans: true);

    public bool Equals(QueryNode? x, QueryNode? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.Kind != y.Kind || (compareSpans && x.Span != y.Span))
        {
            return false;
        }

        return (x, y) switch
        {
            (OrNode a, OrNode b) => SequenceEquals(a.Children, b.Children),
            (AndNode a, AndNode b) => SequenceEquals(a.Children, b.Children),
            (NotNode a, NotNode b) => Equals(a.Child, b.Child),
            (ProximityNode a, ProximityNode b) =>
                a.Distance == b.Distance && a.Ordered == b.Ordered && Equals(a.Left, b.Left) && Equals(a.Right, b.Right),
            (FieldNode a, FieldNode b) => a.Name == b.Name && Equals(a.Child, b.Child),
            (TermNode a, TermNode b) => a.Text == b.Text && a.RootExpand == b.RootExpand,
            (PhraseNode a, PhraseNode b) => a.Text == b.Text,
            (WildcardNode a, WildcardNode b) => a.Pattern == b.Pattern,
            (RangeNode a, RangeNode b) => a.Lower == b.Lower && a.Upper == b.Upper,
            (MatchAllNode, MatchAllNode) or (ExistsNode, ExistsNode) => true,
            _ => false,
        };
    }

    public int GetHashCode(QueryNode obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return HashCode.Combine(obj.Kind, compareSpans ? obj.Span : default);
    }

    private bool SequenceEquals(IReadOnlyList<QueryNode> a, IReadOnlyList<QueryNode> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!Equals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }
}
