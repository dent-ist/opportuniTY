using Opportunity.Core.Pages;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;

namespace Opportunity.Application.Productions;

/// <summary>
/// Images produced pages for a production volume (E12-T05): decodes a page of a stored image, burns its redactions,
/// stamps its endorsements and encodes it, or draws a generated page (slip sheet, placeholder). Implemented by the
/// rendering module over the document's render sandbox (<c>IRenderSession.EndorseAsync</c>), so document content is
/// decoded only there. Deterministic: the same request gives the same bytes.
/// </summary>
public interface IProducedPageImager
{
    /// <summary>Imager, pipeline and library versions; recorded in the volume manifest (Q-08).</summary>
    string Version { get; }

    /// <summary>Version of the burn-in verifier (E12-T06); recorded in the volume manifest with its results.</summary>
    string VerifierVersion { get; }

    /// <summary>Starts one document's work; every request's paths must lie under <paramref name="workDirectory"/>.</summary>
    IProducedPageSession BeginDocument(string workDirectory);
}

/// <summary>One document's page imaging (see <see cref="IProducedPageImager.BeginDocument"/>).</summary>
public interface IProducedPageSession : IAsyncDisposable
{
    /// <exception cref="ProducedPageException">The page cannot be imaged (unreadable, too large, sandbox limit).</exception>
    Task<ProducedPageImage> ImageAsync(ProducedPageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a produced page's burn-in (E12-T06) in the same isolation as <see cref="ImageAsync"/>: decodes the produced
    /// image and the source page and checks that the file holds one plain image, that every redaction box is opaque in
    /// its burned colour and does not reproduce the source, and that the page sits where the imager reported it.
    /// </summary>
    /// <exception cref="ProducedPageException">The verifier itself could not run (transient: a sandbox limit).</exception>
    Task<ProducedPageVerdict> VerifyAsync(ProducedPageVerification request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A produced page to verify (E12-T06): the produced image as read back from storage, the source page it was made from
/// and the redactions that had to be burned into it (normalized page space, so the verifier maps them to pixels of the
/// decoded source itself, with <see cref="RedactionGeometry.BurnedPixels"/>).
/// </summary>
/// <param name="ProducedPath">The produced image under the session's work directory.</param>
/// <param name="PageTopPx">Where the imager reported the source page inside the produced image.</param>
public sealed record ProducedPageVerification(
    string ProducedPath,
    PageImageFormat Format,
    string SourcePath,
    int SourceFrame,
    int PageTopPx,
    IReadOnlyList<VerifiedRedaction> Redactions);

/// <summary>A redaction a produced page must carry.</summary>
public sealed record VerifiedRedaction(NormalizedRect Rect, RedactionType Type);

/// <summary>The burn-in verdict of one produced page; it passed when it has no findings.</summary>
/// <param name="BoxesChecked">Redaction boxes inspected pixel by pixel.</param>
public sealed record ProducedPageVerdict(int WidthPx, int PageHeightPx, int BoxesChecked, IReadOnlyList<BurnInFinding> Findings)
{
    public bool Passed => Findings.Count == 0;
}

/// <summary>One failed check: a <see cref="BurnInCodes"/> code, the 1-based box it concerns and how many pixels failed.</summary>
public sealed record BurnInFinding(string Code, int? Box = null, long? Pixels = null);

/// <summary>Stable, content-free codes of burn-in verification findings (E12-T06).</summary>
public static class BurnInCodes
{
    /// <summary>The produced file is not one plain image of the expected format (extra frames, metadata, trailing data).</summary>
    public const string HiddenData = "HiddenData";

    /// <summary>The produced image cannot be decoded, or is not the size of its source page.</summary>
    public const string ProducedUnreadable = "ProducedUnreadable";

    /// <summary>The source page cannot be decoded, so the box cannot be compared with it.</summary>
    public const string SourceUnreadable = "SourceUnreadable";

    /// <summary>The produced image does not have the source page's width, or the page does not fit where it was reported.</summary>
    public const string PageGeometry = "PageGeometry";

    /// <summary>The source page's content is not where the imager reported it (the boxes would be checked in the wrong place).</summary>
    public const string PageMisaligned = "PageMisaligned";

    /// <summary>Pixels inside a box are not the burned colour (black; white fill or black label).</summary>
    public const string BoxNotOpaque = "BoxNotOpaque";

    /// <summary>Pixels inside a box reproduce the source page's content.</summary>
    public const string BoxShowsSource = "BoxShowsSource";

    /// <summary>A redacted page was produced from another source page than the frozen one, or is missing.</summary>
    public const string PageMissing = "PageMissing";

    /// <summary>The verification could not run for the page (it is treated as failed).</summary>
    public const string NotVerifiable = "NotVerifiable";

    /// <summary>A redacted (or withheld) document's text file is the document's own text.</summary>
    public const string OriginalTextShipped = "OriginalTextShipped";

    /// <summary>A redacted (or withheld) document's text file is not the fixed replacement text.</summary>
    public const string TextNotReplaced = "TextNotReplaced";

    /// <summary>A redacted (or withheld) document's native is in the volume without a recorded native-redaction method.</summary>
    public const string NativeShipped = "NativeShipped";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        HiddenData, ProducedUnreadable, SourceUnreadable, PageGeometry, PageMisaligned, BoxNotOpaque, BoxShowsSource, PageMissing,
        NotVerifiable, OriginalTextShipped, TextNotReplaced, NativeShipped,
    };
}

/// <summary>A text stamped on a produced page.</summary>
public sealed record ProducedStamp(EndorsementPosition Position, string Text);

/// <summary>A redaction burned into a produced page: a rectangle in normalized page space and how it is drawn.</summary>
/// <param name="Label">Printed inside a <see cref="RedactionType.Labelled"/> box; ignored for black boxes.</param>
public sealed record ProducedRedaction(NormalizedRect Rect, RedactionType Type, string? Label);

/// <summary>
/// One produced page: a frame of a local image file (<see cref="InputPath"/>), or with no input a generated page of
/// <see cref="BlankWidthPx"/> × <see cref="BlankHeightPx"/> pixels with centred <see cref="BodyLines"/>.
/// </summary>
/// <param name="OutputDirectory">An existing, empty directory under the work directory for the imager's one file.</param>
/// <param name="Dpi">Resolution of a generated page; a source page keeps its own (null).</param>
public sealed record ProducedPageRequest(
    string? InputPath,
    int Frame,
    string OutputDirectory,
    PageImageFormat Format,
    IReadOnlyList<ProducedStamp> Stamps,
    int FontSizePt,
    int MarginPt,
    bool ExpandCanvas,
    IReadOnlyList<ProducedRedaction> Redactions,
    IReadOnlyList<string>? BodyLines = null,
    int? Dpi = null,
    int BlankWidthPx = 0,
    int BlankHeightPx = 0);

/// <summary>
/// An imaged page: its encoded bytes and where the source page sits inside it (the endorsement bands are added above
/// and below), so a verification can map each burned box to image pixels (E12-T06).
/// </summary>
/// <param name="PageTopPx">First image row of the source page (the height of the top band).</param>
/// <param name="PageHeightPx">Rows of the source page; the page spans the full image width.</param>
public sealed record ProducedPageImage(
    byte[] Content, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format, PageColorMode ColorMode, int PageTopPx, int PageHeightPx);

/// <summary>A page could not be imaged; <see cref="Code"/> is a stable, content-free reason.</summary>
public sealed class ProducedPageException : Exception
{
    public ProducedPageException()
        : this("produced-page-failed", "The page cannot be imaged.")
    {
    }

    public ProducedPageException(string message)
        : this("produced-page-failed", message)
    {
    }

    public ProducedPageException(string message, Exception innerException)
        : this("produced-page-failed", message, innerException)
    {
    }

    /// <param name="transient">The render process hit a limit or crashed: the chunk is retried rather than the page replaced.</param>
    public ProducedPageException(string code, string message, Exception? innerException = null, bool transient = false)
        : base(message, innerException)
    {
        Code = code;
        Transient = transient;
    }

    public string Code { get; } = "produced-page-failed";

    public bool Transient { get; }
}
