using System.Security.Cryptography;

using AwesomeAssertions;

using BitMiracle.LibTiff.Classic;

using Opportunity.Core.Pages;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Rendering.Endorsing;
using Opportunity.Rendering.Renderers;

using SkiaSharp;

using PageImageFormat = Opportunity.Core.Pages.PageImageFormat;

namespace Opportunity.UnitTests.Rendering;

/// <summary>
/// E12-T04: the page endorser stamps Bates numbers and designations deterministically (same inputs, same bytes), never
/// overwrites the page when the canvas is expanded, puts every stamp where the specification says, and reads every page
/// image the review renderer reads. Work happens in a temp directory removed afterwards.
/// </summary>
public sealed class PageEndorserTests : IDisposable
{
    private static readonly EndorsementLayout Standard = new(
    [
        new EndorsementText(EndorsementPosition.BottomLeft, "HIGHLY CONFIDENTIAL – AEO"),
        new EndorsementText(EndorsementPosition.BottomRight, "ABC0000001"),
    ]);

    private readonly string _root = Directory.CreateTempSubdirectory("opp-endorse-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(PageImageFormat.TiffG4)]
    [InlineData(PageImageFormat.Jpeg)]
    [InlineData(PageImageFormat.Png)]
    public void The_same_page_and_layout_give_identical_bytes(PageImageFormat format)
    {
        var source = Write("page.png", ContentPng(600, 800, color: true));

        var first = PageEndorser.Endorse(Request(source, format));
        var second = PageEndorser.Endorse(Request(source, format));

        first.Content.Should().Equal(second.Content);
        first.Should().BeEquivalentTo(new { WidthPx = 600, Format = format });
        Directory.EnumerateFiles(Out()).Should().BeEmpty("the in-process endorser removes its output file");

        var other = PageEndorser.Endorse(Request(source, format) with
        {
            Layout = Standard with { Stamps = [Standard.Stamps[0], new EndorsementText(EndorsementPosition.BottomRight, "ABC0000002")] },
        });
        other.Content.Should().NotEqual(first.Content, "a different Bates number is a different page");
    }

    [Fact]
    public void A_generated_page_has_a_fixed_SHA_256_so_any_change_of_output_is_noticed()
    {
        // A blank letter page at 300 DPI with a body and two stamps, as TIFF G4: LibTiff.NET and aliased text only.
        // If this fails after a library or font upgrade, bump PageEndorser.PipelineVersion (it is recorded with every
        // produced image, Q-08) and update the hash.
        var image = PageEndorser.Endorse(new EndorseRequest(null, 0, Out(), PageImageFormat.TiffG4,
            Standard with { BodyLines = ["Document Produced in Native Format"] }, 300, 2550, 3300));

        (image.WidthPx, image.HeightPx, image.Dpi, image.ColorMode).Should().Be((2550, 3300 + Band(Standard, 300), 300, PageColorMode.Bitonal));
        Convert.ToHexStringLower(SHA256.HashData(image.Content)).Should().Be(GoldenTiffSha256);
        PageEndorser.Version.Should().StartWith("endorser 1;").And.Contain("font LiberationSans-Regular 2.1.5");
    }

    [Fact]
    public void An_expanded_canvas_keeps_every_pixel_of_the_page_and_adds_bands_for_the_stamps()
    {
        var png = ContentPng(500, 700, color: true);
        var source = Write("page.png", png);
        var layout = Standard with { Stamps = [.. Standard.Stamps, new EndorsementText(EndorsementPosition.TopCenter, "Production 1")] };

        var image = PageEndorser.Endorse(Request(source, PageImageFormat.Png) with { Layout = layout });

        using var original = SKBitmap.Decode(png);
        using var endorsed = SKBitmap.Decode(image.Content);
        var top = Band(layout, 100);
        endorsed.Width.Should().Be(500);
        endorsed.Height.Should().Be(700 + 2 * top, "a band above and one below");
        image.ColorMode.Should().Be(PageColorMode.Color);
        for (var y = 0; y < original.Height; y += 7)
        {
            for (var x = 0; x < original.Width; x += 5)
            {
                endorsed.GetPixel(x, y + top).Should().Be(original.GetPixel(x, y), "the page itself is never overwritten");
            }
        }

        Ink(endorsed, 0, 0, 500, top).Should().BeGreaterThan(0, "the top stamp is in the top band");
        Ink(endorsed, 0, 700 + top, 250, top).Should().BeGreaterThan(0, "bottom left");
        Ink(endorsed, 250, 700 + top, 250, top).Should().BeGreaterThan(0, "bottom right");
    }

    [Fact]
    public void Without_canvas_expansion_the_stamps_are_drawn_over_the_page_on_white_boxes()
    {
        var source = Write("page.png", ContentPng(500, 700, color: false, dark: true));

        var image = PageEndorser.Endorse(Request(source, PageImageFormat.Png) with { Layout = Standard with { ExpandCanvas = false, MarginPt = 9 } });

        using var endorsed = SKBitmap.Decode(image.Content);
        (endorsed.Width, endorsed.Height).Should().Be((500, 700));
        image.ColorMode.Should().Be(PageColorMode.Gray);
        // A dark page: the white box behind each stamp makes it legible.
        White(endorsed, 0, 640, 250, 60).Should().BeGreaterThan(200);
        White(endorsed, 250, 640, 250, 60).Should().BeGreaterThan(200);
        White(endorsed, 0, 0, 500, 300).Should().Be(0, "the rest of the page is untouched");
    }

    [Theory]
    [InlineData(EndorsementPosition.TopLeft, 0, 0)]
    [InlineData(EndorsementPosition.TopCenter, 1, 0)]
    [InlineData(EndorsementPosition.TopRight, 2, 0)]
    [InlineData(EndorsementPosition.BottomLeft, 0, 1)]
    [InlineData(EndorsementPosition.BottomCenter, 1, 1)]
    [InlineData(EndorsementPosition.BottomRight, 2, 1)]
    public void Each_position_puts_its_stamp_in_its_third_of_its_band(EndorsementPosition position, int column, int row)
    {
        var source = Write("page.png", ContentPng(900, 600, color: false, bars: false));
        var layout = new EndorsementLayout([new EndorsementText(position, "CONF")]);

        var image = PageEndorser.Endorse(Request(source, PageImageFormat.Png) with { Layout = layout });

        using var endorsed = SKBitmap.Decode(image.Content);
        var band = Band(layout, 100);
        endorsed.Height.Should().Be(600 + band);
        var y = row == 0 ? 0 : 600;
        for (var c = 0; c < 3; c++)
        {
            Ink(endorsed, c * 300, y, 300, band).Should().Be(c == column ? Ink(endorsed, 0, 0, 900, endorsed.Height) : 0);
        }
    }

    [Fact]
    public void Every_frame_of_a_multi_page_TIFF_is_endorsed_with_its_own_label()
    {
        var source = Write("doc.tif", PageImages.Tiff(["P-1", "P-2", "P-3"], width: 850, height: 1100, dpi: 100));
        var pages = new List<EndorsedImage>();
        for (var frame = 0; frame < 3; frame++)
        {
            pages.Add(PageEndorser.Endorse(new EndorseRequest(source, frame, Out(), PageImageFormat.TiffG4, Standard with
            {
                Stamps = [Standard.Stamps[0], new EndorsementText(EndorsementPosition.BottomRight, $"ABC000000{frame + 1}")],
            })));
        }

        pages.Select(p => Convert.ToHexString(SHA256.HashData(p.Content))).Should().OnlyHaveUniqueItems();
        foreach (var page in pages)
        {
            (page.WidthPx, page.Dpi, page.Format).Should().Be((850, 100, PageImageFormat.TiffG4));
            var pixels = ReadTiff(page.Content, out var width, out var height);
            height.Should().Be(1100 + Band(Standard, 100));
            CountBlack(pixels, width, 0, 1100, width, height - 1100).Should().BeGreaterThan(0, "every page carries the endorsement");
            CountBlack(pixels, width, 0, 0, width, 1100).Should().Be(0, "the blank page itself stays blank");
        }
    }

    [Fact]
    public void A_row_too_wide_for_the_page_is_drawn_smaller_instead_of_being_cut_off()
    {
        var source = Write("narrow.png", ContentPng(200, 300, color: false));
        var layout = new EndorsementLayout(
        [
            new EndorsementText(EndorsementPosition.BottomLeft, "HIGHLY CONFIDENTIAL – ATTORNEYS' EYES ONLY"),
            new EndorsementText(EndorsementPosition.BottomRight, "ABC0000000001"),
        ], FontSizePt: 24, MarginPt: 4);

        var image = PageEndorser.Endorse(Request(source, PageImageFormat.Png) with { Layout = layout });

        using var endorsed = SKBitmap.Decode(image.Content);
        endorsed.Width.Should().Be(200);
        var band = endorsed.Height - 300;
        band.Should().BeLessThan(Band(layout, 100), "the font was reduced to fit");
        Ink(endorsed, 0, 300, 4, band).Should().Be(0, "nothing is drawn in the margin");
    }

    [Fact]
    public void JPEG_and_PNG_sources_are_read_and_the_resolution_is_recorded()
    {
        var jpeg = PageEndorser.Endorse(Request(Write("p.jpg", PageImages.Jpeg("x", 425, 550, 50)), PageImageFormat.Jpeg));
        var png = PageEndorser.Endorse(Request(Write("p.png", PageImages.Png("x", 425, 550, 50)), PageImageFormat.TiffG4));

        jpeg.Should().BeEquivalentTo(new { WidthPx = 425, Dpi = 50, Format = PageImageFormat.Jpeg });
        jpeg.Content.AsSpan(13, 5).ToArray().Should().Equal(1, 0, 50, 0, 50);
        png.Should().BeEquivalentTo(new { WidthPx = 425, Dpi = 50, Format = PageImageFormat.TiffG4, ColorMode = PageColorMode.Bitonal });
        ReadTiff(png.Content, out _, out var height).Should().NotBeEmpty();
        height.Should().Be(png.HeightPx);
    }

    [Fact]
    public void Unreadable_pages_fail_with_a_stable_code_and_invalid_requests_are_refused()
    {
        var garbage = Write("garbage.bin", [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var act = () => PageEndorser.Endorse(Request(garbage, PageImageFormat.TiffG4));
        act.Should().Throw<RenderException>().Which.Code.Should().Be(RenderErrorCodes.Unsupported);

        var tiff = Write("one.tif", PageImages.Tiff(["only"], width: 100, height: 100, dpi: 100));
        var missingFrame = () => PageEndorser.Endorse(Request(tiff, PageImageFormat.TiffG4) with { Frame = 1 });
        missingFrame.Should().Throw<RenderException>().Which.Code.Should().Be(RenderErrorCodes.PageFailed);

        var duplicate = () => PageEndorser.Validate(Request(tiff, PageImageFormat.Png) with
        {
            Layout = new EndorsementLayout([new(EndorsementPosition.TopLeft, "a"), new(EndorsementPosition.TopLeft, "b")]),
        });
        duplicate.Should().Throw<ArgumentException>();
        var control = () => PageEndorser.Validate(Request(tiff, PageImageFormat.Png) with
        {
            Layout = new EndorsementLayout([new(EndorsementPosition.TopLeft, "a\nb")]),
        });
        control.Should().Throw<ArgumentException>();
        var webp = () => PageEndorser.Validate(Request(tiff, PageImageFormat.WebP));
        webp.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Redactions_are_burned_into_the_page_pixels_before_it_is_endorsed_deterministically()
    {
        // E12-T05: a black box is black to the last device pixel (rounded outward); a labelled box is white with its label
        // in black, framed just outside; nothing else of the page changes.
        var source = Write("page.png", ContentPng(600, 800, color: true));
        var black = new NormalizedRect(100_000, 100_000, 300_000, 150_000);
        var labelled = new NormalizedRect(400_000, 500_000, 500_000, 120_000);
        var request = Request(source, PageImageFormat.Png) with
        {
            Redactions = [new BurnedRedaction(black, RedactionType.Black), new BurnedRedaction(labelled, RedactionType.Labelled, "Redacted – Privileged")],
        };

        var burned = PageEndorser.Endorse(request);
        var plain = PageEndorser.Endorse(Request(source, PageImageFormat.Png));
        PageEndorser.Endorse(request).Content.Should().Equal(burned.Content, "burning is deterministic");
        (burned.PageTopPx, burned.PageHeightPx, burned.WidthPx).Should().Be((0, 800, 600));

        using var image = SKBitmap.Decode(burned.Content);
        using var original = SKBitmap.Decode(plain.Content);
        var box = RedactionGeometry.BurnedPixels(black, 600, 800);
        box.Should().Be(new PixelRect(60, 80, 180, 120));
        Ink(image, box.X, box.Y, box.Width, box.Height).Should().Be(box.Width * box.Height, "every pixel inside a black box is black");
        var label = RedactionGeometry.BurnedPixels(labelled, 600, 800);
        var white = White(image, label.X, label.Y, label.Width, label.Height);
        var ink = Ink(image, label.X, label.Y, label.Width, label.Height);
        (white + ink).Should().Be(label.Width * label.Height, "inside a labelled box there is only the white fill and the label's black glyphs");
        ink.Should().BePositive("the label is printed");
        Ink(image, label.X - 1, label.Y, 1, label.Height).Should().Be(label.Height, "the frame is drawn just outside the box");
        for (var y = 0; y < 800; y += 7)
        {
            for (var x = 0; x < 600; x += 7)
            {
                var inside = (x >= box.X - 3 && x < box.X + box.Width + 3 && y >= box.Y - 3 && y < box.Y + box.Height + 3)
                    || (x >= label.X - 3 && x < label.X + label.Width + 3 && y >= label.Y - 3 && y < label.Y + label.Height + 3);
                if (!inside)
                {
                    image.GetPixel(x, y).Should().Be(original.GetPixel(x, y), "pixels outside the redactions are the page's own");
                }
            }
        }

        var tiff = PageEndorser.Endorse(request with { Format = PageImageFormat.TiffG4 });
        var bits = ReadTiff(tiff.Content, out var width, out _);
        CountBlack(bits, width, box.X, box.Y, box.Width, box.Height).Should().Be(box.Width * box.Height, "the burn survives TIFF G4 thresholding");

        var outside = () => PageEndorser.Validate(request with { Redactions = [new BurnedRedaction(new NormalizedRect(900_000, 0, 200_000, 10), RedactionType.Black)] });
        outside.Should().Throw<ArgumentException>();
    }

    /// <summary>Band height for a layout at a resolution: margin + text line + padding (see PageEndorser).</summary>
    internal static int Band(EndorsementLayout layout, int dpi)
    {
        var fontPx = Math.Max(PageEndorser.MinFontPx, (int)Math.Round(layout.FontSizePt * dpi / 72d, MidpointRounding.AwayFromZero));
        var margin = (int)Math.Round(layout.MarginPt * dpi / 72d, MidpointRounding.AwayFromZero);
        var pad = Math.Max(1, (fontPx + 4) / 8);
        using var typeface = LoadTypeface();
        using var font = new SKFont(typeface, fontPx) { Edging = SKFontEdging.Alias, Hinting = SKFontHinting.Normal, Subpixel = false };
        var metrics = font.Metrics;
        return margin + (int)Math.Ceiling(-metrics.Ascent) + (int)Math.Ceiling(metrics.Descent) + 3 * pad;
    }

    private const string GoldenTiffSha256 = "4d1cc6a38bdb30f0ecd99827c889e357a21d62d5130d6c20b38aae6a10a25990";

    private static SKTypeface LoadTypeface()
    {
        using var stream = typeof(PageEndorser).Assembly.GetManifestResourceStream("Opportunity.Rendering.Fonts.LiberationSans-Regular.ttf")!;
        return SKTypeface.FromStream(stream);
    }

    private EndorseRequest Request(string source, PageImageFormat format) => new(source, 0, Out(), format, Standard);

    private string Out() => Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A page with content: a gradient (colour or gray, light or dark) and black bars, at 100 DPI.</summary>
    private static byte[] ContentPng(int width, int height, bool color, bool dark = false, bool bars = true)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = (byte)((dark ? 40 : 160) + (x + y) % 60);
                bitmap.SetPixel(x, y, bars && (y / 40) % 5 == 0 && x % 50 < 25
                    ? SKColors.Black
                    : color ? new SKColor(v, (byte)(255 - v), (byte)(x % 256)) : new SKColor(v, v, v));
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        // pHYs: 100 DPI = 3937 pixels per metre.
        return WithPhys(bytes, 3937);
    }

    private static byte[] WithPhys(byte[] png, uint pixelsPerMetre)
    {
        var chunk = new byte[21];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), pixelsPerMetre);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), pixelsPerMetre);
        chunk[16] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17), Crc32.Compute(chunk.AsSpan(4, 13)));
        // After the signature (8) and IHDR (25).
        return [.. png.AsSpan(0, 33), .. chunk, .. png.AsSpan(33)];
    }

    /// <summary>Pure black pixels (stamp ink) in a rectangle.</summary>
    private static int Ink(SKBitmap bitmap, int x0, int y0, int w, int h) => Count(bitmap, x0, y0, w, h, c => c.Red == 0 && c.Green == 0 && c.Blue == 0);

    private static int White(SKBitmap bitmap, int x0, int y0, int w, int h) => Count(bitmap, x0, y0, w, h, c => c.Red == 255 && c.Green == 255 && c.Blue == 255);

    private static int Count(SKBitmap bitmap, int x0, int y0, int w, int h, Func<SKColor, bool> predicate)
    {
        var n = 0;
        for (var y = Math.Max(0, y0); y < Math.Min(bitmap.Height, y0 + h); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(bitmap.Width, x0 + w); x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>Decodes a single-page bitonal TIFF (true = black).</summary>
    private static bool[] ReadTiff(byte[] content, out int width, out int height)
    {
        using var stream = new MemoryStream(content);
        using var tiff = Tiff.ClientOpen("endorsed", "r", stream, new TiffStream())!;
        width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        tiff.GetField(TiffTag.COMPRESSION)[0].ToInt().Should().Be((int)Compression.CCITTFAX4);
        var pixels = new bool[width * height];
        var line = new byte[tiff.ScanlineSize()];
        for (var y = 0; y < height; y++)
        {
            tiff.ReadScanline(line, y).Should().BeTrue();
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = (line[x >> 3] & (0x80 >> (x & 7))) != 0;
            }
        }

        return pixels;
    }

    private static int CountBlack(bool[] pixels, int stride, int x0, int y0, int w, int h)
    {
        var n = 0;
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                n += pixels[y * stride + x] ? 1 : 0;
            }
        }

        return n;
    }
}
