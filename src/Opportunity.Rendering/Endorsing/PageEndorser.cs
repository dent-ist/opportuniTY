using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

using BitMiracle.LibTiff.Classic;

using Opportunity.Core.Pages;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.Import.Images;
using Opportunity.Rendering.Renderers;

using SkiaSharp;

namespace Opportunity.Rendering.Endorsing;

/// <summary>
/// Stamps endorsements (Bates number, confidentiality legend and other configured texts) on a produced page (E12-T04)
/// and encodes it as TIFF CCITT Group 4, JPEG or PNG. It decodes document content, so the render worker runs it in the
/// document's render sandbox (<see cref="IRenderSession.EndorseAsync"/>); in-process use is for tests and development.
/// <para>
/// Deterministic: the same inputs give the same bytes. The text is drawn with an embedded font (Liberation Sans, SIL OFL
/// 1.1), aliased (no anti-aliasing, so no blending), into a coverage mask that is then copied onto the page as pure black
/// on a pure white box; the page's own pixels are copied, never resampled (non-square pixels are made square by
/// nearest-neighbour row repetition); TIFF output is thresholded and written by LibTiff.NET, JPEG and PNG by Skia at a
/// fixed quality. A change to any of that, or to the libraries, changes <see cref="Version"/>.
/// </para>
/// <para>
/// Layout: font size and margin are points at the page's resolution. With <see cref="EndorsementLayout.ExpandCanvas"/>
/// a band is added above and/or below the page for the top and bottom stamps, so the image is never overwritten;
/// otherwise each stamp is drawn over the page on a white box. Left and right stamps sit a margin from the side, a
/// centre stamp is centred; a row of stamps too wide for the page is drawn in a smaller font until it fits.
/// </para>
/// </summary>
public static class PageEndorser
{
    /// <summary>Bump when the output for the same input and layout changes.</summary>
    public const int PipelineVersion = 1;

    public const int JpegQuality = 90;
    public const int MaxStamps = 6;
    public const int MaxTextLength = 600;
    public const int MaxBodyLines = 10;
    public const int MaxBlankSide = 20_000;
    public const int MinFontPx = 4;
    public const int MaxRedactions = 100;

    private const string FontResource = "Opportunity.Rendering.Fonts.LiberationSans-Regular.ttf";
    private const string FontName = "LiberationSans-Regular 2.1.5";
    private const byte Black = 0;
    private const byte White = 255;

    private static readonly Lazy<EmbeddedFont> Font = new(LoadFont, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Endorser, library and font versions; recorded with produced images (Q-08).</summary>
    public static string Version
    {
        get
        {
            var libtiff = typeof(Tiff).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Tiff).Assembly.GetName().Version?.ToString() ?? "unknown";
            return string.Create(CultureInfo.InvariantCulture,
                $"endorser {PipelineVersion};skia {SkiaSharpVersion.Native};libtiff.net {libtiff.Split('+')[0]};font {FontName} {Font.Value.Sha256[..16]}");
        }
    }

    /// <summary>The file the endorser writes into the request's output directory.</summary>
    public static string FileName(PageImageFormat format) => format switch
    {
        PageImageFormat.TiffG4 => "endorsed.tif",
        PageImageFormat.Jpeg => "endorsed.jpg",
        PageImageFormat.Png => "endorsed.png",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Endorsed pages are TIFF G4, JPEG or PNG."),
    };

    /// <summary>Loads the font (the sandbox does this before it applies its seccomp filter).</summary>
    public static void EnsureInitialized() => _ = Font.Value;

    /// <summary>Endorses a page in this process and returns the encoded bytes (the output file is removed).</summary>
    /// <exception cref="RenderException">The source cannot be read, or its page is too large.</exception>
    public static EndorsedImage Endorse(EndorseRequest request, RenderSettings? settings = null)
    {
        var file = EndorseToFile(request, settings ?? new RenderSettings());
        try
        {
            return new EndorsedImage(File.ReadAllBytes(file.Path), file.WidthPx, file.HeightPx, file.Dpi, file.Format, file.ColorMode, file.PageTopPx,
                file.PageHeightPx);
        }
        finally
        {
            File.Delete(file.Path);
        }
    }

    /// <summary>Checks a request before it reaches the endorser (the parent of a sandbox checks it too).</summary>
    public static void Validate(EndorseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Layout);
        _ = FileName(request.Format);
        var layout = request.Layout;
        if (layout.FontSizePt is < 4 or > 72 || layout.MarginPt is < 0 or > 144 || request.Dpi is < 36 or > 1200 || request.Frame < 0)
        {
            throw new ArgumentException("Font size is 4–72 pt, margin 0–144 pt, resolution 36–1200 DPI.", nameof(request));
        }

        if ((layout.Stamps ?? throw new ArgumentException("Give the stamps.", nameof(request))).Count > MaxStamps
            || layout.Stamps.Any(s => s is null || !Enum.IsDefined(s.Position) || !ValidText(s.Text))
            || layout.Stamps.Select(s => s.Position).Distinct().Count() != layout.Stamps.Count)
        {
            throw new ArgumentException($"At most {MaxStamps} stamps, one per position, each 1–{MaxTextLength} characters without control characters.", nameof(request));
        }

        if (layout.BodyLines is { } body && (body.Count > MaxBodyLines || body.Any(l => !ValidText(l))))
        {
            throw new ArgumentException($"At most {MaxBodyLines} body lines of 1–{MaxTextLength} characters.", nameof(request));
        }

        if (request.InputPath is null && (request.BlankWidthPx is < 1 or > MaxBlankSide || request.BlankHeightPx is < 1 or > MaxBlankSide))
        {
            throw new ArgumentException($"A blank page is 1–{MaxBlankSide} pixels on each side.", nameof(request));
        }

        if (request.Redactions is { } redactions
            && (redactions.Count > MaxRedactions
                || redactions.Any(r => r is null || !r.Rect.IsWithinPage || !Enum.IsDefined(r.Type) || (r.Label is not null && !ValidText(r.Label)))))
        {
            throw new ArgumentException($"At most {MaxRedactions} redactions per page, each inside the page, with a valid label.", nameof(request));
        }
    }

    /// <summary>Endorses into <c>OutputDirectory/</c><see cref="FileName"/> (what the sandbox child runs).</summary>
    internal static EndorsedFile EndorseToFile(EndorseRequest request, RenderSettings settings)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(settings);
        var (page, dpi, color) = Load(request, settings);
        try
        {
            if (request.InputPath is not null && request.Redactions is { Count: > 0 } redactions)
            {
                Burn(page, dpi, redactions);
            }

            var (composed, top) = Compose(page, dpi, request.Layout, generated: request.InputPath is null);
            using (composed)
            {
                var path = Path.Combine(request.OutputDirectory, FileName(request.Format));
                Encode(composed, dpi, request.Format, path);
                return new EndorsedFile(path, composed.Width, composed.Height, dpi, request.Format,
                    request.Format == PageImageFormat.TiffG4 ? PageColorMode.Bitonal : color, top, page.Height);
            }
        }
        finally
        {
            page.Dispose();
        }
    }

    private static bool ValidText(string? text) => text is { Length: > 0 and <= MaxTextLength } && !text.Any(char.IsControl) && text.Trim().Length > 0;

    /// <summary>The page as 8-bit gray or opaque 32-bit pixels, its resolution and colour mode.</summary>
    private static (SKBitmap Page, int Dpi, PageColorMode Color) Load(EndorseRequest request, RenderSettings settings)
    {
        if (request.InputPath is null)
        {
            var blank = new SKBitmap(new SKImageInfo(request.BlankWidthPx, request.BlankHeightPx, SKColorType.Gray8, SKAlphaType.Opaque));
            blank.GetPixelSpan().Fill(White);
            return (blank, request.Dpi ?? 300, PageColorMode.Bitonal);
        }

        ImageInfo info;
        using (var probe = File.OpenRead(request.InputPath))
        {
            info = ImageProbe.Probe(probe) ?? throw new RenderException(RenderErrorCodes.Unsupported, "The page is not a readable TIFF, JPEG or PNG.");
        }

        if (request.Frame >= info.Frames.Count)
        {
            throw new RenderException(RenderErrorCodes.PageFailed, "The image has no such page.");
        }

        var frame = info.Frames[request.Frame];
        if ((long)frame.WidthPx * frame.HeightPx > settings.MaxSourcePixels)
        {
            throw new RenderException(RenderErrorCodes.PageTooLarge, "The page is too large to endorse.");
        }

        SKBitmap? bitmap = null;
        try
        {
            if (info.Format == PageImageFormat.TiffG4)
            {
                using var tiff = Tiff.Open(request.InputPath, "r") ?? throw new RenderException(RenderErrorCodes.Unreadable, "The TIFF cannot be opened.");
                var directories = ImageRenderer.PageDirectories(tiff);
                bitmap = request.Frame < directories.Count && tiff.SetDirectory(directories[request.Frame]) ? ImageRenderer.DecodeTiffFrame(tiff, frame) : null;
            }
            else if (request.Frame == 0)
            {
                using var codec = SKCodec.Create(request.InputPath);
                bitmap = codec is null ? null : SKBitmap.Decode(codec);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or RenderException))
        {
            bitmap?.Dispose();
            bitmap = null;
        }

        if (bitmap is null || bitmap.Width != frame.WidthPx || bitmap.Height != frame.HeightPx)
        {
            bitmap?.Dispose();
            throw new RenderException(RenderErrorCodes.PageFailed, "The page cannot be decoded.");
        }

        bitmap = Canonical(bitmap);
        // Gray pixels become 8-bit gray whatever the header said; colour stays colour.
        var color = frame.ColorMode;
        if (bitmap.ColorType != SKColorType.Gray8 && RasterWriter.Normalize(ref bitmap) is var normalized)
        {
            color = normalized == PageColorMode.Color ? PageColorMode.Color : color == PageColorMode.Color ? PageColorMode.Gray : color;
        }

        var dpi = request.Dpi ?? frame.DpiX;
        if (request.Dpi is null && frame.DpiY != frame.DpiX && frame.DpiY > 0)
        {
            var square = RepeatRows(bitmap, (int)Math.Max(1, Math.Round(bitmap.Height * (double)frame.DpiX / frame.DpiY, MidpointRounding.AwayFromZero)));
            bitmap.Dispose();
            bitmap = square;
        }

        return (bitmap, dpi, color);
    }

    /// <summary>8-bit gray stays; anything else becomes opaque 32-bit pixels in Skia's native order.</summary>
    private static SKBitmap Canonical(SKBitmap bitmap)
    {
        if (bitmap.ColorType == SKColorType.Gray8)
        {
            return bitmap;
        }

        if (bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
        {
            var converted = bitmap.Copy(SKImageInfo.PlatformColorType);
            bitmap.Dispose();
            bitmap = converted ?? throw new RenderException(RenderErrorCodes.PageFailed, "The page's pixel format is not supported.");
        }

        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * bitmap.RowBytes, bitmap.Width * 4);
            for (var x = 3; x < row.Length; x += 4)
            {
                row[x] = 0xFF;
            }
        }

        return bitmap;
    }

    /// <summary>Nearest-neighbour vertical scaling (integer arithmetic only) for non-square pixels.</summary>
    private static SKBitmap RepeatRows(SKBitmap source, int height)
    {
        var target = new SKBitmap(new SKImageInfo(source.Width, height, source.ColorType, SKAlphaType.Opaque));
        var from = source.GetPixelSpan();
        var to = target.GetPixelSpan();
        var bytes = source.Width * source.BytesPerPixel;
        for (var y = 0; y < height; y++)
        {
            var sy = (int)Math.Min(source.Height - 1, (long)y * source.Height / height);
            from.Slice(sy * source.RowBytes, bytes).CopyTo(to.Slice(y * target.RowBytes, bytes));
        }

        return target;
    }

    /// <summary>
    /// Burns redactions into the page's pixels (E12-T05, ADR-012 §5.2): a black box is filled black; a labelled box is
    /// filled white, framed in black just outside the rectangle (so every pixel inside is fill or glyph) and its label is
    /// printed centred in black when it fits. Integer arithmetic only, so the result is deterministic.
    /// </summary>
    private static void Burn(SKBitmap page, int dpi, IReadOnlyList<BurnedRedaction> redactions)
    {
        var border = Math.Max(1, (dpi + 75) / 150);
        foreach (var redaction in redactions.Where(r => r.Type == RedactionType.Labelled))
        {
            var box = RedactionGeometry.BurnedPixels(redaction.Rect, page.Width, page.Height);
            Fill(page, box.X - border, box.Y - border, box.Width + 2 * border, box.Height + 2 * border, Black);
        }

        foreach (var redaction in redactions)
        {
            var box = RedactionGeometry.BurnedPixels(redaction.Rect, page.Width, page.Height);
            if (redaction.Type == RedactionType.Black)
            {
                Fill(page, box.X, box.Y, box.Width, box.Height, Black);
                continue;
            }

            Fill(page, box.X, box.Y, box.Width, box.Height, White);
            if (redaction.Label is { Length: > 0 } label)
            {
                var pad = 2;
                var size = Math.Min((int)Math.Round(10d * dpi / 72d, MidpointRounding.AwayFromZero), box.Height - 2 * pad);
                while (size >= MinFontPx)
                {
                    var mask = RenderText(label, size);
                    if (mask.Width <= box.Width - 2 * pad && mask.Height <= box.Height - 2 * pad)
                    {
                        Blit(page, mask, box.X + (box.Width - mask.Width) / 2, box.Y + (box.Height - mask.Height) / 2, box: false, 0);
                        break;
                    }

                    size = Math.Min(size - 1, (int)((long)size * Math.Max(1, box.Width - 2 * pad) / Math.Max(1, mask.Width)));
                }
            }
        }

        // Black boxes win over any label or frame that overlaps them.
        foreach (var redaction in redactions.Where(r => r.Type == RedactionType.Black))
        {
            var box = RedactionGeometry.BurnedPixels(redaction.Rect, page.Width, page.Height);
            Fill(page, box.X, box.Y, box.Width, box.Height, Black);
        }
    }

    private static void Fill(SKBitmap bitmap, int x, int y, int width, int height, byte value)
    {
        int left = Math.Max(0, x), top = Math.Max(0, y), right = Math.Min(bitmap.Width, x + width), bottom = Math.Min(bitmap.Height, y + height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        var pixels = bitmap.GetPixelSpan();
        var bpp = bitmap.BytesPerPixel;
        for (var row = top; row < bottom; row++)
        {
            var span = pixels.Slice(row * bitmap.RowBytes + left * bpp, (right - left) * bpp);
            if (bpp == 1)
            {
                span.Fill(value);
                continue;
            }

            for (var i = 0; i < span.Length; i += 4)
            {
                span[i] = value;
                span[i + 1] = value;
                span[i + 2] = value;
                span[i + 3] = 0xFF;
            }
        }
    }

    private static (SKBitmap Canvas, int PageTop) Compose(SKBitmap page, int dpi, EndorsementLayout layout, bool generated)
    {
        int Px(double points) => (int)Math.Round(points * dpi / 72d, MidpointRounding.AwayFromZero);
        var fontPx = Math.Max(MinFontPx, Px(layout.FontSizePt));
        var margin = Px(layout.MarginPt);
        var pad = Math.Max(1, (fontPx + 4) / 8);
        var width = page.Width;
        var top = Row(layout.Stamps.Where(s => IsTop(s.Position)).OrderBy(s => s.Position).ToList(), fontPx, width - 2 * margin, pad);
        var bottom = Row(layout.Stamps.Where(s => !IsTop(s.Position)).OrderBy(s => s.Position).ToList(), fontPx, width - 2 * margin, pad);
        var topBand = layout.ExpandCanvas && top.Count > 0 ? margin + top[0].Mask.Height + 3 * pad : 0;
        var bottomBand = layout.ExpandCanvas && bottom.Count > 0 ? margin + bottom[0].Mask.Height + 3 * pad : 0;
        var height = checked(page.Height + topBand + bottomBand);

        var canvas = new SKBitmap(new SKImageInfo(width, height, page.ColorType, SKAlphaType.Opaque));
        var target = canvas.GetPixelSpan();
        target.Fill(White);
        var bpp = page.BytesPerPixel;
        var source = page.GetPixelSpan();
        for (var y = 0; y < page.Height; y++)
        {
            source.Slice(y * page.RowBytes, width * bpp).CopyTo(target.Slice((y + topBand) * canvas.RowBytes, width * bpp));
        }

        if (generated && layout.BodyLines is { Count: > 0 } lines)
        {
            var body = Row([.. lines.Select(l => new EndorsementText(EndorsementPosition.TopCenter, l))], fontPx * 2, width - 2 * margin, pad, stack: true);
            var lineHeight = body[0].Mask.Height + pad;
            var y = topBand + Math.Max(0, (page.Height - lineHeight * body.Count) / 2);
            foreach (var line in body)
            {
                Blit(canvas, line.Mask, (width - line.Mask.Width) / 2, y, box: false, pad);
                y += lineHeight;
            }
        }

        foreach (var stamp in top)
        {
            Stamp(canvas, stamp, margin, margin, pad);
        }

        foreach (var stamp in bottom)
        {
            Stamp(canvas, stamp, margin, height - margin - stamp.Mask.Height - 2 * pad, pad);
        }

        return (canvas, topBand);
    }

    private static bool IsTop(EndorsementPosition position) => position is EndorsementPosition.TopLeft or EndorsementPosition.TopCenter or EndorsementPosition.TopRight;

    private sealed record PlacedText(EndorsementPosition Position, TextMask Mask);

    /// <summary>
    /// The texts of one row rendered at <paramref name="fontPx"/>, or smaller until they fit <paramref name="available"/>
    /// side by side (<paramref name="stack"/>: each on its own line, each must fit).
    /// </summary>
    private static List<PlacedText> Row(List<EndorsementText> texts, int fontPx, int available, int pad, bool stack = false)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var size = fontPx;
        while (true)
        {
            var masks = texts.Select(t => new PlacedText(t.Position, RenderText(t.Text, size))).ToList();
            var needed = stack
                ? masks.Max(m => m.Mask.Width + 2 * pad)
                : masks.Sum(m => m.Mask.Width + 2 * pad) + (masks.Count - 1) * 2 * pad;
            if (needed <= available || size <= MinFontPx)
            {
                return masks;
            }

            size = Math.Max(MinFontPx, Math.Min(size - 1, (int)((long)size * Math.Max(1, available) / needed)));
        }
    }

    private static void Stamp(SKBitmap canvas, PlacedText text, int margin, int boxY, int pad)
    {
        var boxWidth = text.Mask.Width + 2 * pad;
        var x = text.Position switch
        {
            EndorsementPosition.TopLeft or EndorsementPosition.BottomLeft => margin,
            EndorsementPosition.TopRight or EndorsementPosition.BottomRight => canvas.Width - margin - boxWidth,
            _ => (canvas.Width - boxWidth) / 2,
        };
        Blit(canvas, text.Mask, Math.Clamp(x, 0, Math.Max(0, canvas.Width - boxWidth)), Math.Max(0, boxY), box: true, pad);
    }

    /// <summary>Draws a white box (when <paramref name="box"/>) and the text's covered pixels in black.</summary>
    private static void Blit(SKBitmap canvas, TextMask mask, int x, int y, bool box, int pad)
    {
        var target = canvas.GetPixelSpan();
        var bpp = canvas.BytesPerPixel;
        var boxWidth = mask.Width + 2 * pad;
        var boxHeight = mask.Height + 2 * pad;
        for (var by = 0; by < boxHeight; by++)
        {
            var cy = y + by;
            if (cy < 0 || cy >= canvas.Height)
            {
                continue;
            }

            var row = target.Slice(cy * canvas.RowBytes, canvas.Width * bpp);
            for (var bx = 0; bx < boxWidth; bx++)
            {
                var cx = x + bx;
                if (cx < 0 || cx >= canvas.Width)
                {
                    continue;
                }

                int mx = bx - pad, my = by - pad;
                var ink = mx >= 0 && my >= 0 && mx < mask.Width && my < mask.Height && mask.Coverage[my * mask.Width + mx] >= 128;
                if (!ink && !box)
                {
                    continue;
                }

                var value = ink ? Black : White;
                var pixel = row.Slice(cx * bpp, bpp);
                if (bpp == 1)
                {
                    pixel[0] = value;
                }
                else
                {
                    pixel[0] = value;
                    pixel[1] = value;
                    pixel[2] = value;
                    pixel[3] = 0xFF;
                }
            }
        }
    }

    private sealed record TextMask(byte[] Coverage, int Width, int Height);

    /// <summary>A text's aliased coverage (0 or 255 per pixel), one line high (ascent + descent).</summary>
    private static TextMask RenderText(string text, int sizePx)
    {
        using var font = new SKFont(Font.Value.Typeface, sizePx)
        {
            Edging = SKFontEdging.Alias,
            Hinting = SKFontHinting.Normal,
            Subpixel = false,
            LinearMetrics = false,
            EmbeddedBitmaps = false,
        };
        var metrics = font.Metrics;
        var ascent = (int)Math.Ceiling(-metrics.Ascent);
        var height = Math.Max(1, ascent + (int)Math.Ceiling(metrics.Descent));
        var width = Math.Max(1, (int)Math.Ceiling(font.MeasureText(text)) + 1);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false })
        {
            canvas.DrawText(text, 0, ascent, SKTextAlign.Left, font, paint);
        }

        var coverage = new byte[width * height];
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            pixels.Slice(y * bitmap.RowBytes, width).CopyTo(coverage.AsSpan(y * width, width));
        }

        return new TextMask(coverage, width, height);
    }

    private static void Encode(SKBitmap bitmap, int dpi, PageImageFormat format, string path)
    {
        if (format == PageImageFormat.TiffG4)
        {
            WriteTiffG4(bitmap, dpi, path);
            return;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format == PageImageFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, format == PageImageFormat.Jpeg ? JpegQuality : 100)
            ?? throw new InvalidOperationException("The endorsed page could not be encoded.");
        var bytes = data.ToArray();
        if (format == PageImageFormat.Jpeg)
        {
            SetJfifDensity(bytes, dpi);
        }

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
    }

    /// <summary>Single-page, single-strip TIFF CCITT Group 4, min-is-white; a pixel darker than mid-gray is black.</summary>
    private static void WriteTiffG4(SKBitmap bitmap, int dpi, string path)
    {
        if (File.Exists(path))
        {
            throw new IOException("The endorsed page already exists.");
        }

        using var tiff = Tiff.Open(path, "w") ?? throw new IOException("The endorsed page cannot be written.");
        tiff.SetField(TiffTag.IMAGEWIDTH, bitmap.Width);
        tiff.SetField(TiffTag.IMAGELENGTH, bitmap.Height);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 1);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tiff.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISWHITE);
        tiff.SetField(TiffTag.FILLORDER, FillOrder.MSB2LSB);
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.ROWSPERSTRIP, bitmap.Height);
        tiff.SetField(TiffTag.XRESOLUTION, (float)dpi);
        tiff.SetField(TiffTag.YRESOLUTION, (float)dpi);
        tiff.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
        var line = new byte[(bitmap.Width + 7) / 8];
        var pixels = bitmap.GetPixelSpan();
        var bpp = bitmap.BytesPerPixel;
        for (var y = 0; y < bitmap.Height; y++)
        {
            Array.Clear(line);
            var row = pixels.Slice(y * bitmap.RowBytes, bitmap.Width * bpp);
            for (var x = 0; x < bitmap.Width; x++)
            {
                var gray = bpp == 1 ? row[x] : (row[x * 4] + row[x * 4 + 1] + row[x * 4 + 2]) / 3;
                if (gray < 128)
                {
                    line[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
            }

            if (!tiff.WriteScanline(line, y))
            {
                throw new IOException("The endorsed page could not be encoded.");
            }
        }

        tiff.WriteDirectory();
    }

    /// <summary>Records the resolution in the JFIF header Skia writes (units: dots per inch).</summary>
    private static void SetJfifDensity(byte[] jpeg, int dpi)
    {
        // SOI, APP0 marker, length, "JFIF\0", version (2), units (1), X density (2), Y density (2).
        if (jpeg.Length > 18 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 && jpeg[2] == 0xFF && jpeg[3] == 0xE0
            && jpeg.AsSpan(6, 5).SequenceEqual("JFIF\0"u8))
        {
            var density = (ushort)Math.Clamp(dpi, 1, ushort.MaxValue);
            jpeg[13] = 1;
            jpeg[14] = (byte)(density >> 8);
            jpeg[15] = (byte)density;
            jpeg[16] = (byte)(density >> 8);
            jpeg[17] = (byte)density;
        }
    }

    private sealed record EmbeddedFont(SKTypeface Typeface, SKData Data, string Sha256);

    private static EmbeddedFont LoadFont()
    {
        using var stream = typeof(PageEndorser).Assembly.GetManifestResourceStream(FontResource)
            ?? throw new InvalidOperationException("The endorsement font is missing from the rendering assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var data = SKData.CreateCopy(bytes);
        var typeface = SKTypeface.FromData(data) ?? throw new InvalidOperationException("The endorsement font cannot be loaded.");
        return new EmbeddedFont(typeface, data, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

/// <summary>An endorsed page written to <see cref="Path"/>.</summary>
internal sealed record EndorsedFile(
    string Path, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format, PageColorMode ColorMode, int PageTopPx = 0, int PageHeightPx = 0);
