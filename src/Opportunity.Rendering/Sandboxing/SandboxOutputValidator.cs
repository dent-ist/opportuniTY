using System.Buffers.Binary;

using Opportunity.Core.Pages;
using Opportunity.Rendering.Renderers;

namespace Opportunity.Rendering.Sandboxing;

/// <summary>
/// Checks a page the sandboxed renderer reports before the worker uploads anything (the child is untrusted): the file
/// is a regular file directly in the request's output directory (no path, no link), within the size limit, and a PNG
/// whose header matches the reported dimensions. Only PNG is produced, so nothing else is ever stored as a rendition.
/// </summary>
internal static class SandboxOutputValidator
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private const int MaxDimension = 100_000;

    /// <summary>The validated page with full paths, or null when anything is off.</summary>
    public static RenderedPage? Validate(SandboxPage page, string outputDirectory, IReadOnlyList<int>? requestedPages, long maxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Index < 0 || (requestedPages is not null && !requestedPages.Contains(page.Index))
            || !(page.WidthPt is > 0 and <= 1_000_000) || !(page.HeightPt is > 0 and <= 1_000_000)
            || !Enum.IsDefined(page.ColorMode)
            || (page.Error is not null && !RenderErrorCodesSet.IsKnown(page.Error)))
        {
            return null;
        }

        RasterFile? review = null, thumbnail = null;
        if (page.Review is { } r && (review = File(r, outputDirectory, maxFileBytes)) is null)
        {
            return null;
        }

        if (page.Thumbnail is { } t && (thumbnail = File(t, outputDirectory, maxFileBytes)) is null)
        {
            return null;
        }

        return new RenderedPage(page.Index, page.WidthPt, page.HeightPt, page.ColorMode, review, thumbnail, page.Error);
    }

    private static RasterFile? File(SandboxRaster raster, string outputDirectory, long maxFileBytes)
    {
        var name = raster.FileName;
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || raster.Format != PageImageFormat.Png
            || raster.WidthPx is < 1 or > MaxDimension || raster.HeightPx is < 1 or > MaxDimension || raster.Dpi is < 1 or > 10_000)
        {
            return null;
        }

        var path = Path.Combine(outputDirectory, name);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || info.Length > maxFileBytes || info.Length < 33)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[24];
        using (var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Options = FileOptions.None }))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            {
                return null;
            }
        }

        // Signature, then the IHDR chunk: length 13, type, width, height (big endian).
        if (!header[..8].SequenceEqual(PngSignature) || BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != 13 || !header[12..16].SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(header[16..]) != (uint)raster.WidthPx || BinaryPrimitives.ReadUInt32BigEndian(header[20..]) != (uint)raster.HeightPx)
        {
            return null;
        }

        return new RasterFile(path, raster.WidthPx, raster.HeightPx, raster.Dpi, raster.Format);
    }
}

/// <summary>The codes a sandbox may report; anything else from the child is a protocol violation.</summary>
internal static class RenderErrorCodesSet
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        RenderErrorCodes.Unsupported,
        RenderErrorCodes.Unreadable,
        RenderErrorCodes.Encrypted,
        RenderErrorCodes.TooManyPages,
        RenderErrorCodes.PageTooLarge,
        RenderErrorCodes.PageFailed,
        RenderErrorCodes.AspectMismatch,
        RenderErrorCodes.MemoryLimit,
    };

    public static bool IsKnown(string code) => Known.Contains(code);
}
