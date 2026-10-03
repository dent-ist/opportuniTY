using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Opportunity.Search.Indexing;

/// <summary>
/// The versioned projection mappings embedded from <c>Mappings/projection.v{G}.json</c> (ADR-007 R2). Each file holds
/// <c>settings.analysis</c> and <c>mappings</c>; <c>G</c> is the ProjectionGeneration. New placements use
/// <see cref="CurrentGeneration"/>; older generations stay loadable while indexes built from them still exist.
/// </summary>
public sealed partial class ProjectionMappings
{
    private readonly SortedDictionary<int, (string Json, string Sha256)> _generations;

    private ProjectionMappings(SortedDictionary<int, (string Json, string Sha256)> generations)
    {
        if (generations.Count == 0)
        {
            throw new InvalidOperationException("No projection mapping is embedded in Opportunity.Search.");
        }

        _generations = generations;
    }

    public static ProjectionMappings Embedded { get; } = LoadEmbedded();

    public IReadOnlyCollection<int> Generations => _generations.Keys;

    public int CurrentGeneration => _generations.Keys.Max();

    /// <summary>A fresh, mutable copy of generation <paramref name="generation"/>'s mapping document.</summary>
    public JsonObject Load(int generation) =>
        _generations.TryGetValue(generation, out var g)
            ? (JsonObject)JsonNode.Parse(g.Json)!
            : throw new InvalidOperationException($"Projection generation {generation} is not known to this build.");

    /// <summary>SHA-256 of the mapping file, recorded in the template's <c>_meta</c> to detect drift.</summary>
    public string Checksum(int generation) => _generations[generation].Sha256;

    /// <summary>Mappings from in-memory JSON keyed by generation (tests).</summary>
    internal static ProjectionMappings FromJson(IReadOnlyDictionary<int, string> generations)
    {
        var map = new SortedDictionary<int, (string, string)>();
        foreach (var (generation, json) in generations)
        {
            map.Add(generation, (json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))));
        }

        return new ProjectionMappings(map);
    }

    private static ProjectionMappings LoadEmbedded()
    {
        var assembly = typeof(ProjectionMappings).Assembly;
        var generations = new SortedDictionary<int, (string, string)>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var match = ResourceName().Match(name);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            generations.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), (json, sha));
        }

        return new ProjectionMappings(generations);
    }

    [GeneratedRegex(@"^Opportunity\.Search\.Mappings\.projection\.v([1-9][0-9]{0,3})\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceName();
}
