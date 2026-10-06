using System.Globalization;
using System.Text;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// Hand-written, deterministic PDFs for render tests (no PDF library): each page shows its label in a standard font
/// and, optionally, a filled color box. Page size and <c>/Rotate</c> are per document. Only synthetic content.
/// </summary>
public static class SyntheticPdf
{
    /// <param name="pages">Page count.</param>
    /// <param name="widthPt">MediaBox width in points (US Letter: 612).</param>
    /// <param name="heightPt">MediaBox height in points (US Letter: 792).</param>
    /// <param name="rotate">The pages' <c>/Rotate</c> (0, 90, 180, 270).</param>
    /// <param name="colorBox">Draw a red box (a color page) instead of a gray one.</param>
    public static byte[] Create(int pages, double widthPt = 612, double heightPt = 792, int rotate = 0, bool colorBox = false, string label = "Synthetic page")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pages);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty, // pages tree, filled below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var kids = new List<int>();
        for (var i = 1; i <= pages; i++)
        {
            var box = colorBox ? "1 0 0 rg" : "0.5 g";
            var content = string.Create(CultureInfo.InvariantCulture,
                $"{box} 72 72 144 72 re f 0 g BT /F1 24 Tf 72 {heightPt - 108:0.##} Td ({Escape(label)} {i}) Tj ET");
            objects.Add(string.Create(CultureInfo.InvariantCulture, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"));
            var contentId = objects.Count;
            objects.Add(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPt:0.##} {heightPt:0.##}] /Rotate {rotate} /Resources << /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R >>"));
            kids.Add(objects.Count);
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => k.ToString(CultureInfo.InvariantCulture) + " 0 R"))}] /Count {pages} >>";

        using var ms = new MemoryStream();
        void Write(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            ms.Write(bytes);
        }

        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n"));
        }

        var xref = ms.Position;
        Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            Write(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
        }

        Write(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return ms.ToArray();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
}
