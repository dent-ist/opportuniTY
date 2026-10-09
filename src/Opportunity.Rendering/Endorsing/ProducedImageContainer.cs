using System.Buffers.Binary;

using Opportunity.Core.Pages;

namespace Opportunity.Rendering.Endorsing;

/// <summary>
/// Checks that a produced page file is one plain image and nothing else (E12-T06): no second frame or sub-image, no
/// metadata, thumbnail, comment or text segment, no bytes after the image. Such places are where an unredacted copy, a
/// thumbnail of the original or extracted text would hide in an otherwise clean raster. The rules match exactly what
/// <see cref="PageEndorser"/> writes (TIFF: the baseline tags of a single-strip bilevel image; JPEG: JFIF and the scan;
/// PNG: the critical chunks and resolution), so anything else is a finding.
/// </summary>
internal static class ProducedImageContainer
{
    // NewSubfileType, SubfileType, ImageWidth, ImageLength, BitsPerSample, Compression, Photometric, FillOrder, StripOffsets,
    // Orientation, SamplesPerPixel, RowsPerStrip, StripByteCounts, XResolution, YResolution, PlanarConfig, ResolutionUnit,
    // T4Options, T6Options.
    private static readonly HashSet<ushort> TiffTags = [254, 255, 256, 257, 258, 259, 262, 266, 273, 274, 277, 278, 279, 282, 283, 284, 292, 293, 296];

    private static readonly HashSet<string> PngChunks = new(StringComparer.Ordinal) { "IHDR", "PLTE", "IDAT", "IEND", "pHYs", "sRGB", "gAMA", "cHRM", "sBIT", "iCCP" };

    /// <summary>Null when the file is one plain image of <paramref name="format"/>; otherwise why not (content-free).</summary>
    public static string? Inspect(byte[] file, PageImageFormat format)
    {
        try
        {
            return format switch
            {
                PageImageFormat.TiffG4 => Tiff(file),
                PageImageFormat.Jpeg => Jpeg(file),
                PageImageFormat.Png => Png(file),
                _ => "unsupported format",
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return "truncated";
        }
    }

    private static string? Tiff(byte[] file)
    {
        if (file.Length < 8)
        {
            return "truncated";
        }

        var little = file[0] == (byte)'I' && file[1] == (byte)'I';
        if (!little && !(file[0] == (byte)'M' && file[1] == (byte)'M'))
        {
            return "not a TIFF";
        }

        ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at, 2));
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at, 4));
        if (U16(2) != 42)
        {
            return "not a classic TIFF";
        }

        var ifd = checked((int)U32(4));
        var count = U16(ifd);
        long end = ifd + 2 + 12L * count + 4;
        long stripOffset = -1, stripBytes = -1;
        var compression = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + 12 * i;
            var tag = U16(entry);
            var type = U16(entry + 2);
            var values = U32(entry + 4);
            if (!TiffTags.Contains(tag))
            {
                return "unexpected TIFF tag " + tag.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var size = type switch { 1 or 2 or 6 or 7 => 1L, 3 or 8 => 2L, 4 or 9 or 11 => 4L, 5 or 10 or 12 => 8L, _ => -1L } * values;
            if (size < 0)
            {
                return "unexpected TIFF field type";
            }

            if (size > 4)
            {
                end = Math.Max(end, U32(entry + 8) + size);
            }

            uint Value() => type == 3 ? U16(entry + 8) : U32(entry + 8);
            switch (tag)
            {
                case 259:
                    compression = (int)Value();
                    break;
                case 273 when values == 1:
                    stripOffset = Value();
                    break;
                case 279 when values == 1:
                    stripBytes = Value();
                    break;
                case 273 or 279:
                    return "more than one strip";
            }
        }

        if (U32(ifd + 2 + 12 * count) != 0)
        {
            return "more than one image";
        }

        if (compression != 4)
        {
            return "not CCITT Group 4";
        }

        if (stripOffset < 0 || stripBytes < 0 || stripOffset + stripBytes > file.Length)
        {
            return "strip outside the file";
        }

        end = Math.Max(end, stripOffset + stripBytes);

        // Word alignment may add one byte after a value or the strip; anything more is unreferenced data.
        return file.Length > end + 1 ? "unreferenced bytes" : null;
    }

    private static string? Jpeg(ReadOnlySpan<byte> file)
    {
        if (file.Length < 4 || file[0] != 0xFF || file[1] != 0xD8)
        {
            return "not a JPEG";
        }

        var at = 2;
        while (true)
        {
            if (file[at] != 0xFF)
            {
                return "invalid marker";
            }

            var marker = file[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            if (marker == 0xD9)
            {
                return at + 2 == file.Length ? null : "bytes after the image";
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(file.Slice(at + 2, 2));
            var segment = file.Slice(at + 4, length - 2);
            switch (marker)
            {
                case 0xE0 when segment.Length == 14 && segment[..5].SequenceEqual("JFIF\0"u8):
                    // JFIF without a thumbnail (its width and height are 0).
                    if (segment[12] != 0 || segment[13] != 0)
                    {
                        return "JFIF thumbnail";
                    }

                    break;
                case 0xDB or 0xC4 or 0xDD or 0xC0 or 0xC1 or 0xC2:
                    break;
                case 0xDA:
                    // Entropy-coded data runs to the next marker that is not a stuffed byte or a restart marker.
                    at += 2 + length;
                    while (!(file[at] == 0xFF && file[at + 1] != 0x00 && file[at + 1] is not (>= 0xD0 and <= 0xD7)))
                    {
                        at++;
                    }

                    continue;
                default:
                    return "unexpected JPEG segment " + marker.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
            }

            at += 2 + length;
        }
    }

    private static string? Png(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8 || !file[..8].SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "not a PNG";
        }

        var at = 8;
        while (true)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.Slice(at, 4)));
            var type = System.Text.Encoding.ASCII.GetString(file.Slice(at + 4, 4));
            if (!PngChunks.Contains(type))
            {
                return "unexpected PNG chunk " + type;
            }

            at = checked(at + 12 + length);
            if (type == "IEND")
            {
                return at == file.Length ? null : "bytes after the image";
            }
        }
    }
}
