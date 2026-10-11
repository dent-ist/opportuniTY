using System.Globalization;
using System.Text;

namespace Opportunity.Production.Productions;

/// <summary>
/// A minimal, deterministic text-only PDF 1.4 writer for reports (E12-T07 QC report): US Letter pages of left-aligned
/// lines in the standard Helvetica and Helvetica-Bold fonts (WinAnsiEncoding, nothing embedded), long lines wrapped at
/// word boundaries, a page footer, and no dates, identifiers or metadata, so equal input gives equal bytes. Characters
/// outside Windows-1252 print as <c>?</c>.
/// </summary>
public sealed class PdfTextDocument
{
    private const float PageWidth = 612f;
    private const float PageHeight = 792f;
    private const float Margin = 54f;
    private const float FooterSize = 8f;

    private readonly List<Line> _lines = [];

    public string Footer { get; init; } = string.Empty;

    /// <summary>Adds text (wrapped to the page width) in <paramref name="size"/> points.</summary>
    public void Add(string text, float size = 9f, bool bold = false, float indent = 0f)
    {
        ArgumentNullException.ThrowIfNull(text);
        var width = PageWidth - (2 * Margin) - indent;
        // Helvetica averages about half an em per character; wrapping by count keeps the writer free of font metrics.
        var perLine = Math.Max(20, (int)(width / (size * (bold ? 0.56f : 0.52f))));
        foreach (var paragraph in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            foreach (var piece in Wrap(paragraph, perLine))
            {
                _lines.Add(new Line(piece, size, bold, indent));
            }
        }
    }

    /// <summary>Adds vertical space.</summary>
    public void Space(float points = 6f) => _lines.Add(new Line(null, points, false, 0));

    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var pages = Paginate();
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Array.Empty<byte>(),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
        };
        var kids = new List<int>();
        for (var p = 0; p < pages.Count; p++)
        {
            var content = PageContent(pages[p], p + 1, pages.Count);
            objects.Add([.. Ascii(string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n")), .. content, .. Ascii("\nendstream")]);
            var contentId = objects.Count;
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>")));
            kids.Add(objects.Count);
        }

        objects[1] = Ascii("<< /Type /Pages /Kids [" + string.Join(' ', kids.Select(k => k.ToString(CultureInfo.InvariantCulture) + " 0 R"))
            + string.Create(CultureInfo.InvariantCulture, $"] /Count {kids.Count} >>"));

        using var buffer = new MemoryStream();
        buffer.Write(Ascii("%PDF-1.4\n"));
        buffer.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = buffer.Position;
            buffer.Write(Ascii(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n")));
            buffer.Write(objects[i]);
            buffer.Write(Ascii("\nendobj\n"));
        }

        var xref = buffer.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        buffer.Write(Ascii(table.ToString()));
        buffer.Position = 0;
        buffer.CopyTo(output);
    }

    private List<List<(Line Line, float Y)>> Paginate()
    {
        var pages = new List<List<(Line, float)>>();
        var current = new List<(Line, float)>();
        var y = PageHeight - Margin;
        foreach (var line in _lines)
        {
            var height = line.Text is null ? line.Size : line.Size * 1.35f;
            if (y - height < Margin + (FooterSize * 2) && current.Count > 0)
            {
                pages.Add(current);
                current = [];
                y = PageHeight - Margin;
            }

            y -= height;
            if (line.Text is not null)
            {
                current.Add((line, y));
            }
        }

        pages.Add(current);
        return pages;
    }

    private byte[] PageContent(List<(Line Line, float Y)> lines, int page, int pages)
    {
        var content = new List<byte>();
        void Text(string font, float size, float x, float y, string text)
        {
            content.AddRange(Ascii(string.Create(CultureInfo.InvariantCulture, $"BT /{font} {size:0.##} Tf {x:0.##} {y:0.##} Td (")));
            content.AddRange(Escape(text));
            content.AddRange(Ascii(") Tj ET\n"));
        }

        foreach (var (line, y) in lines)
        {
            Text(line.Bold ? "F2" : "F1", line.Size, Margin + line.Indent, y, line.Text!);
        }

        var footer = (Footer.Length > 0 ? Footer + "   " : string.Empty) + string.Create(CultureInfo.InvariantCulture, $"Page {page} of {pages}");
        Text("F1", FooterSize, Margin, Margin - FooterSize, footer);
        return [.. content];
    }

    private static IEnumerable<string> Wrap(string text, int perLine)
    {
        if (text.Length <= perLine)
        {
            yield return text;
            yield break;
        }

        var rest = text;
        while (rest.Length > perLine)
        {
            var cut = rest.LastIndexOf(' ', perLine);
            if (cut <= 0)
            {
                cut = perLine;
            }

            yield return rest[..cut].TrimEnd();
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    /// <summary>A PDF literal string body in WinAnsiEncoding: <c>( ) \</c> escaped, control and unmapped characters as octal or <c>?</c>.</summary>
    private static List<byte> Escape(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var c in text)
        {
            var b = WinAnsi(c);
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                bytes.Add((byte)'\\');
                bytes.Add(b);
            }
            else if (b < 0x20 || b >= 0x7F)
            {
                bytes.AddRange(Ascii("\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
            }
            else
            {
                bytes.Add(b);
            }
        }

        return bytes;
    }

    private static byte WinAnsi(char c) => c switch
    {
        < ' ' => (byte)' ',
        < '\u007F' => (byte)c,
        >= ' ' and <= 'ÿ' => (byte)c,
        '€' => 0x80,
        '…' => 0x85,
        '‘' => 0x91,
        '’' => 0x92,
        '“' => 0x93,
        '”' => 0x94,
        '•' => 0x95,
        '–' => 0x96,
        '—' => 0x97,
        _ => (byte)'?',
    };

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private sealed record Line(string? Text, float Size, bool Bold, float Indent);
}
