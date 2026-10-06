using System.Security.Cryptography;
using System.Text;

using Opportunity.Rendering.Renderers;

namespace Opportunity.Rendering.Jobs;

/// <summary>
/// Deterministic identifiers of render output (E11-T02: "deterministic output keys"). The same source rendered by the
/// same renderer version with the same settings always gets the same Rendered page set id and rendition id, so a retried
/// or repeated chunk writes the same object keys (write-once objects accept identical re-puts) and the same rows
/// (inserts ignore existing rows). A new renderer version or new settings yield new ids, i.e. a new rendition
/// (ADR-011 §4.3). The ids are name-based UUIDs (RFC 9562 version 8 layout) over SHA-256.
/// </summary>
public static class RenderIds
{
    /// <summary>The Rendered page set (and its rendition id) of a document's native.</summary>
    public static Guid NativePageSet(Guid documentId, byte[] nativeSha256, RendererIdentity renderer)
    {
        ArgumentNullException.ThrowIfNull(nativeSha256);
        ArgumentNullException.ThrowIfNull(renderer);
        return Create("native", documentId.ToString("N"), Convert.ToHexStringLower(nativeSha256), renderer);
    }

    /// <summary>The rendition holding the derived rasters of an Imported page set's pages.</summary>
    public static Guid ImportedRendition(Guid pageSetId, RendererIdentity renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return Create("imported", pageSetId.ToString("N"), string.Empty, renderer);
    }

    private static Guid Create(string kind, string subject, string source, RendererIdentity renderer)
    {
        var name = string.Join('\n', "opportunity-render", kind, subject, source, renderer.Name, renderer.Version, Convert.ToHexStringLower(renderer.SettingsHash));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
