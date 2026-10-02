namespace Opportunity.Application.Storage;

/// <summary>Low-level rules shared by <see cref="ObjectKey"/> and <see cref="ObjectPrefix"/> (ADR-011 §1.2 and §1.4).</summary>
internal static class ObjectKeyGrammar
{
    public const int MaxKeyBytes = 512;

    public const string WorkspaceRoot = "ws";
    public const string SystemRoot = "sys";

    /// <summary>Second-level areas under <c>ws/{workspaceId}/</c>.</summary>
    public static readonly IReadOnlySet<string> WorkspaceAreas = new HashSet<string>(StringComparer.Ordinal)
    {
        "docs", "imports", "snapshots", "exports", "productions", "reports", "tmp",
    };

    /// <summary>Areas under <c>sys/</c>.</summary>
    public static readonly IReadOnlySet<string> SystemAreas = new HashSet<string>(StringComparer.Ordinal)
    {
        "certificates", "audit",
    };

    /// <summary>Lowercase ASCII <c>[a-z0-9._-]+</c>, not <c>.</c> or <c>..</c>.</summary>
    public static bool IsValidSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || segment is "." || segment is "..")
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A 32-character lowercase hex GUID in <c>N</c> format.</summary>
    public static bool IsId(string segment) =>
        segment.Length == 32 && segment.All(Sha256Digest.IsLowerHex) && Guid.TryParseExact(segment, "N", out _);

    public static bool IsSha256(string segment) => Sha256Digest.TryParse(segment, out _);

    public static string Id(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An object-key identifier must not be the empty GUID.", nameof(id));
        }

        return id.ToString("N");
    }

    /// <summary>Splits and validates the segment grammar; null when the string is not grammatical.</summary>
    public static string[]? SplitSegments(string? value, bool trailingSlash)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxKeyBytes)
        {
            return null;
        }

        // Every allowed character is ASCII, so the character count equals the UTF-8 byte count once validated.
        if (trailingSlash != value.EndsWith('/'))
        {
            return null;
        }

        var body = trailingSlash ? value[..^1] : value;
        var segments = body.Split('/');
        foreach (var segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                return null;
            }
        }

        return segments;
    }
}
