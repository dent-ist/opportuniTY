namespace Opportunity.Import.Volumes;

/// <summary>
/// The lexical rules for a file path from a load file (DAT NativeLink/TextLink, OPT image path): <c>\</c> or <c>/</c>
/// separators, an optional leading <c>.\</c>, and an absolute path only when the configured strip prefix rebases it.
/// What remains must be a plain relative path: no <c>..</c>, no root, drive, UNC or device form, no <c>:</c> (drive
/// letters, alternate data streams, URLs), no wildcard or control characters. Pure; <see cref="ImportVolume"/> applies
/// the file-system rules (symbolic links, containment) on top.
/// </summary>
public static class VolumePath
{
    public const int MaxLength = 4_096;
    public const int MaxSegmentLength = 255;

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Splits <paramref name="raw"/> into relative segments, or explains why it is refused.</summary>
    /// <param name="stripPrefix">Leading portion removed first (compared case-insensitively, either separator).</param>
    public static bool TryParse(string? raw, string? stripPrefix, out IReadOnlyList<string> segments, out string? error)
    {
        segments = [];
        var path = raw?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            error = "The path is empty.";
            return false;
        }

        if (path.Length > MaxLength)
        {
            error = $"The path is longer than {MaxLength} characters.";
            return false;
        }

        if (path.Any(char.IsControl))
        {
            error = "The path contains control characters.";
            return false;
        }

        if (Strip(path, stripPrefix) is { } rebased)
        {
            path = rebased;
        }

        if (path.Length > 0 && Array.IndexOf(Separators, path[0]) >= 0)
        {
            error = "Absolute, UNC and device paths are not allowed; set a strip prefix to rebase them onto the volume.";
            return false;
        }

        var parts = new List<string>();
        foreach (var segment in path.Split(Separators))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment.Trim('.').Length == 0)
            {
                error = "The path leaves the volume ('..').";
                return false;
            }

            if (segment.Contains(':', StringComparison.Ordinal))
            {
                error = "Drive letters, alternate data streams and URLs are not allowed in a volume path.";
                return false;
            }

            if (segment.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            {
                error = "The path contains characters that are not allowed in file names.";
                return false;
            }

            if (segment.Length > MaxSegmentLength)
            {
                error = $"A path segment is longer than {MaxSegmentLength} characters.";
                return false;
            }

            parts.Add(segment);
        }

        if (parts.Count == 0)
        {
            error = "The path names no file.";
            return false;
        }

        segments = parts;
        error = null;
        return true;
    }

    /// <summary>The relative path with <c>/</c> separators, for messages and reports.</summary>
    public static string Display(IReadOnlyList<string> segments) => string.Join('/', segments);

    /// <summary><paramref name="path"/> without <paramref name="prefix"/> when it starts with it at a segment boundary.</summary>
    private static string? Strip(string path, string? prefix)
    {
        var p = prefix?.Trim().TrimEnd(Separators);
        if (string.IsNullOrEmpty(p) || path.Length < p.Length)
        {
            return null;
        }

        for (var i = 0; i < p.Length; i++)
        {
            var a = path[i];
            var b = p[i];
            var same = Array.IndexOf(Separators, a) >= 0
                ? Array.IndexOf(Separators, b) >= 0
                : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
            if (!same)
            {
                return null;
            }
        }

        if (path.Length == p.Length)
        {
            return string.Empty;
        }

        return Array.IndexOf(Separators, path[p.Length]) >= 0 ? path[(p.Length + 1)..] : null;
    }
}
