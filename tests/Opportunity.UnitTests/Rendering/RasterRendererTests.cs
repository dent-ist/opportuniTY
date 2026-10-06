using AwesomeAssertions;

using Opportunity.Core.Pages;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Rendering.Renderers;

using SkiaSharp;

using PageImageFormat = Opportunity.Core.Pages.PageImageFormat;

namespace Opportunity.UnitTests.Rendering;

/// <summary>
/// E11-T02 renderer: synthetic PDFs (hand-written) and page images (hand-encoded G4 TIFF, JPEG, PNG) rendered to review
/// PNGs and thumbnails in a temp directory removed afterwards.
/// </summary>
public sealed class RasterRendererTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("opp-render-test-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_PDF_renders_every_page_at_the_review_resolution_with_a_thumbnail()
    {
        var pages = await RenderAsync(SyntheticPdf.Create(3));

        pages.Should().HaveCount(3);
        pages.Select(p => p.Index).Should().Equal(0, 1, 2);
        foreach (var page in pages)
        {
            page.Error.Should().BeNull();
            (page.WidthPt, page.HeightPt).Should().Be((612m, 792m));
            page.ColorMode.Should().Be(PageColorMode.Gray);
            page.Review!.Should().BeEquivalentTo(new { WidthPx = 1275, HeightPx = 1650, Dpi = 150, Format = PageImageFormat.Png });
            page.Thumbnail!.HeightPx.Should().Be(200);
            page.Thumbnail.WidthPx.Should().Be(155);
            Decode(page.Review.Path).Should().Be((1275, 1650, SKColorType.Gray8));
            Decode(page.Thumbnail.Path).Should().Be((155, 200, SKColorType.Gray8));
        }
    }

    [Fact]
    public async Task Rotated_and_color_PDF_pages_record_upright_geometry_and_color()
    {
        var pages = await RenderAsync(SyntheticPdf.Create(1, rotate: 90, colorBox: true));

        var page = pages.Single();
        (page.WidthPt, page.HeightPt).Should().Be((792m, 612m));
        page.ColorMode.Should().Be(PageColorMode.Color);
        (page.Review!.WidthPx, page.Review.HeightPx).Should().Be((1650, 1275));
    }

    [Fact]
    public async Task An_oversized_PDF_page_is_rendered_within_the_review_bounds_keeping_its_aspect_ratio()
    {
        var page = (await RenderAsync(SyntheticPdf.Create(1, widthPt: 7200, heightPt: 3600))).Single();

        page.Review!.WidthPx.Should().Be(4000);
        page.Review.HeightPx.Should().Be(2000);
        page.Review.Dpi.Should().Be(40);
    }

    [Fact]
    public async Task Rendering_is_deterministic()
    {
        var pdf = SyntheticPdf.Create(2, colorBox: true);
        var first = await RenderAsync(pdf);
        var firstBytes = first.Select(p => File.ReadAllBytes(p.Review!.Path)).ToList();
        var second = await RenderAsync(pdf);

        second.Select(p => File.ReadAllBytes(p.Review!.Path)).Should().BeEquivalentTo(firstBytes, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_multi_page_G4_TIFF_is_split_into_one_raster_per_frame()
    {
        var tiff = PageImages.Tiff(["A-1", "A-2", "A-3"], width: 850, height: 1100, dpi: 100);

        var pages = await RenderAsync(tiff);

        pages.Should().HaveCount(3);
        pages.Should().AllSatisfy(p =>
        {
            p.Error.Should().BeNull();
            p.ColorMode.Should().Be(PageColorMode.Bitonal);
            (p.WidthPt, p.HeightPt).Should().Be((612m, 792m));
            p.Review!.Should().BeEquivalentTo(new { WidthPx = 850, HeightPx = 1100, Dpi = 100, Format = PageImageFormat.Png });
        });
        var pixels = SKBitmap.Decode(pages[1].Review!.Path);
        pixels.GetPixel(10, 10).Should().Be(SKColors.White);
    }

    [Fact]
    public async Task Selected_frames_only_and_thumbnails_only_when_review_is_not_needed()
    {
        var tiff = PageImages.Tiff(["B-1", "B-2", "B-3"], width: 850, height: 1100, dpi: 100);

        var pages = await RenderAsync(tiff, pages: [2], review: false);

        pages.Should().ContainSingle().Which.Index.Should().Be(2);
        pages[0].Review.Should().BeNull();
        pages[0].Thumbnail.Should().NotBeNull();
    }

    [Fact]
    public async Task JPEG_and_PNG_page_images_render_at_their_own_resolution()
    {
        var jpeg = (await RenderAsync(PageImages.Jpeg("J-1"))).Single();
        var png = (await RenderAsync(PageImages.Png("P-1"))).Single();

        jpeg.Review!.Should().BeEquivalentTo(new { WidthPx = 850, HeightPx = 1100, Dpi = 100 });
        png.Review!.Should().BeEquivalentTo(new { WidthPx = 850, HeightPx = 1100, Dpi = 100 });
        (jpeg.WidthPt, jpeg.HeightPt).Should().Be((612m, 792m));
    }

    [Fact]
    public async Task Fax_resolution_TIFF_frames_are_resampled_to_square_pixels()
    {
        var tiff = FaxTiff();

        var page = (await RenderAsync(tiff)).Single();

        page.Error.Should().BeNull();
        page.Review!.Dpi.Should().Be(204);
        page.Review.WidthPx.Should().Be(1728);
        page.Review.HeightPx.Should().Be((int)Math.Round(1100 * 204d / 98));
    }

    [Fact]
    public async Task A_frame_over_the_source_pixel_limit_fails_only_its_page()
    {
        var tiff = PageImages.Tiff(["C-1", "C-2"], width: 850, height: 1100, dpi: 100);
        var renderer = new RasterRenderer(new RenderSettings { MaxSourcePixels = 1_000 });

        var pages = await RenderAsync(tiff, renderer: renderer);

        pages.Should().HaveCount(2).And.AllSatisfy(p => p.Error.Should().Be(RenderErrorCodes.PageTooLarge));
    }

    [Theory]
    [InlineData("%PDF-1.4\nnot really a pdf", RenderErrorCodes.Unreadable)]
    [InlineData("plain text, not a page image", RenderErrorCodes.Unsupported)]
    public async Task Unreadable_sources_fail_as_a_whole_with_a_stable_code(string content, string code)
    {
        var act = async () => await RenderAsync(System.Text.Encoding.ASCII.GetBytes(content));

        (await act.Should().ThrowAsync<RenderException>()).Which.Code.Should().Be(code);
    }

    [Fact]
    public void The_identity_names_the_libraries_and_hashes_the_settings()
    {
        var a = new RasterRenderer().Identity;
        var b = new RasterRenderer(new RenderSettings { ReviewDpi = 200 }).Identity;

        a.Name.Should().Be(RasterRenderer.RendererName);
        a.Version.Should().Contain("pdfium").And.Contain("skia").And.Contain("libtiff.net");
        a.SettingsHash.Should().HaveCount(32).And.NotEqual(b.SettingsHash);
        new RasterRenderer().Identity.SettingsHash.Should().Equal(a.SettingsHash);
    }

    [Theory]
    [InlineData(1275, 1650)]
    [InlineData(4000, 120)]
    [InlineData(120, 4000)]
    [InlineData(150, 150)]
    public void Thumbnails_keep_the_aspect_ratio_within_half_a_percent(int width, int height)
    {
        var (tw, th) = RasterWriter.ThumbnailSize(width, height, new RenderSettings());

        RasterWriter.AspectMatches(tw, th, width, height).Should().BeTrue();
        Math.Max(tw, th).Should().BeLessThanOrEqualTo(Math.Max(width, height));
    }

    private async Task<List<RenderedPage>> RenderAsync(byte[] source, IReadOnlyList<int>? pages = null, bool review = true, RasterRenderer? renderer = null)
    {
        var input = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(input, source, Ct);
        var output = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        var result = new List<RenderedPage>();
        await foreach (var page in (renderer ?? new RasterRenderer()).RenderAsync(new RenderRequest(input, output, pages, review), Ct))
        {
            result.Add(page);
        }

        return result;
    }

    private static (int Width, int Height, SKColorType ColorType) Decode(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        return (bitmap.Width, bitmap.Height, bitmap.ColorType);
    }

    /// <summary>A one-frame G4 TIFF at 204 × 98 DPI (fax "normal" mode): the hand-encoded generator with Y resolution patched.</summary>
    private static byte[] FaxTiff()
    {
        var bytes = PageImages.Tiff(["FAX-1"], width: 1728, height: 1100, dpi: 204);

        // The generator writes one RATIONAL shared by XResolution and YResolution; give YResolution its own value at the end.
        var ifd = BitConverter.ToInt32(bytes, 4);
        var count = BitConverter.ToUInt16(bytes, ifd);
        var extra = bytes.Length;
        var patched = new byte[bytes.Length + 8];
        bytes.CopyTo(patched, 0);
        BitConverter.GetBytes(98u).CopyTo(patched, extra);
        BitConverter.GetBytes(1u).CopyTo(patched, extra + 4);
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + (i * 12);
            if (BitConverter.ToUInt16(patched, entry) == 283)
            {
                BitConverter.GetBytes((uint)extra).CopyTo(patched, entry + 8);
            }
        }

        return patched;
    }
}
