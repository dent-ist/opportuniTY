using Opportunity.Core.Pages;

namespace Opportunity.Rendering.Renderers;

/// <summary>
/// Turns one source file (an imported PDF, or a TIFF/JPEG/PNG page image) into browser-displayable page rasters
/// (E11-T02). The contract is file-in, files-out on purpose: the orchestrator downloads the source to a local file and
/// uploads what the renderer writes into <see cref="RenderRequest.OutputDirectory"/>, so the sandbox (E11-T03) can run
/// an implementation in a separate, network-less process with only that directory mounted, without changing callers.
/// Pages are yielded one at a time and their files must be consumed (uploaded, then deleted by the caller) before the
/// next page is requested, which bounds memory and disk to about one page.
/// </summary>
public interface IRenderer
{
    /// <summary>Name, version and settings fingerprint recorded on Rendered page sets (ADR-012 §1.1).</summary>
    RendererIdentity Identity { get; }

    /// <summary>
    /// Renders the requested pages in ascending order. A page that cannot be rendered is yielded with
    /// <see cref="RenderedPage.Error"/> set; a source that cannot be opened at all throws <see cref="RenderException"/>.
    /// </summary>
    IAsyncEnumerable<RenderedPage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Name">Stable renderer name (<c>page_set.renderer_name</c>).</param>
/// <param name="Version">Pipeline and library versions (<c>page_set.renderer_version</c>); a change yields new renditions.</param>
/// <param name="SettingsHash">SHA-256 of the canonical render settings (<c>page_set.render_settings_hash</c>).</param>
public sealed record RendererIdentity(string Name, string Version, byte[] SettingsHash);

/// <param name="InputPath">A local, seekable copy of the source; its type is sniffed from its bytes, never from a name.</param>
/// <param name="OutputDirectory">An existing, empty directory the renderer may write page files into.</param>
/// <param name="Pages">0-based page indexes (PDF pages, or image frames as <c>ImageProbe</c> counts them) to render; null renders all.</param>
/// <param name="Review">Whether to write the review raster (thumbnails are always written).</param>
public sealed record RenderRequest(string InputPath, string OutputDirectory, IReadOnlyList<int>? Pages = null, bool Review = true);

/// <summary>One rendered page: geometry of the upright page and the files written for it.</summary>
/// <param name="Index">0-based page index in the source.</param>
/// <param name="WidthPt">Width of the upright page in 1/72-inch points.</param>
/// <param name="Error">Why the page has no rasters (a stable code such as <c>page-too-large</c>), else null.</param>
public sealed record RenderedPage(
    int Index,
    decimal WidthPt,
    decimal HeightPt,
    PageColorMode ColorMode,
    RasterFile? Review,
    RasterFile? Thumbnail,
    string? Error = null);

/// <summary>A raster the renderer wrote under the output directory.</summary>
public sealed record RasterFile(string Path, int WidthPx, int HeightPx, int Dpi, PageImageFormat Format)
{
    public string ContentType => Format switch
    {
        PageImageFormat.Png => "image/png",
        PageImageFormat.Jpeg => "image/jpeg",
        PageImageFormat.WebP => "image/webp",
        _ => "application/octet-stream",
    };
}

/// <summary>The whole source cannot be rendered; <see cref="Code"/> is a stable, content-free reason.</summary>
public sealed class RenderException : Exception
{
    public RenderException()
        : this(RenderErrorCodes.Unreadable, "The source cannot be rendered.")
    {
    }

    public RenderException(string message)
        : this(RenderErrorCodes.Unreadable, message)
    {
    }

    public RenderException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = RenderErrorCodes.Unreadable;
    }

    public RenderException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Stable reason codes of render failures (job item results, logs); they never contain document content.</summary>
public static class RenderErrorCodes
{
    public const string Unsupported = "render-unsupported-format";
    public const string Unreadable = "render-unreadable";
    public const string Encrypted = "render-encrypted";
    public const string TooManyPages = "render-too-many-pages";
    public const string PageTooLarge = "render-page-too-large";
    public const string PageFailed = "render-page-failed";
    public const string AspectMismatch = "render-aspect-mismatch";
    public const string SourceMissing = "render-source-missing";
    public const string SourceIntegrity = "render-source-integrity";
}
