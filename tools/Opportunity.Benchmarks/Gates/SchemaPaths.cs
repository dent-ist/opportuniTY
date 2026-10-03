using System.Text.Json.Nodes;

using Opportunity.Benchmarks.Bundles;

namespace Opportunity.Benchmarks.Gates;

/// <summary>Checks a <see cref="BundlePath"/> against a JSON Schema document (local <c>$ref</c>s, allOf/anyOf/oneOf).</summary>
public static class SchemaPaths
{
    /// <summary>Null when the path is declared by the schema, otherwise why not.</summary>
    public static string? Explain(JsonNode schemaRoot, string path)
    {
        ArgumentNullException.ThrowIfNull(schemaRoot);
        JsonNode current = schemaRoot;
        foreach (BundlePath.Segment segment in BundlePath.Parse(path))
        {
            JsonNode? property = FindProperty(schemaRoot, current, segment.Property);
            if (property is null)
            {
                return $"'{segment.Property}' is not declared by the schema.";
            }

            current = Deref(schemaRoot, property);
            if (segment.SelectorKey is null)
            {
                continue;
            }

            if (current["items"] is not JsonNode items)
            {
                return $"'{segment.Property}' is not an array, so it cannot take a [{segment.SelectorKey}=...] selector.";
            }

            current = Deref(schemaRoot, items);
            JsonNode? key = FindProperty(schemaRoot, current, segment.SelectorKey);
            if (key is null)
            {
                return $"items of '{segment.Property}' have no '{segment.SelectorKey}' property.";
            }

            key = Deref(schemaRoot, key);
            if (key["enum"] is JsonArray allowed && !allowed.Any(v => v?.GetValue<string>() == segment.SelectorValue))
            {
                return $"'{segment.SelectorValue}' is not an allowed {segment.SelectorKey} ({string.Join(", ", allowed)}).";
            }
        }

        return null;
    }

    private static JsonNode? FindProperty(JsonNode root, JsonNode schema, string name)
    {
        schema = Deref(root, schema);
        if (schema["properties"] is JsonObject properties && properties.TryGetPropertyValue(name, out JsonNode? found) && found is not null)
        {
            return found;
        }

        foreach (string combinator in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema[combinator] is JsonArray branches)
            {
                foreach (JsonNode? branch in branches)
                {
                    if (branch is not null && FindProperty(root, branch, name) is { } inBranch)
                    {
                        return inBranch;
                    }
                }
            }
        }

        return null;
    }

    private static JsonNode Deref(JsonNode root, JsonNode schema)
    {
        for (int depth = 0; depth < 32 && schema["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith("#/", StringComparison.Ordinal); depth++)
        {
            JsonNode? target = root;
            foreach (string token in reference[2..].Split('/'))
            {
                target = target?[token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
            }

            schema = target ?? throw new InvalidOperationException($"Unresolvable $ref '{reference}'.");
        }

        return schema;
    }
}
