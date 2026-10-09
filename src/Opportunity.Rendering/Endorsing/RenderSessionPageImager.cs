using Opportunity.Application.Productions;
using Opportunity.Rendering.Renderers;

namespace Opportunity.Rendering.Endorsing;

/// <summary>
/// The production volume writer's page imager (E12-T05) over the render worker's <see cref="IRenderer"/>: each
/// document's pages are decoded, redacted and endorsed in that document's render session, i.e. in its sandboxed child
/// process when the renderer is the <c>SandboxedRenderer</c> (docs/architecture/rendering.md).
/// </summary>
public sealed class RenderSessionPageImager(IRenderer renderer) : IProducedPageImager
{
    private readonly IRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    public string Version => PageEndorser.Version;

    public string VerifierVersion => BurnInVerifier.VersionText;

    public IProducedPageSession BeginDocument(string workDirectory) => new Session(_renderer.BeginDocument(workDirectory));

    /// <summary>The endorser request of a produced page.</summary>
    public static EndorseRequest ToEndorseRequest(ProducedPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new EndorseRequest(
            request.InputPath,
            request.Frame,
            request.OutputDirectory,
            request.Format,
            new EndorsementLayout([.. request.Stamps.Select(s => new EndorsementText(s.Position, s.Text))], request.FontSizePt, request.MarginPt,
                request.ExpandCanvas, request.BodyLines),
            request.Dpi,
            request.BlankWidthPx,
            request.BlankHeightPx,
            [.. request.Redactions.Select(r => new BurnedRedaction(r.Rect, r.Type, r.Label))]);
    }

    private sealed class Session(IRenderSession session) : IProducedPageSession
    {
        public async Task<ProducedPageImage> ImageAsync(ProducedPageRequest request, CancellationToken cancellationToken = default)
        {
            EndorsedImage image;
            try
            {
                image = await session.EndorseAsync(ToEndorseRequest(request), cancellationToken).ConfigureAwait(false);
            }
            catch (RenderLimitException ex)
            {
                throw new ProducedPageException(ex.Code, ex.Message, ex, transient: true);
            }
            catch (RenderException ex)
            {
                throw new ProducedPageException(ex.Code, ex.Message, ex);
            }

            return new ProducedPageImage(image.Content, image.WidthPx, image.HeightPx, image.Dpi, image.Format, image.ColorMode, image.PageTopPx,
                image.PageHeightPx);
        }

        public async Task<ProducedPageVerdict> VerifyAsync(ProducedPageVerification request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            BurnInResult result;
            try
            {
                result = await session.VerifyAsync(new BurnInRequest(
                    request.ProducedPath, request.Format, request.SourcePath, request.SourceFrame, request.PageTopPx,
                    [.. request.Redactions.Select(r => new BurnedRedaction(r.Rect, r.Type))]), cancellationToken).ConfigureAwait(false);
            }
            catch (RenderLimitException ex)
            {
                throw new ProducedPageException(ex.Code, ex.Message, ex, transient: true);
            }
            catch (RenderException ex)
            {
                throw new ProducedPageException(ex.Code, ex.Message, ex);
            }

            return new ProducedPageVerdict(result.WidthPx, result.PageHeightPx, result.BoxesChecked,
                [.. result.Issues.Select(i => new BurnInFinding(i.Code, i.Box, i.Pixels))]);
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
