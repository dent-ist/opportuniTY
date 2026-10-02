using System.Collections.Frozen;

namespace Opportunity.Testing.Images;

/// <summary>
/// Reads <c>versions.env</c> at the repository root, the single source of truth for tool and image versions.
/// </summary>
public sealed class VersionsFile
{
    public const string FileName = "versions.env";

    private static readonly Lazy<VersionsFile> RepositoryInstance = new(() => Load(Locate()));

    private readonly FrozenDictionary<string, string> _values;

    private VersionsFile(string path, FrozenDictionary<string, string> values)
    {
        Path = path;
        _values = values;
    }

    public static VersionsFile Repository => RepositoryInstance.Value;

    public string Path { get; }

    public string this[string key] =>
        TryGet(key) ?? throw new KeyNotFoundException($"'{key}' is not defined in {Path}.");

    public string? TryGet(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public static VersionsFile Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new FormatException($"Malformed line in {path}: '{raw}'.");
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
        }

        return new VersionsFile(path, values.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Walks up from the test binaries (then the working directory) to the first <c>versions.env</c>.</summary>
    public static string Locate()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Could not find {FileName} above '{AppContext.BaseDirectory}'.");
    }
}
