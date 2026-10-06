using Opportunity.Core.Pages;

using SkiaSharp;

namespace Opportunity.Rendering.Renderers;

/// <summary>
/// Renders PDF pages with PDFium, one page at a time: the document is opened from the local file (PDFium reads it on
/// demand, so a large PDF is never loaded whole), each page is rasterized at <see cref="RenderSettings.ReviewDpi"/>
/// (lower for pages beyond the review bounds) with its annotations' appearances, and the raster is released before the
/// next page. Geometry is that of the upright page (its <c>/Rotate</c> applied), which is also the raster's orientation,
/// so the page row's rotation is 0.
/// </summary>
internal static class PdfRenderer
{
    public static IEnumerable<RenderedPage> Render(RenderRequest request, RenderSettings settings, CancellationToken cancellationToken)
    {
        PdfiumNative.EnsureInitialized();
        nint document;
        int count;
        lock (PdfiumNative.Sync)
        {
            document = PdfiumNative.FPDF_LoadDocument(request.InputPath, null);
            if (document == 0)
            {
                var error = (int)PdfiumNative.FPDF_GetLastError().Value;
                throw error is PdfiumNative.ErrorPassword or PdfiumNative.ErrorSecurity
                    ? new RenderException(RenderErrorCodes.Encrypted, "The PDF is encrypted with a password or an unsupported security handler.")
                    : new RenderException(RenderErrorCodes.Unreadable, "The PDF cannot be opened.");
            }

            count = PdfiumNative.FPDF_GetPageCount(document);
        }

        try
        {
            if (count <= 0)
            {
                throw new RenderException(RenderErrorCodes.Unreadable, "The PDF has no pages.");
            }

            if (count > settings.MaxPages)
            {
                throw new RenderException(RenderErrorCodes.TooManyPages, $"The PDF has more than {settings.MaxPages} pages.");
            }

            foreach (var index in request.Pages ?? Enumerable.Range(0, count).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return index < 0 || index >= count
                    ? new RenderedPage(index, 1, 1, PageColorMode.Gray, null, null, RenderErrorCodes.PageFailed)
                    : RenderPage(document, index, request, settings);
            }
        }
        finally
        {
            lock (PdfiumNative.Sync)
            {
                PdfiumNative.FPDF_CloseDocument(document);
            }
        }
    }

    private static RenderedPage RenderPage(nint document, int index, RenderRequest request, RenderSettings settings)
    {
        SKBitmap? bitmap = null;
        decimal widthPt, heightPt;
        lock (PdfiumNative.Sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(document, index);
            if (page == 0)
            {
                return new RenderedPage(index, 1, 1, PageColorMode.Gray, null, null, RenderErrorCodes.PageFailed);
            }

            try
            {
                double w = PdfiumNative.FPDF_GetPageWidthF(page), h = PdfiumNative.FPDF_GetPageHeightF(page);
                if (!(w is >= 1 and <= 999_999) || !(h is >= 1 and <= 999_999))
                {
                    return new RenderedPage(index, 1, 1, PageColorMode.Gray, null, null, RenderErrorCodes.PageFailed);
                }

                widthPt = Math.Round((decimal)w, 2);
                heightPt = Math.Round((decimal)h, 2);
                var (px, py, _) = RasterWriter.ReviewSize(w * settings.ReviewDpi / 72d, h * settings.ReviewDpi / 72d, settings.ReviewDpi, settings);
                var native = PdfiumNative.FPDFBitmap_Create(px, py, 0);
                if (native == 0)
                {
                    return new RenderedPage(index, widthPt, heightPt, PageColorMode.Gray, null, null, RenderErrorCodes.PageTooLarge);
                }

                try
                {
                    _ = PdfiumNative.FPDFBitmap_FillRect(native, 0, 0, px, py, 0xFFFFFFFF);
                    PdfiumNative.FPDF_RenderPageBitmap(native, page, 0, 0, px, py, 0, PdfiumNative.FlagAnnotations);
                    bitmap = Copy(native, px, py);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(native);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        try
        {
            var color = RasterWriter.Normalize(ref bitmap);
            var (review, thumbnail, error) = RasterWriter.Write(
                bitmap, 72d * bitmap.Width / (double)widthPt, (double)widthPt, (double)heightPt, request.Review, request.OutputDirectory, index, settings);
            return new RenderedPage(index, widthPt, heightPt, color, review, thumbnail, error);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Copies PDFium's BGRx buffer into an opaque BGRA Skia bitmap, row by row (strides may differ).</summary>
    private static SKBitmap Copy(nint native, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var stride = PdfiumNative.FPDFBitmap_GetStride(native);
        var buffer = PdfiumNative.FPDFBitmap_GetBuffer(native);
        var target = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        unsafe
        {
            for (var y = 0; y < height; y++)
            {
                var source = new ReadOnlySpan<byte>((byte*)buffer + ((long)y * stride), width * 4);
                var row = target.Slice(y * rowBytes, width * 4);
                source.CopyTo(row);
                for (var x = 3; x < row.Length; x += 4)
                {
                    row[x] = 0xFF;
                }
            }
        }

        return bitmap;
    }
}
