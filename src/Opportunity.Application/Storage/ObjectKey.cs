using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Opportunity.Application.Storage;

/// <summary>What an object is, derived from its key layout (ADR-011 §1.5).</summary>
public enum ObjectArea
{
    Native,
    Text,
    Image,
    Rendition,
    ImportSource,
    ImportUpload,
    SnapshotManifest,
    Export,
    Production,
    Report,
    Tmp,
    Certificate,
    Audit,
}

/// <summary>
/// A validated logical object key (ADR-011 §1). Keys use the grammar <c>[a-z0-9._-]</c> segments joined by <c>/</c>, are
/// at most 512 bytes, live under <c>ws/{workspaceId}/</c> or <c>sys/</c>, and must match one of the area layouts. They
/// never contain user- or load-file-supplied strings; build them with <see cref="ObjectKeys"/>.
/// </summary>
public sealed class ObjectKey : IEquatable<ObjectKey>
{
    private ObjectKey(string value, ObjectArea area, Guid? workspaceId, Guid? documentId, Sha256Digest? contentSha256)
    {
        Value = value;
        Area = area;
        WorkspaceId = workspaceId;
        DocumentId = documentId;
        ContentSha256 = contentSha256;
    }

    public string Value { get; }

    public ObjectArea Area { get; }

    /// <summary>The owning workspace; null for installation-level (<c>sys/</c>) objects.</summary>
    public Guid? WorkspaceId { get; }

    public Guid? DocumentId { get; }

    /// <summary>For content-addressed areas, the SHA-256 the bytes must have.</summary>
    public Sha256Digest? ContentSha256 { get; }

    public bool IsContentAddressed => ContentSha256 is not null;

    /// <summary>Originals are never modified or deleted except by an ADR-014 deletion (ADR-011 §4.2).</summary>
    public bool IsOriginal => Area is ObjectArea.Native or ObjectArea.Text or ObjectArea.Image or ObjectArea.ImportSource;

    public static ObjectKey Parse(string value) =>
        TryParse(value, out var key) ? key : throw new ArgumentException("Not a valid object key (ADR-011 §1).", nameof(value));

    public static bool TryParse(string? value, [NotNullWhen(true)] out ObjectKey? key)
    {
        key = null;
        var s = ObjectKeyGrammar.SplitSegments(value, trailingSlash: false);
        if (s is null)
        {
            return false;
        }

        key = s[0] switch
        {
            ObjectKeyGrammar.WorkspaceRoot when s.Length >= 4 && ObjectKeyGrammar.IsId(s[1]) => ParseWorkspace(value!, s),
            ObjectKeyGrammar.SystemRoot when s.Length >= 3 => ParseSystem(value!, s),
            _ => null,
        };
        return key is not null;
    }

    public bool IsUnder(ObjectPrefix prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return Value.StartsWith(prefix.Value, StringComparison.Ordinal);
    }

    private static ObjectKey? ParseWorkspace(string value, string[] s)
    {
        var ws = Guid.ParseExact(s[1], "N");
        switch (s[2])
        {
            case "docs" when s.Length == 6 && ObjectKeyGrammar.IsId(s[3]) && ObjectKeyGrammar.IsSha256(s[5]):
                ObjectArea? area = s[4] switch
                {
                    "native" => ObjectArea.Native,
                    "text" => ObjectArea.Text,
                    "image" => ObjectArea.Image,
                    _ => null,
                };
                return area is null ? null : new(value, area.Value, ws, Guid.ParseExact(s[3], "N"), Sha256Digest.Parse(s[5]));
            case "docs" when s.Length == 7 && ObjectKeyGrammar.IsId(s[3]) && s[4] == "rend" && ObjectKeyGrammar.IsId(s[5]):
                return new(value, ObjectArea.Rendition, ws, Guid.ParseExact(s[3], "N"), null);
            case "imports" when s.Length == 6 && ObjectKeyGrammar.IsId(s[3]) && s[4] == "source" && ObjectKeyGrammar.IsSha256(s[5]):
                return new(value, ObjectArea.ImportSource, ws, null, Sha256Digest.Parse(s[5]));
            case "imports" when s.Length == 6 && ObjectKeyGrammar.IsId(s[3]) && s[4] == "upload" && ObjectKeyGrammar.IsId(s[5]):
                return new(value, ObjectArea.ImportUpload, ws, null, null);
            case "snapshots" when s.Length == 5 && ObjectKeyGrammar.IsId(s[3]) && IsChunkName(s[4]):
                return new(value, ObjectArea.SnapshotManifest, ws, null, null);
            case "exports" when s.Length >= 6 && ObjectKeyGrammar.IsId(s[3]) && ObjectKeyGrammar.IsId(s[4]):
                return new(value, ObjectArea.Export, ws, null, null);
            case "productions" when s.Length >= 7 && ObjectKeyGrammar.IsId(s[3]) && IsVersion(s[4]) && ObjectKeyGrammar.IsId(s[5]):
                return new(value, ObjectArea.Production, ws, null, null);
            case "reports" when s.Length == 5 && ObjectKeyGrammar.IsId(s[3]) && ObjectKeyGrammar.IsSha256(s[4]):
                return new(value, ObjectArea.Report, ws, null, Sha256Digest.Parse(s[4]));
            case "tmp" when s.Length >= 5 && ObjectKeyGrammar.IsId(s[3]):
                return new(value, ObjectArea.Tmp, ws, null, null);
            default:
                return null;
        }
    }

    private static ObjectKey? ParseSystem(string value, string[] s) => s[1] switch
    {
        "certificates" when s.Length == 4 && ObjectKeyGrammar.IsId(s[2]) && ObjectKeyGrammar.IsSha256(s[3]) =>
            new(value, ObjectArea.Certificate, null, null, Sha256Digest.Parse(s[3])),
        "audit" => new(value, ObjectArea.Audit, null, null, null),
        _ => null,
    };

    private static bool IsChunkName(string segment) =>
        segment.Length == 10 && segment.EndsWith(".bin", StringComparison.Ordinal) && segment[..6].All(char.IsAsciiDigit);

    private static bool IsVersion(string segment) =>
        segment.Length > 1 && segment[0] == 'v' && segment[1..].All(char.IsAsciiDigit)
        && int.TryParse(segment[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0;

    public bool Equals(ObjectKey? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ObjectKey);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static bool operator ==(ObjectKey? left, ObjectKey? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ObjectKey? left, ObjectKey? right) => !(left == right);
}
