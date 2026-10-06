using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Opportunity.Rendering.Renderers;

/// <summary>
/// Raster settings of the review page set (E11-T02). Review rasters are PNG at <see cref="ReviewDpi"/> for PDFs and at
/// the source resolution for page images, both scaled down to stay within <see cref="MaxReviewSide"/> and
/// <see cref="MaxReviewPixels"/>; thumbnails are PNG with the longer side <see cref="ThumbnailSide"/>. Every raster keeps
/// the page's aspect ratio within <see cref="MaxAspectDeviation"/> (ADR-012 §1.4), so normalized redaction coordinates
/// apply to all of them.
/// </summary>
public sealed record RenderSettings
{
    /// <summary>Rendering resolution of PDF pages.</summary>
    public int ReviewDpi { get; init; } = 150;

    /// <summary>Longest side of a review raster in pixels.</summary>
    public int MaxReviewSide { get; init; } = 4_000;

    /// <summary>Largest review raster (pixels); a larger page is rendered at a lower resolution.</summary>
    public long MaxReviewPixels { get; init; } = 16_000_000;

    /// <summary>Longest side of a thumbnail in pixels.</summary>
    public int ThumbnailSide { get; init; } = 200;

    /// <summary>Largest decoded source frame (pixels) a page image may have; a larger frame fails its page.</summary>
    public long MaxSourcePixels { get; init; } = 50_000_000;

    /// <summary>Pages rendered from one source at most; a longer document fails as a whole.</summary>
    public int MaxPages { get; init; } = 20_000;

    /// <summary>ADR-012 §1.4: a raster whose aspect ratio differs more from the page's is rejected.</summary>
    public const double MaxAspectDeviation = 0.005;

    /// <summary>The canonical form hashed into <see cref="RendererIdentity.SettingsHash"/>.</summary>
    public string Canonical() => string.Create(CultureInfo.InvariantCulture,
        $"review=png;dpi={ReviewDpi};side={MaxReviewSide};pixels={MaxReviewPixels};thumb=png;thumbSide={ThumbnailSide};sourcePixels={MaxSourcePixels};pages={MaxPages}");

    public byte[] Hash() => SHA256.HashData(Encoding.UTF8.GetBytes(Canonical()));
}
