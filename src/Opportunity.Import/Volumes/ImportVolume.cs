namespace Opportunity.Import.Volumes;

/// <summary>
/// Where import volumes live on the import worker: <c>Import:VolumeShareRoot</c>, a server-side directory (the
/// "staging location" of the wizard, guide §5.1). An import's <c>paths.volumeRoot</c> names a folder inside it.
/// </summary>
public sealed class ImportVolumeOptions
{
    public const string SectionName = "Import";

    /// <summary>Absolute directory every volume root must lie in; null when the installation has none.</summary>
    public string? VolumeShareRoot { get; init; }
}

/// <summary>
/// The import-root jail (ADR-015 D15.1, threat T-43) for load-file paths: a volume root inside the configured share,
/// and volume-relative paths resolved inside it. <c>\</c> and <c>/</c> both separate; a leading <c>.\</c> is dropped
/// and a configured prefix (e.g. <c>\\server\export\</c>) is stripped first. Rejected: <c>..</c> segments, rooted,
/// drive-letter and UNC paths, alternate data streams (<c>:</c>), control characters, and any existing path component
/// that is a symbolic link or junction (not followed, even when it points inside the root).
/// </summary>
/// <remarks>
/// E08-T05 (OPT images) and E08-T04 (natives, text) both resolve volume-relative paths; this is the single place that
/// turns a load-file string into a file path, kept free of import logic so both can share it.
/// </remarks>
public sealed class ImportVolume
{
    private ImportVolume(string root) => Root = root;

    /// <summary>The volume root as a full path, without a trailing separator.</summary>
    public string Root { get; }

    /// <summary>
    /// The volume root for an import: <paramref name="volumeRoot"/> (relative to the share, or absolute inside it; empty
    /// is the share itself), which must be an existing directory reached without symbolic links.
    /// </summary>
    public static bool TryOpen(ImportVolumeOptions options, string? volumeRoot, out ImportVolume? volume, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        volume = null;
        if (string.IsNullOrWhiteSpace(options.VolumeShareRoot) || !Path.IsPathFullyQualified(options.VolumeShareRoot))
        {
            error = "No import volume share is configured (Import:VolumeShareRoot), so image files cannot be read.";
            return false;
        }

        var share = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.VolumeShareRoot));
        if (!Directory.Exists(share))
        {
            error = "The import volume share does not exist on the import worker.";
            return false;
        }

        var root = share;
        if (!string.IsNullOrWhiteSpace(volumeRoot))
        {
            var candidate = volumeRoot.Trim();
            if (Path.IsPathFullyQualified(candidate))
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                if (!IsInside(share, full, allowEqual: true))
                {
                    error = "The volume root is outside the import volume share.";
                    return false;
                }

                candidate = Path.GetRelativePath(share, full);
                if (candidate == ".")
                {
                    candidate = string.Empty;
                }
            }

            if (candidate.Length > 0)
            {
                if (!TryResolveUnder(share, candidate, null, out var resolved, out error))
                {
                    error = "The volume root was rejected: " + error;
                    return false;
                }

                root = resolved!;
            }
        }

        if (!Directory.Exists(root))
        {
            error = "The volume root does not exist in the import volume share.";
            return false;
        }

        volume = new ImportVolume(root);
        error = null;
        return true;
    }

    /// <summary>Resolves a load-file path against the volume root; false with the reason when it is rejected.</summary>
    public bool TryResolve(string loadFilePath, string? stripPrefix, out string? fullPath, out string? error) =>
        TryResolveUnder(Root, loadFilePath, stripPrefix, out fullPath, out error);

    internal static bool TryResolveUnder(string root, string loadFilePath, string? stripPrefix, out string? fullPath, out string? error)
    {
        fullPath = null;
        var path = (loadFilePath ?? string.Empty).Trim();
        if (stripPrefix is { Length: > 0 } prefix)
        {
            var normalizedPrefix = prefix.Trim().Replace('\\', '/');
            var normalizedPath = path.Replace('\\', '/');
            if (normalizedPrefix.Length > 0 && normalizedPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[normalizedPrefix.Length..];
            }
        }

        if (path.Length == 0)
        {
            error = "The path is empty.";
            return false;
        }

        if (path.Length > 4_000 || path.Any(char.IsControl))
        {
            error = "The path is too long or contains control characters.";
            return false;
        }

        var slashed = path.Replace('\\', '/');
        if (slashed.StartsWith('/') || (slashed.Length >= 2 && slashed[1] == ':' && char.IsAsciiLetter(slashed[0])))
        {
            error = "Absolute, drive-letter and UNC paths are not allowed; paths are relative to the volume root.";
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in slashed.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment.Trim() == ".." || segment.Contains(':', StringComparison.Ordinal))
            {
                error = "The path leaves the volume root ('..') or names a drive or stream (':').";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            error = "The path names no file.";
            return false;
        }

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = new FileInfo(current);
            if (!info.Exists)
            {
                info = new DirectoryInfo(current);
            }

            // A link is rejected even when it resolves inside the root: the jail never follows links.
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                error = "The path goes through a symbolic link, which is not followed.";
                return false;
            }
        }

        var full = Path.GetFullPath(current);
        if (!IsInside(root, full, allowEqual: false))
        {
            error = "The path leaves the volume root.";
            return false;
        }

        fullPath = full;
        error = null;
        return true;
    }

    private static bool IsInside(string root, string full, bool allowEqual)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (allowEqual && string.Equals(root, full, comparison))
            || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
