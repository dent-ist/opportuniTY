using AwesomeAssertions;

using Opportunity.Core.Pages;
using Opportunity.Rendering.Sandboxing;

using SkiaSharp;

using PageImageFormat = Opportunity.Core.Pages.PageImageFormat;

namespace Opportunity.UnitTests.Rendering.Sandbox;

/// <summary>The worker trusts nothing the sandboxed renderer reports: names, links, sizes, formats and codes are checked.</summary>
public sealed class SandboxOutputValidatorTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("opp-sandbox-out-").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public void A_PNG_in_the_output_directory_with_the_reported_size_is_accepted()
    {
        WritePng("p1.png", 30, 20);

        var page = SandboxOutputValidator.Validate(Page(Raster("p1.png", 30, 20)), _output, null, 1 << 20);

        page.Should().NotBeNull();
        page!.Review!.Path.Should().Be(Path.Combine(_output, "p1.png"));
        page.Review.ContentType.Should().Be("image/png");
    }

    [Theory]
    [InlineData("../p1.png")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/p1.png")]
    [InlineData("..")]
    [InlineData("")]
    public void A_name_that_is_a_path_is_rejected(string name)
    {
        WritePng("p1.png", 30, 20);

        SandboxOutputValidator.Validate(Page(Raster(name, 30, 20)), _output, null, 1 << 20).Should().BeNull();
    }

    [Fact]
    public void A_symbolic_link_a_non_PNG_a_size_mismatch_or_an_oversized_file_is_rejected()
    {
        WritePng("real.png", 30, 20);
        File.CreateSymbolicLink(Path.Combine(_output, "link.png"), Path.Combine(_output, "real.png"));
        File.WriteAllText(Path.Combine(_output, "page.png"), "<html><script>alert(1)</script></html>");

        SandboxOutputValidator.Validate(Page(Raster("link.png", 30, 20)), _output, null, 1 << 20).Should().BeNull();
        SandboxOutputValidator.Validate(Page(Raster("page.png", 30, 20)), _output, null, 1 << 20).Should().BeNull();
        SandboxOutputValidator.Validate(Page(Raster("real.png", 31, 20)), _output, null, 1 << 20).Should().BeNull();
        SandboxOutputValidator.Validate(Page(Raster("real.png", 30, 20)), _output, null, 10).Should().BeNull();
        SandboxOutputValidator.Validate(Page(Raster("real.png", 30, 20) with { Format = PageImageFormat.Jpeg }), _output, null, 1 << 20).Should().BeNull();
    }

    [Fact]
    public void A_page_that_was_not_requested_or_an_unknown_error_code_is_rejected()
    {
        WritePng("p1.png", 30, 20);

        SandboxOutputValidator.Validate(Page(Raster("p1.png", 30, 20)), _output, [1, 2], 1 << 20).Should().BeNull();
        SandboxOutputValidator.Validate(Page(null) with { Error = "<script>" }, _output, null, 1 << 20).Should().BeNull();
        SandboxOutputValidator.Validate(Page(null) with { Error = "render-page-too-large" }, _output, null, 1 << 20)!.Error.Should().Be("render-page-too-large");
    }

    private static SandboxPage Page(SandboxRaster? review) => new(0, 612, 792, PageColorMode.Gray, review, null, null);

    private static SandboxRaster Raster(string name, int width, int height) => new(name, width, height, 72, PageImageFormat.Png);

    private void WritePng(string name, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(_output, name), data.ToArray());
    }
}
