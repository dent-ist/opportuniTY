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

    /// <summary>Starts one document's work; every request's paths must lie under <paramref name="workDirectory"/>.</summary>
    IProducedPageSession BeginDocument(string workDirectory);
}

/// <summary>One document's page imaging (see <see cref="IProducedPageImager.BeginDocument"/>).</summary>
public interface IProducedPageSession : IAsyncDisposable
{
    /// <exception cref="ProducedPageException">The page cannot be imaged (unreadable, too large, sandbox limit).</exception>
    Task<ProducedPageImage> ImageAsync(ProducedPageRequest request, CancellationToken cancellationToken = default);
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
