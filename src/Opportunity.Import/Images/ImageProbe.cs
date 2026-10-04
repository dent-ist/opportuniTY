using System.Buffers.Binary;

using Opportunity.Core.Pages;

namespace Opportunity.Import.Images;

/// <summary>One page raster of an image file: pixel size, resolution and color mode.</summary>
public sealed record ImageFrame(int WidthPx, int HeightPx, int DpiX, int DpiY, PageColorMode ColorMode)
{
    /// <summary>Page width in 1/72-inch points (ADR-012 §1.3).</summary>
    public decimal WidthPt => Math.Round(WidthPx * 72m / DpiX, 2);

    public decimal HeightPt => Math.Round(HeightPx * 72m / DpiY, 2);
}

/// <summary>What <see cref="ImageProbe"/> found: the format (sniffed from magic bytes, never from a file name) and frames.</summary>
public sealed record ImageInfo(PageImageFormat Format, string ContentType, IReadOnlyList<ImageFrame> Frames, int? TiffCompression = null);

/// <summary>
/// Reads the headers of the page image formats an OPT references (ADR-012 §1.4: TIFF, JPEG, PNG): dimensions, DPI and
/// color mode of every frame, without decoding pixels. Offsets come from the file and are bounds-checked; a malformed or
/// unsupported file yields null. A TIFF's reduced-resolution (thumbnail) subfiles are not pages.
/// </summary>
public static class ImageProbe
{
    /// <summary>Frames read from one multi-page TIFF at most.</summary>
    public const int MaxFrames = 10_000;

    /// <summary>Resolution assumed when a file declares none (the TIFF default).</summary>
    public const int DefaultDpi = 72;

    private const int MaxDimension = 1_000_000;

    // page.width_pt / height_pt are numeric(8, 2).
    private const decimal MaxPoints = 999_999m;

    public static ImageInfo? Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The image stream must be seekable.", nameof(stream));
        }

        try
        {
            Span<byte> magic = stackalloc byte[8];
            stream.Position = 0;
            var read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
            magic = magic[..read];
            ImageInfo? info = magic switch
            {
                [(byte)'I', (byte)'I', 42, 0, ..] => Tiff(stream, littleEndian: true),
                [(byte)'M', (byte)'M', 0, 42, ..] => Tiff(stream, littleEndian: false),
                [0xFF, 0xD8, 0xFF, ..] => Jpeg(stream),
                [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A] => Png(stream),
                _ => null,
            };
            return info is { Frames.Count: > 0 } && info.Frames.All(Valid) ? info : null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static bool Valid(ImageFrame f) =>
        f.WidthPx is > 0 and <= MaxDimension && f.HeightPx is > 0 and <= MaxDimension
        && f.DpiX is > 0 and <= 100_000 && f.DpiY is > 0 and <= 100_000
        && f.WidthPt is > 0 and <= MaxPoints && f.HeightPt is > 0 and <= MaxPoints;

    // ---- TIFF -----------------------------------------------------------------------------------------------------

    private static ImageInfo? Tiff(Stream s, bool littleEndian)
    {
        var length = s.Length;
        var frames = new List<ImageFrame>();
        var visited = new HashSet<long>();
        int? compression = null;
        long ifd = U32(s, 4, littleEndian);
        while (ifd != 0)
        {
            if (ifd < 8 || ifd + 2 > length || !visited.Add(ifd) || visited.Count > MaxFrames * 2)
            {
                return null;
            }

            var count = U16(s, ifd, littleEndian);
            if (ifd + 2 + (count * 12L) + 4 > length)
            {
                return null;
            }

            long width = 0, height = 0, subfileType = 0, photometric = -1, bits = 1, unit = 2, frameCompression = 1;
            double xres = 0, yres = 0;
            for (var i = 0; i < count; i++)
            {
                var entry = ifd + 2 + (i * 12L);
                var tag = U16(s, entry, littleEndian);
                var type = U16(s, entry + 2, littleEndian);
                var n = U32(s, entry + 4, littleEndian);
                switch (tag)
                {
                    case 254: subfileType = Scalar(s, entry, type, littleEndian); break;
                    case 256: width = Scalar(s, entry, type, littleEndian); break;
                    case 257: height = Scalar(s, entry, type, littleEndian); break;
                    case 258: bits = n >= 1 ? Scalar(s, entry, type, littleEndian, n) : 1; break;
                    case 259: frameCompression = Scalar(s, entry, type, littleEndian); break;
                    case 262: photometric = Scalar(s, entry, type, littleEndian); break;
                    case 282: xres = Rational(s, entry, type, littleEndian); break;
                    case 283: yres = Rational(s, entry, type, littleEndian); break;
                    case 296: unit = Scalar(s, entry, type, littleEndian); break;
                }
            }

            // Bit 0 of NewSubfileType marks a reduced-resolution image (a thumbnail), not a page.
            if ((subfileType & 1) == 0)
            {
                if (frames.Count >= MaxFrames)
                {
                    return null;
                }

                compression ??= (int)frameCompression;
                var color = photometric is 0 or 1 ? (bits <= 1 ? PageColorMode.Bitonal : PageColorMode.Gray) : PageColorMode.Color;
                frames.Add(new ImageFrame((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue),
                    Dpi(xres, unit), Dpi(yres, unit), color));
            }

            ifd = U32(s, ifd + 2 + (count * 12L), littleEndian);
        }

        return new ImageInfo(PageImageFormat.TiffG4, "image/tiff", frames, compression);
    }

    private static int Dpi(double value, long unit) => unit switch
    {
        _ when value <= 0 || double.IsNaN(value) || value > 100_000 => DefaultDpi,
        3 => Math.Max(1, (int)Math.Round(value * 2.54)),
        2 => Math.Max(1, (int)Math.Round(value)),
        _ => DefaultDpi,
    };

    /// <summary>A SHORT or LONG value (the first of <paramref name="count"/> values), inline or at its offset.</summary>
    private static long Scalar(Stream s, long entry, int type, bool le, long count = 1)
    {
        var size = type == 3 ? 2 : 4;
        var at = size * count <= 4 ? entry + 8 : U32(s, entry + 8, le);
        return type switch
        {
            3 => U16(s, at, le),
            4 => U32(s, at, le),
            _ => 0,
        };
    }

    private static double Rational(Stream s, long entry, int type, bool le)
    {
        if (type != 5)
        {
            return Scalar(s, entry, type, le);
        }

        var at = U32(s, entry + 8, le);
        var num = U32(s, at, le);
        var den = U32(s, at + 4, le);
        return den == 0 ? 0 : (double)num / den;
    }

    private static ushort U16(Stream s, long at, bool le)
    {
        Span<byte> b = stackalloc byte[2];
        ReadAt(s, at, b);
        return le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private static uint U32(Stream s, long at, bool le)
    {
        Span<byte> b = stackalloc byte[4];
        ReadAt(s, at, b);
        return le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static void ReadAt(Stream s, long at, Span<byte> buffer)
    {
        if (at < 0 || at + buffer.Length > s.Length)
        {
            throw new EndOfStreamException();
        }

        s.Position = at;
        s.ReadExactly(buffer);
    }

    // ---- JPEG -----------------------------------------------------------------------------------------------------

    private static ImageInfo? Jpeg(Stream s)
    {
        long at = 2;
        int dpiX = 0, dpiY = 0;
        Span<byte> header = stackalloc byte[4];
        Span<byte> jfif = stackalloc byte[12];
        Span<byte> sof = stackalloc byte[6];
        while (at + 4 <= s.Length)
        {
            ReadAt(s, at, header[..2]);
            if (header[0] != 0xFF)
            {
                return null;
            }

            var marker = header[1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null;
            }

            ReadAt(s, at + 2, header[2..4]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header[2..4]);
            if (length < 2)
            {
                return null;
            }

            if (marker == 0xE0 && length >= 16)
            {
                ReadAt(s, at + 4, jfif);
                if (jfif[..5].SequenceEqual("JFIF\0"u8))
                {
                    var units = jfif[7];
                    var x = BinaryPrimitives.ReadUInt16BigEndian(jfif[8..10]);
                    var y = BinaryPrimitives.ReadUInt16BigEndian(jfif[10..12]);
                    (dpiX, dpiY) = units switch
                    {
                        1 => (x, y),
                        2 => ((int)Math.Round(x * 2.54), (int)Math.Round(y * 2.54)),
                        _ => (0, 0),
                    };
                }
            }
            else if (marker is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF))
            {
                ReadAt(s, at + 4, sof);
                var height = BinaryPrimitives.ReadUInt16BigEndian(sof[1..3]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(sof[3..5]);
                var color = sof[5] == 1 ? PageColorMode.Gray : PageColorMode.Color;
                return new ImageInfo(PageImageFormat.Jpeg, "image/jpeg",
                    [new ImageFrame(width, height, dpiX > 0 ? dpiX : DefaultDpi, dpiY > 0 ? dpiY : DefaultDpi, color)]);
            }

            at += 2 + length;
        }

        return null;
    }

    // ---- PNG ------------------------------------------------------------------------------------------------------

    private static ImageInfo? Png(Stream s)
    {
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> ihdr = stackalloc byte[13];
        Span<byte> phys = stackalloc byte[9];
        ReadAt(s, 8, chunk);
        if (!chunk[4..].SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(chunk[..4]) != 13)
        {
            return null;
        }

        ReadAt(s, 16, ihdr);
        var width = BinaryPrimitives.ReadUInt32BigEndian(ihdr[..4]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..8]);
        var bitDepth = ihdr[8];
        var color = ihdr[9] switch
        {
            0 => bitDepth == 1 ? PageColorMode.Bitonal : PageColorMode.Gray,
            3 when bitDepth == 1 => PageColorMode.Bitonal,
            4 => PageColorMode.Gray,
            _ => PageColorMode.Color,
        };

        int dpiX = 0, dpiY = 0;
        long at = 8 + 8 + 13 + 4;
        for (var chunks = 0; at + 8 <= s.Length && chunks < 10_000; chunks++)
        {
            ReadAt(s, at, chunk);
            var length = BinaryPrimitives.ReadUInt32BigEndian(chunk[..4]);
            if (chunk[4..].SequenceEqual("IDAT"u8) || chunk[4..].SequenceEqual("IEND"u8))
            {
                break;
            }

            if (chunk[4..].SequenceEqual("pHYs"u8) && length == 9)
            {
                ReadAt(s, at + 8, phys);
                if (phys[8] == 1)
                {
                    dpiX = (int)Math.Round(BinaryPrimitives.ReadUInt32BigEndian(phys[..4]) * 0.0254);
                    dpiY = (int)Math.Round(BinaryPrimitives.ReadUInt32BigEndian(phys[4..8]) * 0.0254);
                }
            }

            at += 12L + length;
        }

        return new ImageInfo(PageImageFormat.Png, "image/png",
            [new ImageFrame((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue),
                dpiX > 0 ? dpiX : DefaultDpi, dpiY > 0 ? dpiY : DefaultDpi, color)]);
    }
}
