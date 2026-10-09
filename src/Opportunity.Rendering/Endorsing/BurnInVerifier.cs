using System.Globalization;

using Opportunity.Application.Productions;
using Opportunity.Core.Pages;
using Opportunity.Core.Redactions;
using Opportunity.Import.Images;
using Opportunity.Rendering.Renderers;

using SkiaSharp;

namespace Opportunity.Rendering.Endorsing;

/// <summary>A produced page to verify (E12-T06); paths under the session's work directory.</summary>
/// <param name="Frame">The source page's frame in <see cref="SourcePath"/>.</param>
/// <param name="PageTopPx">Where the imager reported the source page inside the produced image.</param>
public sealed record BurnInRequest(
    string ProducedPath, PageImageFormat Format, string SourcePath, int Frame, int PageTopPx, IReadOnlyList<BurnedRedaction> Redactions);

/// <summary>The verdict on one produced page (no issues: it passed).</summary>
public sealed record BurnInResult(int WidthPx, int PageHeightPx, int BoxesChecked, IReadOnlyList<BurnInIssue> Issues);

/// <summary>One failed check (<see cref="BurnInCodes"/>), the 1-based box it concerns and the number of failing pixels.</summary>
public sealed record BurnInIssue(string Code, int? Box = null, long? Pixels = null);

/// <summary>
/// Verifies that redactions were burned into a produced page (E12-T06, ADR-012 §5). It decodes the produced image and
/// the source page exactly as the endorser does, so it runs where the endorser runs: in the document's render sandbox
/// (<see cref="IRenderSession.VerifyAsync"/>). Independently of what the imager reported it maps each redaction to the
/// pixels of the decoded source with <see cref="RedactionGeometry.BurnedPixels"/> and checks:
/// <list type="bullet">
/// <item>the file is one plain image of the expected format (<see cref="ProducedImageContainer"/>);</item>
/// <item>the produced image has the source page's width and holds the page at the reported row, and the source's content
/// outside the boxes is there (otherwise the boxes would be checked in the wrong place);</item>
/// <item>every pixel of a black box is black; a labelled box is white with a black label (at least half white);</item>
/// <item>no box reproduces the source: of the box's source pixels that differ from the burned colour, fewer than half
/// are found unchanged in the produced image.</item>
/// </list>
/// TIFF G4 and PNG are lossless, so pixels are compared exactly (a TIFF G4 page against the source thresholded as the
/// endorser thresholds it); JPEG pixels within <see cref="JpegTolerance"/> of luminance.
/// </summary>
public static class BurnInVerifier
{
    /// <summary>Bump when a rule changes; recorded in the volume manifest with the results.</summary>
    public const int Version = 1;

    /// <summary>How far a JPEG pixel may be from its expected value (compression noise at quality 90).</summary>
    public const int JpegTolerance = 48;

    /// <summary>A box whose source differs from the burned colour in fewer pixels has nothing to reproduce.</summary>
    public const int MinContentPixels = 16;

    /// <summary>The alignment check needs this much source ink outside the boxes.</summary>
    public const int MinAlignmentInk = 64;

    public static string VersionText => "burn-in verifier " + Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Verifies one produced page in this process (the sandbox child calls this).</summary>
    public static BurnInResult Verify(BurnInRequest request, RenderSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        settings ??= new RenderSettings();
        var issues = new List<BurnInIssue>();
        var bytes = File.ReadAllBytes(request.ProducedPath);
        if (ProducedImageContainer.Inspect(bytes, request.Format) is not null)
        {
            issues.Add(new BurnInIssue(BurnInCodes.HiddenData));
        }

        int dpi;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            dpi = ImageProbe.Probe(stream)?.Frames[0].DpiX ?? 0;
        }

        SKBitmap produced;
        try
        {
            (produced, _, _) = PageEndorser.LoadPage(request.ProducedPath, 0, null, settings);
        }
        catch (RenderException)
        {
            issues.Add(new BurnInIssue(BurnInCodes.ProducedUnreadable));
            return new BurnInResult(0, 0, 0, issues);
        }

        using (produced)
        {
            SKBitmap source;
            try
            {
                (source, _, _) = PageEndorser.LoadPage(request.SourcePath, request.Frame, null, settings);
            }
            catch (RenderException)
            {
                issues.Add(new BurnInIssue(BurnInCodes.SourceUnreadable));
                return new BurnInResult(produced.Width, 0, 0, issues);
            }

            using (source)
            {
                if (produced.Width != source.Width || request.PageTopPx < 0 || (long)request.PageTopPx + source.Height > produced.Height)
                {
                    issues.Add(new BurnInIssue(BurnInCodes.PageGeometry));
                    return new BurnInResult(produced.Width, source.Height, 0, issues);
                }

                var page = new Raster(produced, request.PageTopPx, source.Height);
                var original = new Raster(source, 0, source.Height);
                var rule = new Rule(request.Format == PageImageFormat.Jpeg ? JpegTolerance : 0, request.Format == PageImageFormat.TiffG4);
                var boxes = request.Redactions.Select(r => (Box: RedactionGeometry.BurnedPixels(r.Rect, source.Width, source.Height), r.Type)).ToList();
                var checkedBoxes = 0;
                for (var i = 0; i < boxes.Count; i++)
                {
                    var (box, type) = boxes[i];
                    if (box.Width <= 0 || box.Height <= 0)
                    {
                        continue;
                    }

                    checkedBoxes++;
                    CheckBox(page, original, rule, boxes, i, issues);
                }

                CheckAlignment(page, original, rule, boxes.Select(b => b.Box).ToList(), PageEndorser.FrameWidth(dpi > 0 ? dpi : 150) + 1, issues);
                return new BurnInResult(produced.Width, source.Height, checkedBoxes, issues);
            }
        }
    }

    /// <summary>Checks a request before it reaches the verifier (the parent of a sandbox checks it too).</summary>
    public static void Validate(BurnInRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProducedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        if (request.Format is not (PageImageFormat.TiffG4 or PageImageFormat.Jpeg or PageImageFormat.Png) || request.Frame < 0 || request.PageTopPx < 0)
        {
            throw new ArgumentException("A produced page is TIFF G4, JPEG or PNG; frame and page top are not negative.", nameof(request));
        }

        if (request.Redactions is not { Count: > 0 and <= PageEndorser.MaxRedactions } redactions
            || redactions.Any(r => r is null || !r.Rect.IsWithinPage || !Enum.IsDefined(r.Type)))
        {
            throw new ArgumentException($"1–{PageEndorser.MaxRedactions} redactions, each inside the page.", nameof(request));
        }
    }

    private static void CheckBox(Raster page, Raster original, Rule rule, List<(PixelRect Box, RedactionType Type)> boxes, int index, List<BurnInIssue> issues)
    {
        var (box, type) = boxes[index];
        var blackBoxes = type == RedactionType.Labelled ? boxes.Where(b => b.Type == RedactionType.Black).Select(b => b.Box).ToList() : [];
        long considered = 0, wrong = 0, white = 0, content = 0, reproduced = 0;
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            for (var x = box.X; x < box.X + box.Width; x++)
            {
                // A black box wins over a labelled box it overlaps; those pixels are checked as black.
                if (blackBoxes.Count > 0 && blackBoxes.Exists(b => Inside(b, x, y)))
                {
                    continue;
                }

                considered++;
                var p = page.At(x, y);
                var s = rule.AsProduced(original.At(x, y));
                if (type == RedactionType.Black)
                {
                    wrong += rule.IsBlack(p) ? 0 : 1;
                }
                else
                {
                    var isWhite = rule.IsWhite(p);
                    white += isWhite ? 1 : 0;
                    wrong += isWhite || rule.IsBlack(p) || (rule.Tolerance > 0 && p.Chroma <= rule.Tolerance) ? 0 : 1;
                }

                if (type == RedactionType.Black ? s.Max > 2 * rule.Tolerance : s.Min < 255 - 2 * rule.Tolerance)
                {
                    content++;
                    reproduced += rule.Same(p, s) ? 1 : 0;
                }
            }
        }

        if (considered == 0)
        {
            return;
        }

        if (wrong > 0 || (type == RedactionType.Labelled && white * 2 < considered))
        {
            issues.Add(new BurnInIssue(BurnInCodes.BoxNotOpaque, index + 1, wrong > 0 ? wrong : considered - white));
        }

        if (content >= MinContentPixels && reproduced * 2 >= content)
        {
            issues.Add(new BurnInIssue(BurnInCodes.BoxShowsSource, index + 1, reproduced));
        }
    }

    /// <summary>The source's ink outside the boxes (and their frames) must be found at the reported place.</summary>
    private static void CheckAlignment(Raster page, Raster original, Rule rule, List<PixelRect> boxes, int margin, List<BurnInIssue> issues)
    {
        var width = original.Width;
        var excluded = new bool[width];
        long ink = 0, found = 0;
        for (var y = 0; y < original.Height; y++)
        {
            Array.Clear(excluded);
            foreach (var box in boxes)
            {
                if (y >= box.Y - margin && y < box.Y + box.Height + margin)
                {
                    Array.Fill(excluded, true, Math.Max(0, box.X - margin), Math.Max(0, Math.Min(width, box.X + box.Width + margin) - Math.Max(0, box.X - margin)));
                }
            }

            for (var x = 0; x < width; x++)
            {
                if (excluded[x])
                {
                    continue;
                }

                var s = rule.AsProduced(original.At(x, y));
                if (s.Gray >= 128)
                {
                    continue;
                }

                ink++;
                found += rule.Same(page.At(x, y), s) ? 1 : 0;
            }
        }

        if (ink >= MinAlignmentInk && found * 2 < ink)
        {
            issues.Add(new BurnInIssue(BurnInCodes.PageMisaligned, null, ink - found));
        }
    }

    private static bool Inside(PixelRect box, int x, int y) => x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;

    private readonly record struct Pixel(int Min, int Max, int Gray)
    {
        public int Chroma => Max - Min;
    }

    /// <param name="Bitonal">The produced format is TIFF G4: the source is compared as the endorser thresholds it.</param>
    private readonly record struct Rule(int Tolerance, bool Bitonal)
    {
        // Lossy: luminance only, as JPEG subsamples colour and bleeds it a pixel or two into a box's edge.
        public bool IsBlack(Pixel p) => Tolerance == 0 ? p.Max == 0 : p.Gray <= Tolerance;

        public bool IsWhite(Pixel p) => Tolerance == 0 ? p.Min == 255 : p.Gray >= 255 - Tolerance;

        public bool Same(Pixel a, Pixel b) => Math.Abs(a.Gray - b.Gray) <= Tolerance && Math.Abs(a.Chroma - b.Chroma) <= Tolerance;

        public Pixel AsProduced(Pixel source) => Bitonal ? (source.Gray < 128 ? new Pixel(0, 0, 0) : new Pixel(255, 255, 255)) : source;
    }

    /// <summary>Rows <c>[top, top + height)</c> of a decoded bitmap (8-bit gray or 32-bit) as the page's coordinates.</summary>
    private sealed class Raster(SKBitmap bitmap, int top, int height)
    {
        private readonly byte[] _pixels = bitmap.GetPixelSpan().ToArray();
        private readonly int _rowBytes = bitmap.RowBytes;
        private readonly int _bpp = bitmap.BytesPerPixel;

        public int Width { get; } = bitmap.Width;

        public int Height { get; } = height;

        public Pixel At(int x, int y)
        {
            var at = (y + top) * _rowBytes + x * _bpp;
            if (_bpp == 1)
            {
                return new Pixel(_pixels[at], _pixels[at], _pixels[at]);
            }

            int a = _pixels[at], b = _pixels[at + 1], c = _pixels[at + 2];
            return new Pixel(Math.Min(a, Math.Min(b, c)), Math.Max(a, Math.Max(b, c)), (a + b + c) / 3);
        }
    }
}
