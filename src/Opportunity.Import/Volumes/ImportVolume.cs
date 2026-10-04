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

public enum VolumeFileStatus
{
    /// <summary>A regular file inside the volume; <see cref="VolumeFile.FullPath"/> is its link-free real path.</summary>
    Found,

    /// <summary>The path is acceptable but names nothing readable (absent, a directory, a dangling link).</summary>
    Missing,

    /// <summary>The path is refused: traversal, an absolute form, or a symbolic link that leads outside the volume.</summary>
    Rejected,
}

/// <param name="RelativePath">The parsed volume-relative path (<c>/</c> separators), when the path parsed.</param>
/// <param name="FullPath">The file to open, only when <see cref="Status"/> is <see cref="VolumeFileStatus.Found"/>.</param>
/// <param name="Reason">Why the file is missing or rejected; safe to show in the import report.</param>
public sealed record VolumeFile(VolumeFileStatus Status, string? RelativePath, string? FullPath, string? Reason);

/// <summary>
/// A load-file volume on the import share: the folder that relative native, text and image paths resolve against.
/// <see cref="Resolve"/> parses the path lexically (<see cref="VolumePath"/>), then walks it one component at a time from
/// the volume's real path and accepts only a regular file inside the volume. Symbolic links are never followed below the
/// import share (the volume folder and every file path from a load file): a link anywhere on the path rejects it, so
/// nothing a load file names can be redirected. Only the operator-configured share path itself may contain links.
/// Volumes produced on Windows often differ in letter case from their DAT paths; a component that does not exist
/// exactly is matched case-insensitively when exactly one entry matches.
/// </summary>
public sealed class ImportVolume
{
    private const int MaxLinkHops = 40;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private ImportVolume(string realRoot) => Root = realRoot;

    /// <summary>The volume folder's real path (links resolved).</summary>
    public string Root { get; }

    /// <summary>
    /// Opens <paramref name="volumeRoot"/> (relative, may be null) under the import share <paramref name="shareRoot"/>
    /// (operator-configured, absolute). The volume must be an existing folder inside the share.
    /// </summary>
    private static bool TryOpenCore(string? shareRoot, string? volumeRoot, out ImportVolume? volume, out string? error)
    {
        volume = null;
        if (string.IsNullOrWhiteSpace(shareRoot) || !Path.IsPathFullyQualified(shareRoot))
        {
            error = "No import share is configured for this installation (Import:VolumeShareRoot), so native and text files cannot be read.";
            return false;
        }

        var fullShare = Path.GetFullPath(shareRoot);
        if (!Directory.Exists(fullShare) || Walk(Path.GetPathRoot(fullShare)!, Split(fullShare), null, followLinks: true) is not { Path: { } share } || !Directory.Exists(share))
        {
            error = "The import share is not available on this worker.";
            return false;
        }

        var root = share;
        if (!string.IsNullOrWhiteSpace(volumeRoot) && Path.IsPathFullyQualified(volumeRoot.Trim()))
        {
            // An absolute volume folder is accepted only inside the share, and is then walked like a relative one.
            var absolute = Path.GetFullPath(volumeRoot.Trim());
            if (!IsWithin(fullShare, absolute) && !IsWithin(share, absolute))
            {
                error = "The volume folder leads outside the import share.";
                return false;
            }

            volumeRoot = Path.GetRelativePath(IsWithin(share, absolute) ? share : fullShare, absolute);
            if (volumeRoot == ".")
            {
                volumeRoot = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(volumeRoot))
        {
            if (!VolumePath.TryParse(volumeRoot, null, out var segments, out var parseError))
            {
                error = "The volume folder is not a valid path inside the import share: " + parseError;
                return false;
            }

            var walked = Walk(share, segments, share, followLinks: false);
            if (walked.Path is null || !Directory.Exists(walked.Path))
            {
                error = walked.Rejected
                    ? "The volume folder leads outside the import share."
                    : $"The volume folder '{VolumePath.Display(segments)}' does not exist in the import share.";
                return false;
            }

            root = walked.Path;
        }

        volume = new ImportVolume(root);
        error = null;
        return true;
    }

    /// <summary>The volume of an import: <paramref name="volumeRoot"/> inside the configured share.</summary>
    public static bool TryOpen(ImportVolumeOptions options, string? volumeRoot, out ImportVolume? volume, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TryOpenCore(options.VolumeShareRoot, volumeRoot, out volume, out error);
    }

    /// <summary>
    /// The E08-T05 shape: false with the reason when the path is refused; true with the file to open otherwise, which
    /// may not exist (the caller reports a missing file).
    /// </summary>
    public bool TryResolve(string loadFilePath, string? stripPrefix, out string? fullPath, out string? error)
    {
        var file = Resolve(loadFilePath, stripPrefix);
        error = file.Reason;
        fullPath = file.Status switch
        {
            VolumeFileStatus.Found => file.FullPath,
            VolumeFileStatus.Missing => Path.Combine([Root, .. file.RelativePath!.Split('/')]),
            _ => null,
        };
        return file.Status != VolumeFileStatus.Rejected;
    }

    /// <summary>Resolves a load-file path; never throws for a bad or hostile path.</summary>
    public VolumeFile Resolve(string? loadFilePath, string? stripPrefix = null)
    {
        if (!VolumePath.TryParse(loadFilePath, stripPrefix, out var segments, out var error))
        {
            return new VolumeFile(VolumeFileStatus.Rejected, null, null, error);
        }

        var relative = VolumePath.Display(segments);
        WalkResult walked;
        try
        {
            walked = Walk(Root, segments, Root, followLinks: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException)
        {
            return new VolumeFile(VolumeFileStatus.Missing, relative, null, $"'{relative}' cannot be read: {ex.GetType().Name}.");
        }

        if (walked.Rejected)
        {
            return new VolumeFile(VolumeFileStatus.Rejected, relative, null, $"'{relative}' is or passes through a symbolic link, which is never followed in a volume.");
        }

        if (walked.Path is not { } path || !File.Exists(path))
        {
            return new VolumeFile(VolumeFileStatus.Missing, relative, null, $"'{relative}' is not in the volume.");
        }

        return new VolumeFile(VolumeFileStatus.Found, relative, path, null);
    }

    private readonly record struct WalkResult(string? Path, bool Rejected);

    /// <summary>
    /// Component-wise resolution from <paramref name="start"/> (itself a real path): links are expanded in place and
    /// <c>..</c> from link targets pops the real path, so the result is the true real path. With
    /// <paramref name="containment"/>, a result outside it is rejected (intermediate steps may pass outside, e.g. an
    /// absolute link that points back in). Null path: something does not exist. Without <paramref name="followLinks"/>
    /// any link on the path rejects it.
    /// </summary>
    private static WalkResult Walk(string start, IReadOnlyList<string> segments, string? containment, bool followLinks)
    {
        var pending = new Stack<string>(segments.Reverse());
        var current = start;
        var hops = 0;
        while (pending.TryPop(out var segment))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var candidate = Path.Join(current, segment);
            if (!Exists(candidate))
            {
                if (CaseInsensitiveMatch(current, segment) is not { } match)
                {
                    return new WalkResult(null, false);
                }

                candidate = match;
            }

            var link = new FileInfo(candidate).LinkTarget;
            if (link is null)
            {
                current = candidate;
                continue;
            }

            if (!followLinks)
            {
                return new WalkResult(null, true);
            }

            if (++hops > MaxLinkHops)
            {
                return new WalkResult(null, true);
            }

            foreach (var part in Split(link).Reverse())
            {
                pending.Push(part);
            }

            if (Path.IsPathRooted(link))
            {
                current = Path.GetPathRoot(link)!;
            }
        }

        if (containment is not null && !IsWithin(containment, current))
        {
            return new WalkResult(null, true);
        }

        return new WalkResult(current, false);
    }

    private static bool Exists(string path)
    {
        var info = new FileInfo(path);
        return info.Exists || Directory.Exists(path) || info.LinkTarget is not null;
    }

    private static string? CaseInsensitiveMatch(string directory, string name)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string? found = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
            {
                if (found is not null)
                {
                    return null; // ambiguous
                }

                found = entry;
            }
        }

        return found;
    }

    /// <summary>The components of <paramref name="path"/> after its root, if any.</summary>
    private static string[] Split(string path) =>
        path[(Path.GetPathRoot(path)?.Length ?? 0)..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsWithin(string root, string path)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(r, PathComparison)
            || (path.StartsWith(r, PathComparison) && path.Length > r.Length && path[r.Length] == Path.DirectorySeparatorChar)
            || (r.Length == 0 && path.StartsWith(Path.DirectorySeparatorChar));
    }
}
