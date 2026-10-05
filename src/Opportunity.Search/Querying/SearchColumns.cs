using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Contracts.Search;
using Opportunity.Core.Fields;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.Search.Querying;

/// <summary>A field whose values every hit of a search carries: its query name and projection path.</summary>
internal sealed record ResultField(string Name, string Path);

/// <summary>
/// The grid's columns over the projection (E16-T09): sort keys for workspace fields with the <c>sortable</c> capability,
/// and the values of the fields a search asked for, read from <c>_source</c> as strings. Paths never leave the search
/// module; the API addresses fields by query name.
/// </summary>
internal static class SearchColumns
{
    /// <summary>
    /// The sort key of a workspace field by query name, or null when the field is unknown, has no search slot or lacks
    /// the <c>sortable</c> capability (choice and user fields, ADR-007 R8). Text sorts on its keyword companion.
    /// </summary>
    public static SortKey? SortKeyFor(SearchFieldResolver resolver, string name, bool descending)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (Single(resolver, name) is not { } target || !target.Has(FieldCapabilities.Sortable))
        {
            return null;
        }

        var path = target.Kind switch
        {
            SearchValueKind.FullText when target.Definition?.Storage == FieldStorage.Column =>
                target.Path == ProjectionFields.FileName ? ProjectionFields.FileNameKeyword : null,
            SearchValueKind.FullText when target.Path != ProjectionFields.Text => target.Path + ".kw",
            SearchValueKind.Keyword => target.SortKeyPath ?? target.Path,
            SearchValueKind.Integer or SearchValueKind.Decimal or SearchValueKind.Date or SearchValueKind.Boolean => target.Path,
            _ => null,
        };
        return path is null ? null : new SortKey(name.ToLowerInvariant(), path, descending);
    }

    /// <summary>The field to return for <paramref name="name"/>, or null when it is unknown or has no search slot.</summary>
    public static ResultField? FieldFor(SearchFieldResolver resolver, string name)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return Single(resolver, name) is { } target && target.Path != ProjectionFields.Text && !ProjectionFields.NotAddressable.Contains(target.Path)
            ? new ResultField(name, target.Path)
            : null;
    }

    public static string ToJson(IReadOnlyList<ResultField> fields) =>
        new JsonArray([.. fields.Select(f => (JsonNode)Obj(("name", f.Name), ("path", f.Path)))]).ToJsonString();

    public static IReadOnlyList<ResultField> FromJson(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : [.. JsonNode.Parse(json)!.AsArray().Select(n => new ResultField(n!["name"]!.GetValue<string>(), n["path"]!.GetValue<string>()))];

    /// <summary>The values of <paramref name="fields"/> in a hit's <c>_source</c>; null when none were asked for.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Values(JsonObject source, IReadOnlyList<ResultField> fields)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (fields.Count == 0)
        {
            return null;
        }

        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var strings = Strings(Find(source, field.Path)).Take(SearchResultFields.MaxValuesPerField).ToList();
            if (strings.Count > 0)
            {
                values[field.Name] = strings;
            }
        }

        return values;
    }

    /// <summary>
    /// The node at a dotted path: nested objects (<c>coding.ch.s001</c>), or a key of a <c>flat_object</c> container that
    /// may itself contain dots (<c>metadataOverflow.some.key</c>).
    /// </summary>
    internal static JsonNode? Find(JsonObject source, string path)
    {
        if (source.TryGetPropertyValue(path, out var direct))
        {
            return direct;
        }

        var dot = path.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && source[path[..dot]] is JsonObject child ? Find(child, path[(dot + 1)..]) : null;
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (Scalar(item) is { } value)
                    {
                        yield return value;
                    }
                }

                break;
            case JsonValue:
                if (Scalar(node) is { } single)
                {
                    yield return single;
                }

                break;
        }
    }

    private static string? Scalar(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        var text = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        return text is null ? null
            : text.Length > SearchResultFields.MaxValueLength ? text[..SearchResultFields.MaxValueLength]
            : text.ToString(CultureInfo.InvariantCulture);
    }

    private static SearchTarget? Single(SearchFieldResolver resolver, string name) =>
        resolver.Resolve(name, out var targets) == FieldResolution.Resolved && targets.Count == 1 ? targets[0] : null;
}
