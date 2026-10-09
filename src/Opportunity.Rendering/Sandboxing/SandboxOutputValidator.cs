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

/// <summary>
/// Checks an endorsed page (E12-T04) the sandboxed endorser reports and reads it while the child is stopped: exactly the
/// expected file name in the request's output directory, a regular file within the size limit, the requested format,
/// and an image header (PNG IHDR, JPEG SOF, TIFF IFD with CCITT Group 4) whose dimensions match the report.
/// </summary>
internal static class EndorsedOutputValidator
{
    private const int MaxDimension = 100_000;

    public static Endorsing.EndorsedImage? Read(SandboxEndorsed reported, string outputDirectory, PageImageFormat format, long maxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(reported);
        if (reported.FileName != Endorsing.PageEndorser.FileName(format) || reported.Format != format
            || reported.WidthPx is < 1 or > MaxDimension || reported.HeightPx is < 1 or > MaxDimension || reported.Dpi is < 1 or > 10_000
            || !Enum.IsDefined(reported.ColorMode))
        {
            return null;
        }

        var path = Path.Combine(outputDirectory, reported.FileName);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || info.Length > maxFileBytes || info.Length < 16)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        var size = format switch
        {
            PageImageFormat.Png => PngSize(bytes),
            PageImageFormat.Jpeg => JpegSize(bytes),
            PageImageFormat.TiffG4 => TiffG4Size(bytes),
            _ => null,
        };
        return size == (reported.WidthPx, reported.HeightPx)
            ? new Endorsing.EndorsedImage(bytes, reported.WidthPx, reported.HeightPx, reported.Dpi, format, reported.ColorMode)
            : null;
    }

    private static (int, int)? PngSize(ReadOnlySpan<byte> b) =>
        b.Length >= 24 && b[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])
            && BinaryPrimitives.ReadUInt32BigEndian(b[8..]) == 13 && b[12..16].SequenceEqual("IHDR"u8)
            ? ((int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(b[16..])), (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(b[20..])))
            : null;

    /// <summary>The frame size from the first SOFn marker (bounded walk over the marker segments).</summary>
    private static (int, int)? JpegSize(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
        {
            return null;
        }

        var i = 2;
        for (var segments = 0; segments < 256 && i + 4 <= b.Length; segments++)
        {
            if (b[i] != 0xFF)
            {
                return null;
            }

            var marker = b[i + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            if (length < 2 || i + 2 + length > b.Length)
            {
                return null;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return length >= 7 ? (BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..])) : null;
            }

            i += 2 + length;
        }

        return null;
    }

    /// <summary>Width and length of the first IFD, which must be CCITT Group 4 (compression 4).</summary>
    private static (int, int)? TiffG4Size(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8)
        {
            return null;
        }

        var little = b[0] == 'I' && b[1] == 'I';
        if (!little && !(b[0] == 'M' && b[1] == 'M'))
        {
            return null;
        }

        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
        var ifd = U32(b[4..]);
        if (U16(b[2..]) != 42 || ifd < 8 || ifd > b.Length - 2)
        {
            return null;
        }

        var count = U16(b[(int)ifd..]);
        if (count > 512 || ifd + 2 + count * 12L > b.Length)
        {
            return null;
        }

        long width = -1, height = -1, compression = -1;
        for (var e = 0; e < count; e++)
        {
            var entry = b.Slice((int)ifd + 2 + e * 12, 12);
            var tag = U16(entry);
            var type = U16(entry[2..]);
            long value = type switch
            {
                3 => U16(entry[8..]),
                4 => U32(entry[8..]),
                _ => -1,
            };
            switch (tag)
            {
                case 256: width = value; break;
                case 257: height = value; break;
                case 259: compression = value; break;
            }
        }

        return compression == 4 && width is >= 1 and <= MaxDimension && height is >= 1 and <= MaxDimension ? ((int)width, (int)height) : null;
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
