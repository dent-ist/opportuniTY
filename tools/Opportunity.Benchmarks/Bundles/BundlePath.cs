using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Opportunity.Benchmarks.Bundles;

/// <summary>
/// Paths into bundle.json as used by gates.yaml: dot-separated property names; <c>name[key=value]</c> selects the
/// items of an array whose <c>key</c> property equals <c>value</c>, e.g.
/// <c>scenarios[role=bulk-load].queryClasses[name=simple].latency.p95</c>.
/// </summary>
public static partial class BundlePath
{
    public sealed record Segment(string Property, string? SelectorKey, string? SelectorValue);

    public static IReadOnlyList<Segment> Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return [.. path.Split('.').Select(part =>
        {
            Match match = SegmentPattern().Match(part);
            if (!match.Success)
            {
                throw new FormatException($"Invalid bundle path segment '{part}' in '{path}'.");
            }

            return new Segment(
                match.Groups["name"].Value,
                match.Groups["key"].Success ? match.Groups["key"].Value : null,
                match.Groups["value"].Success ? match.Groups["value"].Value : null);
        })];
    }

    /// <summary>Every node the path selects (several when a selector matches several items, e.g. repeated roles).</summary>
    public static IReadOnlyList<JsonNode> Resolve(JsonNode root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        IEnumerable<JsonNode> current = [root];
        foreach (Segment segment in Parse(path))
        {
            current = current.SelectMany(node => Step(node, segment)).ToList();
        }

        return [.. current];
    }

    private static IEnumerable<JsonNode> Step(JsonNode node, Segment segment)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment.Property, out JsonNode? child) || child is null)
        {
            return [];
        }

        if (segment.SelectorKey is null)
        {
            return [child];
        }

        return child is JsonArray array
            ? array.OfType<JsonObject>().Where(item =>
                item.TryGetPropertyValue(segment.SelectorKey, out JsonNode? key)
                && key is JsonValue value
                && value.GetValueKind() == JsonValueKind.String
                && value.GetValue<string>() == segment.SelectorValue)
            : [];
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_]+)(\[(?<key>[A-Za-z0-9_]+)=(?<value>[^\]]+)\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
