using AwesomeAssertions;

using Opportunity.Application.Productions;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Rendering.Endorsing;

using SkiaSharp;

using PageImageFormat = Opportunity.Core.Pages.PageImageFormat;

namespace Opportunity.UnitTests.Rendering;

/// <summary>
/// E12-T06: the burn-in verifier passes every page the endorser burned correctly (TIFF G4, JPEG, PNG; black and labelled
/// boxes; colour and gray sources; boxes over blank or black areas) and fails pages that leak: unburned or misplaced boxes,
/// a page reported at the wrong place, a produced file with hidden data, unreadable images. Work happens in a temp
/// directory removed afterwards.
/// </summary>
public sealed class BurnInVerifierTests : IDisposable
{
    private static readonly EndorsementLayout Layout = new(
    [
        new EndorsementText(EndorsementPosition.TopCenter, "Production 1"),
        new EndorsementText(EndorsementPosition.BottomRight, "ABC0000001"),
    ]);

    private static readonly NormalizedRect BlackRect = new(100_000, 100_000, 300_000, 150_000);
    private static readonly NormalizedRect LabelRect = new(400_000, 500_000, 500_000, 120_000);

    private readonly string _root = Directory.CreateTempSubdirectory("opp-burnin-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static TheoryData<PageImageFormat, bool, bool> Formats => new()
    {
        { PageImageFormat.TiffG4, false, true },
        { PageImageFormat.TiffG4, true, false },
        { PageImageFormat.Jpeg, true, true },
        { PageImageFormat.Jpeg, false, false },
        { PageImageFormat.Png, true, true },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_correctly_burned_page_passes(PageImageFormat format, bool color, bool expand)
    {
        var source = Write("page.png", ContentPng(600, 800, color));
        var redactions = Burned();
        var image = PageEndorser.Endorse(new EndorseRequest(source, 0, Out(), format, Layout with { ExpandCanvas = expand }, Redactions: redactions));
        var produced = Write("produced", image.Content);

        var result = BurnInVerifier.Verify(new BurnInRequest(produced, format, source, 0, image.PageTopPx, redactions));

        result.Issues.Should().BeEmpty();
        result.Should().BeEquivalentTo(new { WidthPx = 600, PageHeightPx = 800, BoxesChecked = 2 });
    }

    [Theory]
    [InlineData(PageImageFormat.TiffG4)]
    [InlineData(PageImageFormat.Jpeg)]
    [InlineData(PageImageFormat.Png)]
    public void A_page_whose_boxes_were_not_burned_fails_both_boxes(PageImageFormat format)
    {
        var source = Write("page.png", ContentPng(600, 800, color: false));
        var image = PageEndorser.Endorse(new EndorseRequest(source, 0, Out(), format, Layout));
        var produced = Write("produced", image.Content);

        var result = BurnInVerifier.Verify(new BurnInRequest(produced, format, source, 0, image.PageTopPx, Burned()));

        result.Issues.Should().Contain(i => i.Code == BurnInCodes.BoxNotOpaque && i.Box == 1);
        result.Issues.Where(i => i.Code == BurnInCodes.BoxShowsSource).Select(i => i.Box).Should().BeEquivalentTo([1, 2],
            "the source's content is visible in both boxes");
        result.Issues.Should().NotContain(i => i.Code == BurnInCodes.PageMisaligned);
    }

    [Fact]
    public void A_box_burned_in_the_wrong_place_fails_where_it_should_be()
    {
        var source = Write("page.png", ContentPng(600, 800, color: true, bars: false));
        var shifted = new NormalizedRect(BlackRect.X + 50_000, BlackRect.Y, BlackRect.W, BlackRect.H);
        var image = PageEndorser.Endorse(new EndorseRequest(source, 0, Out(), PageImageFormat.Png, Layout,
            Redactions: [new BurnedRedaction(shifted, RedactionType.Black)]));
        var produced = Write("produced", image.Content);

        var result = BurnInVerifier.Verify(new BurnInRequest(produced, PageImageFormat.Png, source, 0, image.PageTopPx,
            [new BurnedRedaction(BlackRect, RedactionType.Black)]));

        result.Issues.Should().ContainSingle(i => i.Code == BurnInCodes.BoxNotOpaque && i.Box == 1)
            .Which.Pixels.Should().Be(30L * 120, "the box's left sixth (30 of 180 columns) still shows the page");
    }

    [Fact]
    public void Boxes_over_areas_already_in_the_burned_colour_pass()
    {
        // A black box over black and a labelled box over white leave nothing of the source to see; neither is a leak.
        var source = Write("page.png", ContentPng(600, 800, color: false, bars: false, fill: (BlackRect, SKColors.Black)));
        var redactions = Burned();
        var image = PageEndorser.Endorse(new EndorseRequest(source, 0, Out(), PageImageFormat.TiffG4, Layout, Redactions: redactions));
        var produced = Write("produced", image.Content);

        BurnInVerifier.Verify(new BurnInRequest(produced, PageImageFormat.TiffG4, source, 0, image.PageTopPx, redactions)).Issues.Should().BeEmpty();
    }

    [Fact]
    public void A_page_reported_at_the_wrong_row_fails_the_alignment_or_geometry_check()
    {
        var source = Write("page.png", ContentPng(600, 800, color: false));
        var redactions = Burned();
        var image = PageEndorser.Endorse(new EndorseRequest(source, 0, Out(), PageImageFormat.TiffG4, Layout, Redactions: redactions));
        image.PageTopPx.Should().BePositive("the top stamp band sits above the page");
        var produced = Write("produced", image.Content);

        BurnInVerifier.Verify(new BurnInRequest(produced, PageImageFormat.TiffG4, source, 0, 0, redactions)).Issues
            .Should().Contain(i => i.Code == BurnInCodes.PageMisaligned);
        BurnInVerifier.Verify(new BurnInRequest(produced, PageImageFormat.TiffG4, source, 0, image.HeightPx - 10, redactions)).Issues
            .Should().Equal(new BurnInIssue(BurnInCodes.PageGeometry));

        var other = Write("other.png", ContentPng(500, 800, color: false));
        BurnInVerifier.Verify(new BurnInRequest(produced, PageImageFormat.TiffG4, other, 0, image.PageTopPx, redactions)).Issues
            .Should().Equal(new BurnInIssue(BurnInCodes.PageGeometry));
    }

    [Theory]
    [InlineData(PageImageFormat.TiffG4)]
    [InlineData(PageImageFormat.Jpeg)]
    [InlineData(PageImageFormat.Png)]
    public void Data_hidden_in_the_produced_file_is_found(PageImageFormat format)
    {
        var source = Write("page.png", ContentPng(600, 800, color: true));
        var redactions = Burned();
        var image = PageEndorser.Endorse(new EndorseRequest(source, 0, Out(), format, Layout, Redactions: redactions));
        ProducedImageContainer.Inspect(image.Content, format).Should().BeNull("the endorser writes one plain image");

        // The original page appended after the image, as a leak would carry it.
        var appended = Write("appended", [.. image.Content, .. File.ReadAllBytes(source)]);
        BurnInVerifier.Verify(new BurnInRequest(appended, format, source, 0, image.PageTopPx, redactions)).Issues
            .Should().Equal(new BurnInIssue(BurnInCodes.HiddenData));

        if (format == PageImageFormat.Jpeg)
        {
            // An EXIF segment (where a thumbnail of the original would sit).
            byte[] exif = [0xFF, 0xE1, 0x00, 0x08, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
            var withExif = Write("exif.jpg", [.. image.Content.AsSpan(0, 2), .. exif, .. image.Content.AsSpan(2)]);
            BurnInVerifier.Verify(new BurnInRequest(withExif, format, source, 0, image.PageTopPx, redactions)).Issues
                .Should().Equal(new BurnInIssue(BurnInCodes.HiddenData));
        }
    }

    [Fact]
    public void A_second_TIFF_page_behind_the_produced_page_is_hidden_data()
    {
        var tiff = PageImages.Tiff(["P-1", "P-2"], width: 850, height: 1100, dpi: 100);
        ProducedImageContainer.Inspect(tiff, PageImageFormat.TiffG4).Should().NotBeNull("a produced page is one image");
        ProducedImageContainer.Inspect(PageImages.Tiff(["P-1"], width: 850, height: 1100, dpi: 100), PageImageFormat.TiffG4)
            .Should().NotBeNull("a description tag is metadata the endorser never writes");
        ProducedImageContainer.Inspect([1, 2, 3], PageImageFormat.Jpeg).Should().NotBeNull();
        ProducedImageContainer.Inspect([0x49, 0x49, 42, 0, 0xFF, 0xFF, 0, 0], PageImageFormat.TiffG4).Should().NotBeNull("a truncated file is not an image");
    }

    [Fact]
    public void Unreadable_images_fail_and_requests_are_validated()
    {
        var source = Write("page.png", ContentPng(600, 800, color: false));
        var redactions = Burned();
        var image = PageEndorser.Endorse(new EndorseRequest(source, 0, Out(), PageImageFormat.Png, Layout, Redactions: redactions));
        var produced = Write("produced", image.Content);
        var garbage = Write("garbage", [1, 2, 3, 4, 5]);

        BurnInVerifier.Verify(new BurnInRequest(garbage, PageImageFormat.Png, source, 0, 0, redactions)).Issues
            .Should().Equal(new BurnInIssue(BurnInCodes.HiddenData), new BurnInIssue(BurnInCodes.ProducedUnreadable));
        BurnInVerifier.Verify(new BurnInRequest(produced, PageImageFormat.Png, garbage, 0, 0, redactions)).Issues
            .Should().Equal(new BurnInIssue(BurnInCodes.SourceUnreadable));
        BurnInVerifier.Verify(new BurnInRequest(produced, PageImageFormat.Png, source, 3, 0, redactions)).Issues
            .Should().ContainSingle("the source has no such frame").Which.Code.Should().Be(BurnInCodes.SourceUnreadable);

        var none = () => BurnInVerifier.Validate(new BurnInRequest(produced, PageImageFormat.Png, source, 0, 0, []));
        none.Should().Throw<ArgumentException>("a page without redactions has nothing to verify");
        var webp = () => BurnInVerifier.Validate(new BurnInRequest(produced, PageImageFormat.WebP, source, 0, 0, redactions));
        webp.Should().Throw<ArgumentException>();
    }

    private static List<BurnedRedaction> Burned() =>
        [new BurnedRedaction(BlackRect, RedactionType.Black), new BurnedRedaction(LabelRect, RedactionType.Labelled, "Redacted – Privileged")];

    private string Out()
    {
        var path = Path.Combine(_root, "out");
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Directory.CreateDirectory(path).FullName;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A page with content: a gradient (colour or gray, light or dark), black bars and text-like strokes, at 100 DPI.</summary>
    private static byte[] ContentPng(int width, int height, bool color, bool dark = false, bool bars = true, (NormalizedRect Rect, SKColor Color)? fill = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var filled = fill is { } f ? RedactionGeometry.BurnedPixels(f.Rect, width, height) : default;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = (byte)((dark ? 40 : 160) + (x + y) % 60);
                var pixel = bars && ((y / 40) % 5 == 0 && x % 50 < 25 || (y / 12) % 3 == 0 && (x * 7 + y) % 11 < 4)
                    ? SKColors.Black
                    : color ? new SKColor(v, (byte)(255 - v), (byte)(x % 256)) : new SKColor(v, v, v);
                if (fill is { } paint && x >= filled.X && x < filled.X + filled.Width && y >= filled.Y && y < filled.Y + filled.Height)
                {
                    pixel = paint.Color;
                }

                bitmap.SetPixel(x, y, pixel);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
