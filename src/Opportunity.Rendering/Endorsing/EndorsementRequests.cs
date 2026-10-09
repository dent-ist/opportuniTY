using Opportunity.Core.Pages;
using Opportunity.Core.Productions;

namespace Opportunity.Rendering.Endorsing;

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
public sealed record EndorseRequest(
    string? InputPath,
    int Frame,
    string OutputDirectory,
    PageImageFormat Format,
    EndorsementLayout Layout,
    int? Dpi = null,
    int BlankWidthPx = 0,
    int BlankHeightPx = 0);

/// <summary>An endorsed page: the encoded image bytes and what they hold.</summary>
public sealed record EndorsedImage(byte[] Content, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format, PageColorMode ColorMode);
