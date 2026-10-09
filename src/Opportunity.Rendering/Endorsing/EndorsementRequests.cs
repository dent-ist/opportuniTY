using Opportunity.Core.Pages;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;

namespace Opportunity.Rendering.Endorsing;

/// <summary>
/// A redaction burned into a produced page (E12-T05, ADR-012 §5): a rectangle in normalized page space (rounded
/// outward to device pixels) drawn as an opaque black box, or as a white box framed in black just outside the
/// rectangle with <see cref="Label"/> printed inside when it fits.
/// </summary>
public sealed record BurnedRedaction(NormalizedRect Rect, RedactionType Type, string? Label = null);

/// <summary>One text stamped on a page.</summary>
public sealed record EndorsementText(EndorsementPosition Position, string Text);

/// <summary>
/// How a produced page is endorsed (E12-T04, from the production specification): the stamps, the font size and margin
/// in points (converted to pixels at the page's resolution), and whether the canvas is expanded by a band above and
/// below the page (the image is never overwritten) or the stamps are drawn over the page on a white box.
/// </summary>
/// <param name="BodyLines">Centered lines for a generated page (slip sheet or placeholder); ignored on a source image.</param>
public sealed record EndorsementLayout(
    IReadOnlyList<EndorsementText> Stamps,
    int FontSizePt = 10,
    int MarginPt = 18,
    bool ExpandCanvas = true,
    IReadOnlyList<string>? BodyLines = null);

/// <summary>
/// One page to endorse: a frame of a page image under the session's work directory (TIFF of any compression, JPEG or
/// PNG; frames numbered as OPT import numbers them), or, with no <see cref="InputPath"/>, a blank page of
/// <see cref="BlankWidthPx"/> × <see cref="BlankHeightPx"/> (a slip sheet or placeholder).
/// </summary>
/// <param name="OutputDirectory">An existing directory under the work directory the endorser writes its one file into.</param>
/// <param name="Format">Output: TIFF CCITT Group 4 (black and white), JPEG or PNG.</param>
/// <param name="Dpi">The page's resolution; null takes the image's own (non-square pixels are made square).</param>
/// <param name="Redactions">Burned into the source page before it is endorsed (E12-T05); ignored on a blank page.</param>
public sealed record EndorseRequest(
    string? InputPath,
    int Frame,
    string OutputDirectory,
    PageImageFormat Format,
    EndorsementLayout Layout,
    int? Dpi = null,
    int BlankWidthPx = 0,
    int BlankHeightPx = 0,
    IReadOnlyList<BurnedRedaction>? Redactions = null);

/// <summary>An endorsed page: the encoded image bytes and what they hold.</summary>
/// <param name="PageTopPx">First image row of the source page (the endorsement band above it is this high).</param>
/// <param name="PageHeightPx">Rows of the source page (it spans the full width).</param>
public sealed record EndorsedImage(
    byte[] Content, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format, PageColorMode ColorMode, int PageTopPx = 0, int PageHeightPx = 0);
