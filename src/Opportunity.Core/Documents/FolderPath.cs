using System.Diagnostics.CodeAnalysis;

namespace Opportunity.Core.Documents;

/// <summary>
/// Imported folder paths (#179, Q-55): the read-only folder tree of a workspace is derived from the Folder Path a load
/// file maps for each document. Backslashes and slashes both separate folders; segments are trimmed and empty ones
/// (leading, trailing or doubled separators) dropped, so <c>\Mail\Inbox\</c>, <c>Mail/Inbox</c> and <c>/Mail//Inbox</c>
/// name the same folder, shown as <c>Mail\Inbox</c>. Folders match case-insensitively (as Windows paths do); the first
/// spelling loaded is kept for display.
/// </summary>
public static class FolderPath
{
    /// <summary>Display separator between folder names.</summary>
    public const char Separator = '\\';

    /// <summary>Deepest folder accepted (segments).</summary>
    public const int MaxDepth = 64;

    /// <summary>Longest folder name accepted (characters).</summary>
    public const int MaxNameLength = 255;

    /// <summary>Longest normalized path accepted (characters).</summary>
    public const int MaxPathLength = 4000;

    /// <summary>
    /// The folder names of <paramref name="raw"/>, outermost first. False, with a reason, when the path is too deep or
    /// long or holds a control character; true with no segments when it names no folder (blank or only separators).
    /// </summary>
    public static bool TryParse(string? raw, out IReadOnlyList<string> segments, [NotNullWhen(false)] out string? error)
    {
        segments = [];
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var names = new List<string>();
        foreach (var part in raw.Split(['\\', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Any(char.IsControl))
            {
                error = "the folder path holds a control character";
                return false;
            }

            if (part.Length > MaxNameLength)
            {
                error = $"a folder name is longer than {MaxNameLength} characters";
                return false;
            }

            names.Add(part);
        }

        if (names.Count > MaxDepth)
        {
            error = $"the folder path is deeper than {MaxDepth} folders";
            return false;
        }

        if (Join(names).Length > MaxPathLength)
        {
            error = $"the folder path is longer than {MaxPathLength} characters";
            return false;
        }

        segments = names;
        return true;
    }

    /// <summary>The display path of <paramref name="segments"/> (<c>Mail\Inbox</c>).</summary>
    public static string Join(IEnumerable<string> segments) => string.Join(Separator, segments);

    /// <summary>The case-insensitive key of a display path: two paths name the same folder exactly when their keys are equal.</summary>
    public static string Key(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.ToUpperInvariant();
    }

    /// <summary>The display paths of every folder on the way to <paramref name="segments"/>, outermost first, itself last.</summary>
    public static IReadOnlyList<string> Prefixes(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var prefixes = new List<string>(segments.Count);
        for (var i = 1; i <= segments.Count; i++)
        {
            prefixes.Add(Join(segments.Take(i)));
        }

        return prefixes;
    }
}
