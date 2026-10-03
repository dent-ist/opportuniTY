namespace Opportunity.Benchmarks.Capture;

/// <summary>
/// Reads <c>versions.env</c>, the single source of truth for service versions and image digests (<c>KEY</c> +
/// <c>KEY_DIGEST</c>), and records it with its hash.
/// </summary>
public sealed class VersionsEnvFile
{
    public const string FileName = "versions.env";

    /// <summary>versions.env keys that pin a dependency image, with the image repository Compose and the fixtures use.</summary>
    public static IReadOnlyDictionary<string, string> ImageRepositories { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["POSTGRES"] = "postgres",
        ["OPENSEARCH"] = "opensearchproject/opensearch",
        ["RABBITMQ"] = "rabbitmq",
        ["SEAWEEDFS"] = "chrislusf/seaweedfs",
        ["TOXIPROXY"] = "ghcr.io/shopify/toxiproxy",
    };

    private VersionsEnvFile(string path, string sha256, IReadOnlyDictionary<string, string> values)
    {
        Path = path;
        Sha256 = sha256;
        Values = values;
    }

    public string Path { get; }

    public string Sha256 { get; }

    public IReadOnlyDictionary<string, string> Values { get; }

    public static VersionsEnvFile Load(string path)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException($"Malformed line in {path}: '{raw}'.");
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
        }

        return new VersionsEnvFile(path, Infrastructure.BenchJson.Sha256OfFile(path), values);
    }

    /// <summary>Walks up from <paramref name="start"/> (default: working directory, then the binaries) to the first versions.env.</summary>
    public static string? Locate(string? start = null)
    {
        foreach (string origin in start is null ? [Directory.GetCurrentDirectory(), AppContext.BaseDirectory] : new[] { start })
        {
            for (var dir = new DirectoryInfo(origin); dir is not null; dir = dir.Parent)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public IReadOnlyList<PinnedImage> PinnedImages() =>
        [.. ImageRepositories
            .Where(pair => Values.ContainsKey(pair.Key))
            .Select(pair => new PinnedImage
            {
                Key = pair.Key,
                Repository = pair.Value,
                Tag = Values[pair.Key],
                Digest = Values.TryGetValue(pair.Key + "_DIGEST", out string? digest) && digest.Length > 0 ? digest : null,
            })];

    public VersionsEnvInfo ToInfo(string? displayPath = null) => new()
    {
        Path = displayPath ?? Path,
        Sha256 = Sha256,
        Values = Values,
    };
}
