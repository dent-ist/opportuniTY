using System.Runtime.InteropServices;

namespace Opportunity.Rendering.Renderers;

/// <summary>
/// The few PDFium entry points the review renderer needs (fpdfview.h). PDFium is not thread-safe: every call goes
/// through <see cref="Sync"/>. No form-fill environment is ever created, so document JavaScript and XFA never run.
/// The native library comes from the bblanchon.PDFium.* packages (PDFium: BSD-3-Clause; binaries: Apache-2.0).
/// </summary>
internal static partial class PdfiumNative
{
    /// <summary>The PDFium build of the referenced bblanchon.PDFium.* packages (Directory.Packages.props); part of the renderer version.</summary>
    public const string Build = "157.0.8086";

    private const string Library = "pdfium";

    public const int FlagAnnotations = 0x01;

    public const int ErrorFile = 2;
    public const int ErrorFormat = 3;
    public const int ErrorPassword = 4;
    public const int ErrorSecurity = 5;

    public static readonly Lock Sync = new();

    private static bool _initialized;

    /// <summary>Initializes the library once per process (under <see cref="Sync"/>).</summary>
    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                FPDF_InitLibrary();
                _initialized = true;
            }
        }
    }

    [LibraryImport(Library)]
    private static partial void FPDF_InitLibrary();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadDocument(string filePath, string? password);

    [LibraryImport(Library)]
    public static partial CULong FPDF_GetLastError();

    [LibraryImport(Library)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    public static partial void FPDF_ClosePage(nint page);

    /// <summary>Width of the page as displayed (its /Rotate applied), in points.</summary>
    [LibraryImport(Library)]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Library)]
    public static partial float FPDF_GetPageHeightF(nint page);

    /// <summary>A BGRx bitmap (alpha 0) or BGRA bitmap (alpha 1); zero when it cannot be allocated.</summary>
    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    public static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Library)]
    public static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    public static partial void FPDF_RenderPageBitmap(
        nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
}
