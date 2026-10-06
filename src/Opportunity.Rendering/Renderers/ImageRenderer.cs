using BitMiracle.LibTiff.Classic;

using Opportunity.Core.Pages;
using Opportunity.Import.Images;

using SkiaSharp;

namespace Opportunity.Rendering.Renderers;

/// <summary>
/// Renders page images (TIFF of any compression including CCITT Group 4, JPEG, PNG) to review PNGs and thumbnails.
/// Frames are numbered exactly as OPT import numbers them (<see cref="ImageProbe"/>: a TIFF's reduced-resolution
/// subfiles are skipped), so frame <c>n</c> is the page row whose <c>SourceFrame</c> is <c>n</c>. A TIFF is decoded by
/// LibTiff.NET one frame at a time (bitonal strips line by line straight into 8-bit gray); JPEG and PNG by Skia. The
/// review raster keeps the source resolution unless the page exceeds the review bounds.
/// </summary>
internal static class ImageRenderer
{
    static ImageRenderer()
    {
        // LibTiff.NET writes warnings and errors to the console by default; failures surface as return values instead.
        Tiff.SetErrorHandler(new QuietTiffErrors());
    }

    public static IEnumerable<RenderedPage> Render(RenderRequest request, RenderSettings settings, CancellationToken cancellationToken)
    {
        ImageInfo info;
        using (var probe = File.OpenRead(request.InputPath))
        {
            info = ImageProbe.Probe(probe)
                ?? throw new RenderException(RenderErrorCodes.Unsupported, "The source is not a readable PDF, TIFF, JPEG or PNG.");
        }

        if (info.Frames.Count > settings.MaxPages)
        {
            throw new RenderException(RenderErrorCodes.TooManyPages, $"The image has more than {settings.MaxPages} pages.");
        }

        var indexes = request.Pages ?? Enumerable.Range(0, info.Frames.Count).ToList();
        return info.Format == PageImageFormat.TiffG4
            ? RenderTiff(request, settings, info, indexes, cancellationToken)
            : RenderSingle(request, settings, info, indexes, cancellationToken);
    }

    private static IEnumerable<RenderedPage> RenderSingle(
        RenderRequest request, RenderSettings settings, ImageInfo info, IReadOnlyList<int> indexes, CancellationToken cancellationToken)
    {
        foreach (var index in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index != 0)
            {
                yield return Failed(index, null, RenderErrorCodes.PageFailed);
                continue;
            }

            var frame = info.Frames[0];
            if ((long)frame.WidthPx * frame.HeightPx > settings.MaxSourcePixels)
            {
                yield return Failed(index, frame, RenderErrorCodes.PageTooLarge);
                continue;
            }

            SKBitmap? bitmap;
            using (var codec = SKCodec.Create(request.InputPath))
            {
                bitmap = codec is null ? null : SKBitmap.Decode(codec);
            }

            if (bitmap is null || bitmap.Width != frame.WidthPx || bitmap.Height != frame.HeightPx)
            {
                bitmap?.Dispose();
                yield return Failed(index, frame, RenderErrorCodes.PageFailed);
                continue;
            }

            yield return Write(bitmap, frame, index, request, settings);
        }
    }

    private static IEnumerable<RenderedPage> RenderTiff(
        RenderRequest request, RenderSettings settings, ImageInfo info, IReadOnlyList<int> indexes, CancellationToken cancellationToken)
    {
        using var tiff = Tiff.Open(request.InputPath, "r")
            ?? throw new RenderException(RenderErrorCodes.Unreadable, "The TIFF cannot be opened.");
        var directories = PageDirectories(tiff);
        foreach (var index in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < 0 || index >= info.Frames.Count || index >= directories.Count)
            {
                yield return Failed(index, index >= 0 && index < info.Frames.Count ? info.Frames[index] : null, RenderErrorCodes.PageFailed);
                continue;
            }

            var frame = info.Frames[index];
            if ((long)frame.WidthPx * frame.HeightPx > settings.MaxSourcePixels)
            {
                yield return Failed(index, frame, RenderErrorCodes.PageTooLarge);
                continue;
            }

            SKBitmap? bitmap = null;
            try
            {
                bitmap = tiff.SetDirectory(directories[index]) ? DecodeTiffFrame(tiff, frame) : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A malformed strip or an unsupported codec fails this page only.
                bitmap?.Dispose();
                bitmap = null;
            }

            if (bitmap is null)
            {
                yield return Failed(index, frame, RenderErrorCodes.PageFailed);
                continue;
            }

            yield return Write(bitmap, frame, index, request, settings);
        }
    }

    /// <summary>Directory numbers of the page frames (reduced-resolution subfiles skipped, as <see cref="ImageProbe"/> does).</summary>
    private static List<short> PageDirectories(Tiff tiff)
    {
        var pages = new List<short>();
        var count = Math.Min((int)tiff.NumberOfDirectories(), short.MaxValue);
        for (short d = 0; d < count; d++)
        {
            if (!tiff.SetDirectory(d))
            {
                break;
            }

            var subfile = tiff.GetField(TiffTag.SUBFILETYPE);
            if (subfile is null || (subfile[0].ToInt() & 1) == 0)
            {
                pages.Add(d);
            }
        }

        return pages;
    }

    private static SKBitmap? DecodeTiffFrame(Tiff tiff, ImageFrame frame)
    {
        int width = Int(tiff, TiffTag.IMAGEWIDTH, 0), height = Int(tiff, TiffTag.IMAGELENGTH, 0);
        if (width != frame.WidthPx || height != frame.HeightPx)
        {
            return null;
        }

        var bits = Int(tiff, TiffTag.BITSPERSAMPLE, 1);
        var samples = Int(tiff, TiffTag.SAMPLESPERPIXEL, 1);
        var photometric = Int(tiff, TiffTag.PHOTOMETRIC, -1);
        var orientation = Int(tiff, TiffTag.ORIENTATION, 1);
        if (bits == 1 && samples == 1 && photometric is 0 or 1 && orientation == 1 && !tiff.IsTiled())
        {
            return DecodeBitonal(tiff, width, height, whiteIsZero: photometric == 0);
        }

        var raster = new int[checked(width * height)];
        if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
        {
            return null;
        }

        // Packed ABGR ints are R, G, B, A bytes in memory on little-endian hosts: Skia's RGBA8888 layout.
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var target = bitmap.GetPixelSpan();
        var source = System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.AsSpan());
        var rowBytes = bitmap.RowBytes;
        for (var y = 0; y < height; y++)
        {
            var row = target.Slice(y * rowBytes, width * 4);
            source.Slice(y * width * 4, width * 4).CopyTo(row);
            for (var x = 3; x < row.Length; x += 4)
            {
                row[x] = 0xFF;
            }
        }

        return bitmap;
    }

    /// <summary>A bitonal frame line by line into 8-bit gray (black 0, white 255), without an RGBA intermediate.</summary>
    private static SKBitmap? DecodeBitonal(Tiff tiff, int width, int height, bool whiteIsZero)
    {
        var line = new byte[Math.Max(tiff.ScanlineSize(), (width + 7) / 8)];
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        var target = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        for (var y = 0; y < height; y++)
        {
            if (!tiff.ReadScanline(line, y))
            {
                bitmap.Dispose();
                return null;
            }

            var row = target.Slice(y * rowBytes, width);
            for (var x = 0; x < width; x++)
            {
                var set = (line[x >> 3] & (0x80 >> (x & 7))) != 0;
                row[x] = set == whiteIsZero ? (byte)0 : (byte)255;
            }
        }

        return bitmap;
    }

    private static RenderedPage Write(SKBitmap bitmap, ImageFrame frame, int index, RenderRequest request, RenderSettings settings)
    {
        try
        {
            var color = frame.ColorMode;
            if (bitmap.ColorType != SKColorType.Gray8 && color != PageColorMode.Color)
            {
                RasterWriter.Normalize(ref bitmap);
            }

            // Non-square pixels (fax resolutions such as 204 × 98 DPI): resample to square pixels at the horizontal DPI.
            if (frame.DpiX != frame.DpiY)
            {
                var height = Math.Max(1, (int)Math.Round(bitmap.Height * (double)frame.DpiX / frame.DpiY));
                var square = RasterWriter.Resize(bitmap, bitmap.Width, height);
                bitmap.Dispose();
                bitmap = square;
            }

            var (review, thumbnail, error) = RasterWriter.Write(
                bitmap, frame.DpiX, (double)frame.WidthPt, (double)frame.HeightPt, request.Review, request.OutputDirectory, index, settings);
            return new RenderedPage(index, frame.WidthPt, frame.HeightPt, color, review, thumbnail, error);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static RenderedPage Failed(int index, ImageFrame? frame, string code) =>
        new(index, frame?.WidthPt ?? 1, frame?.HeightPt ?? 1, frame?.ColorMode ?? PageColorMode.Gray, null, null, code);

    private static int Int(Tiff tiff, TiffTag tag, int fallback) =>
        tiff.GetField(tag) is { Length: > 0 } value ? value[0].ToInt() : fallback;

    private sealed class QuietTiffErrors : TiffErrorHandler
    {
        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args)
        {
        }

        public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args)
        {
        }

        public override void WarningHandler(Tiff tif, string method, string format, params object[] args)
        {
        }

        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args)
        {
        }
    }
}
