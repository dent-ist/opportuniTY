using System.Globalization;

using Opportunity.Core.Pages;

using SkiaSharp;

namespace Opportunity.Rendering.Renderers;

/// <summary>
/// Sizes, scales and encodes the review raster and thumbnail of one page (ADR-012 §1.4): PNG, 8-bit gray when the page
/// has no color, the page's aspect ratio kept within <see cref="RenderSettings.MaxAspectDeviation"/>. PNG encoding is a
/// pure function of the pixels, so a re-render of the same page with the same libraries writes the same bytes.
/// </summary>
internal static class RasterWriter
{
    /// <summary>Pixel size and effective resolution of a raster of a <paramref name="width"/> × <paramref name="height"/> source at <paramref name="dpi"/>, within the review bounds.</summary>
    public static (int Width, int Height, int Dpi) ReviewSize(double width, double height, double dpi, RenderSettings settings)
    {
        var f = Math.Min(1d, Math.Min(settings.MaxReviewSide / Math.Max(width, height), Math.Sqrt(settings.MaxReviewPixels / (width * height))));
        return (Math.Max(1, (int)Math.Round(width * f)), Math.Max(1, (int)Math.Round(height * f)), Math.Max(1, (int)Math.Round(dpi * f)));
    }

    /// <summary>Thumbnail size for a review raster: longer side <see cref="RenderSettings.ThumbnailSide"/>, shorter side at least 100 px (so rounding stays within 0.5 %).</summary>
    public static (int Width, int Height) ThumbnailSize(int width, int height, RenderSettings settings)
    {
        var f = Math.Min(1d, Math.Max((double)settings.ThumbnailSide / Math.Max(width, height), 100d / Math.Min(width, height)));
        return (Math.Max(1, (int)Math.Round(width * f)), Math.Max(1, (int)Math.Round(height * f)));
    }

    /// <summary>ADR-012 §1.4: the raster's aspect ratio is within 0.5 % of the page's.</summary>
    public static bool AspectMatches(int widthPx, int heightPx, double pageWidth, double pageHeight) =>
        Math.Abs((widthPx / (double)heightPx) / (pageWidth / pageHeight) - 1d) <= RenderSettings.MaxAspectDeviation;

    /// <summary>True when every pixel of a BGRA/RGBA bitmap has equal color channels.</summary>
    public static bool IsGray(SKBitmap bitmap)
    {
        if (bitmap.ColorType == SKColorType.Gray8)
        {
            return true;
        }

        var bytes = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        var rowLength = bitmap.Width * 4;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = bytes.Slice(y * rowBytes, rowLength);
            for (var x = 0; x < row.Length; x += 4)
            {
                if (row[x] != row[x + 1] || row[x + 1] != row[x + 2])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>An 8-bit gray copy of a gray BGRA/RGBA bitmap (the first channel; all are equal).</summary>
    public static SKBitmap ToGray(SKBitmap bitmap)
    {
        var gray = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var source = bitmap.GetPixelSpan();
        var target = gray.GetPixelSpan();
        var sourceRow = bitmap.RowBytes;
        var targetRow = gray.RowBytes;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var s = source.Slice(y * sourceRow, bitmap.Width * 4);
            var t = target.Slice(y * targetRow, bitmap.Width);
            for (var x = 0; x < t.Length; x++)
            {
                t[x] = s[x * 4];
            }
        }

        return gray;
    }

    /// <summary>
    /// Writes the review raster (when <paramref name="review"/>) and the thumbnail of a decoded page. The bitmap is the
    /// page at <paramref name="dpi"/>; it is scaled down to the review bounds first.
    /// </summary>
    public static (RasterFile? Review, RasterFile? Thumbnail, string? Error) Write(
        SKBitmap page, double dpi, double pageWidth, double pageHeight, bool review, string outputDirectory, int index, RenderSettings settings)
    {
        var (w, h, reviewDpi) = ReviewSize(page.Width, page.Height, dpi, settings);
        if (!AspectMatches(w, h, pageWidth, pageHeight))
        {
            return (null, null, RenderErrorCodes.AspectMismatch);
        }

        using var scaled = w == page.Width && h == page.Height ? null : Resize(page, w, h);
        var source = scaled ?? page;
        RasterFile? reviewFile = null;
        if (review)
        {
            var path = Path.Combine(outputDirectory, Name(index, "png"));
            Encode(source, path);
            reviewFile = new RasterFile(path, w, h, reviewDpi, PageImageFormat.Png);
        }

        var (tw, th) = ThumbnailSize(w, h, settings);
        if (!AspectMatches(tw, th, pageWidth, pageHeight))
        {
            return (reviewFile, null, null);
        }

        var thumbPath = Path.Combine(outputDirectory, Name(index, "thumb.png"));
        if (tw == w && th == h)
        {
            Encode(source, thumbPath);
        }
        else
        {
            using var thumb = Resize(source, tw, th);
            Encode(thumb, thumbPath);
        }

        var thumbDpi = Math.Max(1, (int)Math.Round(reviewDpi * (double)tw / w));
        return (reviewFile, new RasterFile(thumbPath, tw, th, thumbDpi, PageImageFormat.Png), null);
    }

    /// <summary>Gray pages become 8-bit gray PNGs; others keep their color.</summary>
    public static PageColorMode Normalize(ref SKBitmap bitmap)
    {
        if (bitmap.ColorType == SKColorType.Gray8)
        {
            return PageColorMode.Gray;
        }

        if (!IsGray(bitmap))
        {
            return PageColorMode.Color;
        }

        var gray = ToGray(bitmap);
        bitmap.Dispose();
        bitmap = gray;
        return PageColorMode.Gray;
    }

    public static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var info = new SKImageInfo(width, height, source.ColorType, SKAlphaType.Opaque);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidOperationException("The page raster could not be scaled.");
    }

    private static void Encode(SKBitmap bitmap, string path)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The page raster could not be encoded.");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        data.SaveTo(file);
    }

    private static string Name(int index, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"p{index + 1:D6}.{extension}");
}
