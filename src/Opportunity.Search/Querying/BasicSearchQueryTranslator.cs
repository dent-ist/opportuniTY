using System.Globalization;
using System.Text.Json.Nodes;

using Opportunity.Core.QueryLanguage;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>
/// First-slice translator over the fixed system fields of projection v1: unfielded terms search <c>text</c>,
/// <c>W/n</c> uses interval queries, and fields resolve case-insensitively against <see cref="Fields"/>. Workspace
/// custom fields, coding fields, cost limits and the final proximity semantics belong to the planner (E07-T07), which
/// replaces this class behind <see cref="ISearchQueryTranslator"/>.
/// </summary>
public sealed class BasicSearchQueryTranslator : ISearchQueryTranslator
{
    private enum FieldKind
    {
        Text,
        Keyword,
        Date,
        Number,
        Boolean,
    }

    private sealed record FieldInfo(string Path, FieldKind Kind, string? WildcardPath = null);

    private static readonly Dictionary<string, FieldInfo> Fields = BuildFields();

    public ValueTask<SearchTranslation> TranslateAsync(
        QueryNode ast, SearchTranslationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<QueryDiagnostic>();
        var complex = false;
        var query = Translate(ast, null, context.Limits, errors, ref complex);
        return ValueTask.FromResult(errors.Count > 0 || query is null
            ? SearchTranslation.Failed(errors)
            : SearchTranslation.Ok(query, complex ? SearchTranslation.Complex : SearchTranslation.Simple));
    }

    private static JsonObject? Translate(QueryNode node, FieldInfo? field, QueryLimits limits, List<QueryDiagnostic> errors, ref bool complex)
    {
        switch (node)
        {
            case MatchAllNode:
                return Obj(("match_all", new JsonObject()));
            case AndNode and:
                return Bool("must", Children(and.Children, field, limits, errors, ref complex));
            case OrNode or:
                var should = Bool("should", Children(or.Children, field, limits, errors, ref complex));
                ((JsonObject)should["bool"]!)["minimum_should_match"] = 1;
                return should;
            case NotNode not:
                return Bool("must_not", Children([not.Child], field, limits, errors, ref complex));
            case FieldNode f:
                if (!Fields.TryGetValue(f.Name, out var resolved) || ProjectionFields.NotAddressable.Contains(f.Name))
                {
                    errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.UnknownField, $"Unknown field '{f.Name}'.", f.Span));
                    return null;
                }

                return Translate(f.Child, resolved, limits, errors, ref complex);
            case ProximityNode p:
                complex = true;
                return Proximity(p, field ?? Fields["text"], errors);
            default:
                return Leaf(node, field ?? Fields["text"], limits, errors, ref complex);
        }
    }

    private static JsonArray Children(
        IEnumerable<QueryNode> children, FieldInfo? field, QueryLimits limits, List<QueryDiagnostic> errors, ref bool complex)
    {
        var array = new JsonArray();
        foreach (var child in children)
        {
            if (Translate(child, field, limits, errors, ref complex) is { } clause)
            {
                array.Add(clause);
            }
        }

        return array;
    }

    private static JsonObject? Leaf(QueryNode node, FieldInfo field, QueryLimits limits, List<QueryDiagnostic> errors, ref bool complex)
    {
        switch (node)
        {
            case ExistsNode:
                return Obj(("exists", Obj(("field", field.Path))));
            case TermNode term:
                return field.Kind == FieldKind.Text
                    ? Obj(("match", Obj((field.Path, Obj(("query", term.Text), ("operator", "and"))))))
                    : Exact(field, term.Text, term.Span, errors);
            case PhraseNode phrase:
                return field.Kind == FieldKind.Text
                    ? Obj(("match_phrase", Obj((field.Path, Obj(("query", phrase.Text))))))
                    : Exact(field, phrase.Text, phrase.Span, errors);
            case WildcardNode wildcard:
                complex = true;
                if (field.Kind is not (FieldKind.Text or FieldKind.Keyword))
                {
                    errors.Add(Unsupported("Wildcards", wildcard.Span));
                    return null;
                }

                var minimum = field.Kind == FieldKind.Text ? limits.MinWildcardPrefix : 1;
                if (wildcard.LiteralPrefixLength < minimum)
                {
                    errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.LeadingWildcard,
                        $"A wildcard needs at least {minimum} literal character(s) before the first * or ?.", wildcard.Span));
                    return null;
                }

                return Obj(("wildcard", Obj((field.WildcardPath ?? field.Path,
                    Obj(("value", wildcard.Pattern), ("case_insensitive", true))))));
            case RangeNode range:
                if (field.Kind is not (FieldKind.Date or FieldKind.Number or FieldKind.Keyword))
                {
                    errors.Add(Unsupported("Ranges", range.Span));
                    return null;
                }

                var bounds = new JsonObject();
                if (range.Lower is { } lower && Value(field, lower.Value, range.Span, errors) is { } lo)
                {
                    bounds[lower.Inclusive ? "gte" : "gt"] = lo;
                }

                if (range.Upper is { } upper && Value(field, upper.Value, range.Span, errors) is { } hi)
                {
                    bounds[upper.Inclusive ? "lte" : "lt"] = hi;
                }

                return Obj(("range", Obj((field.Path, bounds))));
            default:
                errors.Add(Unsupported(node.Kind.ToString(), node.Span));
                return null;
        }
    }

    private static JsonObject? Exact(FieldInfo field, string text, SourceSpan span, List<QueryDiagnostic> errors) =>
        Value(field, text, span, errors) is { } value ? Obj(("term", Obj((field.Path, Obj(("value", value)))))) : null;

    private static JsonNode? Value(FieldInfo field, string text, SourceSpan span, List<QueryDiagnostic> errors)
    {
        switch (field.Kind)
        {
            case FieldKind.Date when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date):
                return date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            case FieldKind.Number when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number):
                return number;
            case FieldKind.Boolean when text.ToUpperInvariant() is "TRUE" or "YES":
                return true;
            case FieldKind.Boolean when text.ToUpperInvariant() is "FALSE" or "NO":
                return false;
            case FieldKind.Keyword or FieldKind.Text:
                return text;
            default:
                errors.Add(new QueryDiagnostic(SearchQueryErrorCodes.InvalidFieldValue,
                    $"'{text}' is not a valid {field.Kind.ToString().ToLowerInvariant()} value.", span));
                return null;
        }
    }

    /// <summary><c>a W/n b</c>: both within n words in either order, at most n−1 words between (ADR-008 §2).</summary>
    private static JsonObject? Proximity(ProximityNode p, FieldInfo field, List<QueryDiagnostic> errors)
    {
        if (field.Kind != FieldKind.Text)
        {
            errors.Add(Unsupported("Proximity", p.Span));
            return null;
        }

        var left = Interval(p.Left, errors);
        var right = Interval(p.Right, errors);
        if (left is null || right is null)
        {
            return null;
        }

        var allOf = Obj(
            ("intervals", new JsonArray(left, right)),
            ("max_gaps", p.Distance - 1),
            ("ordered", p.Ordered));
        return Obj(("intervals", Obj((field.Path, Obj(("all_of", allOf))))));
    }

    private static JsonObject? Interval(QueryNode node, List<QueryDiagnostic> errors)
    {
        switch (node)
        {
            case TermNode term:
                return Obj(("match", Obj(("query", term.Text), ("max_gaps", 0), ("ordered", true))));
            case PhraseNode phrase:
                return Obj(("match", Obj(("query", phrase.Text), ("max_gaps", 0), ("ordered", true))));
            case WildcardNode wildcard:
                return Obj(("wildcard", Obj(("pattern", wildcard.Pattern.ToLowerInvariant()))));
            case OrNode or:
                var any = new JsonArray();
                foreach (var child in or.Children)
                {
                    if (Interval(child, errors) is not { } interval)
                    {
                        return null;
                    }

                    any.Add(interval);
                }

                return Obj(("any_of", Obj(("intervals", any))));
            default:
                errors.Add(new QueryDiagnostic(QueryErrorCodes.InvalidProximityOperand,
                    "A W/n operand must be a term, phrase, wildcard or an OR of those.", node.Span));
                return null;
        }
    }

    private static JsonObject Bool(string occur, JsonArray clauses) => Obj(("bool", Obj((occur, clauses))));

    private static QueryDiagnostic Unsupported(string what, SourceSpan span) =>
        new(SearchQueryErrorCodes.UnsupportedForField, $"{what} are not supported for this field.", span);

    private static Dictionary<string, FieldInfo> BuildFields()
    {
        var fields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = new(ProjectionFields.Text, FieldKind.Text),
            ["fileName"] = new(ProjectionFields.FileName, FieldKind.Text, ProjectionFields.FileNameKeyword),
            ["controlNumber"] = new(ProjectionFields.ControlNumber, FieldKind.Keyword),
        };
        foreach (var keyword in new[]
        {
            "fileType", "fileExtension", "mimeType", "familyId", "parentDocumentId", "familyStatus", "duplicateGroupId",
            "emailThreadId", "begBates", "endBates", "md5", "sha1", "sha256",
        })
        {
            fields[keyword] = new(keyword, FieldKind.Keyword);
        }

        foreach (var date in new[] { "documentDate", "familyDate", "dateSent", "dateReceived", "dateCreated", "dateLastModified" })
        {
            fields[date] = new(date, FieldKind.Date);
        }

        foreach (var number in new[] { "fileSize", "pageCount", "familySequence", "textLength" })
        {
            fields[number] = new(number, FieldKind.Number);
        }

        foreach (var flag in new[] { "textTruncated", "textMissing", "nativeMissing", "imagesIncomplete", "isDuplicatePrimary" })
        {
            fields[flag] = new(flag, FieldKind.Boolean);
        }

        return fields;
    }
}
