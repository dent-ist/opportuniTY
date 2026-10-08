using AwesomeAssertions;

using Opportunity.Core.Redactions;

namespace Opportunity.UnitTests.Redactions;

/// <summary>
/// ADR-012 §2 (E11-T04 "redactions survive re-rendering at different resolutions"): a box drawn on one raster is stored
/// as integers in normalized page space and burned on any other raster of the page, rounded outward, so it always
/// covers at least what was drawn.
/// </summary>
public class NormalizedRectTests
{
    // US Letter (8.5 × 11 in) and A4 landscape (11.69 × 8.27 in) at the DPIs a page is rendered or produced at.
    private static readonly (double WidthIn, double HeightIn)[] Pages = [(8.5, 11), (11.69, 8.27)];
    private static readonly int[] Dpis = [72, 96, 150, 200, 300, 600];

    [Fact]
    public void A_box_drawn_on_one_rendering_covers_at_least_the_same_area_on_every_other_rendering()
    {
        var random = new Random(20261006);
        foreach (var (widthIn, heightIn) in Pages)
        {
            foreach (var drawnDpi in Dpis)
            {
                var (drawnW, drawnH) = Raster(widthIn, heightIn, drawnDpi);
                for (var i = 0; i < 200; i++)
                {
                    var x = random.Next(0, drawnW - 2);
                    var y = random.Next(0, drawnH - 2);
                    var drawn = new PixelRect(x, y, random.Next(2, drawnW - x + 1), random.Next(2, drawnH - y + 1));
                    var stored = NormalizedRect.FromPixels(drawn, drawnW, drawnH);
                    stored.IsWithinPage.Should().BeTrue();

                    // The drawing raster itself gets back at least the drawn pixels.
                    var back = stored.ToPixels(drawnW, drawnH);
                    Contains(back, drawn).Should().BeTrue($"{drawn} at {drawnDpi} DPI came back as {back}");

                    foreach (var dpi in Dpis)
                    {
                        var (w, h) = Raster(widthIn, heightIn, dpi);
                        var burned = stored.ToPixels(w, h);

                        // As fractions of the page, the burned box contains the drawn box and is at most one device pixel larger per
                        // side (plus the drawing raster's own rounding).
                        var slack = (1.0 / w) + (1.0 / drawnW) + (2.0 / NormalizedRect.Scale);
                        Fraction(burned.X, w).Should().BeLessThanOrEqualTo(Fraction(drawn.X, drawnW) + 1e-12);
                        Fraction(burned.Y, h).Should().BeLessThanOrEqualTo(Fraction(drawn.Y, drawnH) + 1e-12);
                        Fraction(burned.X + burned.Width, w).Should().BeGreaterThanOrEqualTo(Fraction(drawn.X + drawn.Width, drawnW) - 1e-12);
                        Fraction(burned.Y + burned.Height, h).Should().BeGreaterThanOrEqualTo(Fraction(drawn.Y + drawn.Height, drawnH) - 1e-12);
                        Fraction(burned.X, w).Should().BeGreaterThan(Fraction(drawn.X, drawnW) - slack);
                        Fraction(burned.X + burned.Width, w).Should().BeLessThan(Fraction(drawn.X + drawn.Width, drawnW) + slack);
                        burned.X.Should().BeGreaterThanOrEqualTo(0);
                        (burned.X + burned.Width).Should().BeLessThanOrEqualTo(w);
                        (burned.Y + burned.Height).Should().BeLessThanOrEqualTo(h);
                    }
                }
            }
        }
    }

    [Fact]
    public void The_stored_rectangle_does_not_depend_on_later_renderings_and_a_full_page_stays_full()
    {
        var stored = new NormalizedRect(123_457, 654_321, 200_001, 33_333);
        foreach (var dpi in Dpis)
        {
            var (w, h) = Raster(8.5, 11, dpi);
            var burned = stored.ToPixels(w, h);
            burned.X.Should().Be((int)Math.Floor(123_457 * (double)w / NormalizedRect.Scale));
            (burned.X + burned.Width).Should().Be((int)Math.Ceiling(323_458 * (double)w / NormalizedRect.Scale));
            NormalizedRect.FullPage.ToPixels(w, h).Should().Be(new PixelRect(0, 0, w, h));
        }

        NormalizedRect.FromPixels(new PixelRect(0, 0, 1275, 1650), 1275, 1650).Should().Be(NormalizedRect.FullPage);
    }

    [Fact]
    public void Rectangles_outside_the_page_or_empty_are_not_within_it()
    {
        new NormalizedRect(0, 0, 1_000_000, 1_000_000).IsWithinPage.Should().BeTrue();
        new NormalizedRect(1, 0, 1_000_000, 10).IsWithinPage.Should().BeFalse();
        new NormalizedRect(-1, 0, 10, 10).IsWithinPage.Should().BeFalse();
        new NormalizedRect(0, 0, 0, 10).IsWithinPage.Should().BeFalse();
        new NormalizedRect(0, 999_999, 10, 2).IsWithinPage.Should().BeFalse();
        new NormalizedRect(int.MaxValue, 0, int.MaxValue, 1).IsWithinPage.Should().BeFalse();
    }

    [Fact]
    public void The_minimum_size_is_two_device_pixels_at_300_dpi()
    {
        // Letter: 612 × 792 pt = 2550 × 3300 px at 300 DPI.
        RedactionGeometry.MinimumSize(612, 792).Should().Be((785, 607));
        var (w, h) = RedactionGeometry.MinimumSize(612, 792);
        new NormalizedRect(0, 0, w, h).ToPixels(2550, 3300).Should().Match<PixelRect>(p => p.Width >= 2 && p.Height >= 2);
        RedactionGeometry.MinimumSize(0, 0).Should().Be((1, 1));
    }

    private static (int Width, int Height) Raster(double widthIn, double heightIn, int dpi) =>
        ((int)Math.Round(widthIn * dpi), (int)Math.Round(heightIn * dpi));

    private static double Fraction(int pixels, int size) => pixels / (double)size;

    private static bool Contains(PixelRect outer, PixelRect inner) =>
        outer.X <= inner.X && outer.Y <= inner.Y && outer.X + outer.Width >= inner.X + inner.Width && outer.Y + outer.Height >= inner.Y + inner.Height;
}
