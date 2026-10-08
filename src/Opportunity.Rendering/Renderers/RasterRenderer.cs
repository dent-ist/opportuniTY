using System.Reflection;
using System.Runtime.CompilerServices;

using BitMiracle.LibTiff.Classic;

using SkiaSharp;

namespace Opportunity.Rendering.Renderers;

/// <summary>
/// The in-process review renderer (E11-T02): PDFs through PDFium, page images through LibTiff.NET and Skia, PNG output
/// through Skia. The source type is sniffed from its first bytes. E11-T03 wraps an <see cref="IRenderer"/> in a
/// sandboxed process; this class is what runs inside it.
/// </summary>
public sealed class RasterRenderer : IRenderer
{
    public const string RendererName = "opportunity-raster";

    /// <summary>Bump when the pipeline's output for the same input and settings changes.</summary>
    public const int PipelineVersion = 1;

    private readonly RenderSettings _settings;

    public RasterRenderer(RenderSettings? settings = null)
    {
        _settings = settings ?? new RenderSettings();
        Identity = new RendererIdentity(RendererName, VersionString(), _settings.Hash());
    }

    public RendererIdentity Identity { get; }

    public RenderSettings Settings => _settings;

    public async IAsyncEnumerable<RenderedPage> RenderAsync(RenderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var page in Render(request, cancellationToken))
        {
            yield return page;
        }
    }

    /// <summary>The synchronous form, for the sandbox process (which renders on its main thread only).</summary>
    public IEnumerable<RenderedPage> Render(RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsPdf(request.InputPath)
            ? PdfRenderer.Render(request, _settings, cancellationToken)
            : ImageRenderer.Render(request, _settings, cancellationToken);
    }

    private static bool IsPdf(string path)
    {
        Span<byte> head = stackalloc byte[5];
        using var file = File.OpenRead(path);
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return read == head.Length && head.SequenceEqual("%PDF-"u8);
    }

    private static string VersionString()
    {
        var libtiff = typeof(Tiff).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Tiff).Assembly.GetName().Version?.ToString() ?? "unknown";
        return $"{PipelineVersion};pdfium {PdfiumNative.Build};skia {SkiaSharpVersion.Native};libtiff.net {libtiff.Split('+')[0]}";
    }
}
