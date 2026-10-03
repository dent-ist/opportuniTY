using System.Globalization;
using System.Text;

using Opportunity.DataGenerator.Corpus.Model;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>A synthetic native file: its bytes are a pure function of the document content.</summary>
public sealed record NativeFile(string Extension, byte[] Bytes, bool IsPlaceholder);

/// <summary>
/// Builds small but valid native files of the claimed type from a document's content (metadata, content key and
/// the first characters of its extracted text). Copies of the same content therefore get identical bytes, so
/// hashes computed from the written natives reproduce the corpus duplicate groups. Every native embeds the
/// content key, so different contents never collide.
/// <list type="bullet">
/// <item>Valid renditions: txt, csv, html, pdf (one page, Helvetica), docx and xlsx (minimal OOXML packages),
/// zip (stored), jpg and png (blank raster), and emails (<c>msg</c>) as RFC 5322 <c>.eml</c>.</item>
/// <item>Placeholders (<see cref="NativeFile.IsPlaceholder"/>): pptx and any other type — a short text file with
/// the claimed extension that starts with <c>OPPORTUNITY SYNTHETIC PLACEHOLDER NATIVE</c>.</item>
/// </list>
/// </summary>
public static class NativeFactory
{
    public const string PlaceholderSignature = "OPPORTUNITY SYNTHETIC PLACEHOLDER NATIVE";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static string ContentKeyHex(DocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.ContentKey.ToString("x32", CultureInfo.InvariantCulture);
    }

    /// <summary>File extension of the native rendition for a corpus file type.</summary>
    public static string ExtensionFor(string fileType) => fileType == "msg" ? "eml" : fileType;

    public static NativeFile Build(DocumentContent content, string excerpt)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(excerpt);
        string key = ContentKeyHex(content);
        string ext = ExtensionFor(content.FileType);
        byte[]? bytes = content.FileType switch
        {
            "msg" => Eml(content, excerpt, key),
            "txt" => Utf8.GetBytes(excerpt + "\r\n\r\n[content-key " + key + "]\r\n"),
            "csv" => Csv(excerpt, key),
            "html" => Html(content, excerpt, key),
            "pdf" => Pdf(excerpt, key),
            "docx" => Docx(excerpt, key),
            "xlsx" => Xlsx(excerpt, key),
            "zip" => StoredZip.Create([("contents.txt", Utf8.GetBytes("Synthetic container " + key + "\r\nFileName: " + Field(content, FieldCatalog.FileName) + "\r\n"))]),
            "jpg" => PageImages.Jpeg("content-key " + key, 320, 240, 72),
            "png" => PageImages.Png("content-key " + key, 320, 240, 72),
            _ => null,
        };
        return bytes != null
            ? new NativeFile(ext, bytes, false)
            : new NativeFile(ext, Utf8.GetBytes(PlaceholderSignature + "\r\nFileType: " + content.FileType + "\r\nContentKey: " + key
                + "\r\nFileName: " + Field(content, FieldCatalog.FileName) + "\r\n\r\n" + excerpt + "\r\n"), true);
    }

    private static string Field(DocumentContent content, int ordinal) => content.Fields[ordinal] as string ?? "";

    private static IEnumerable<string> Lines(string excerpt) =>
        excerpt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Length > 0);

    private static byte[] Eml(DocumentContent content, string excerpt, string key)
    {
        object?[] f = content.Fields;
        var sb = new StringBuilder();
        void Header(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        string Addresses(object? value) => value switch
        {
            string s => Address(s),
            string[] list => string.Join(",\r\n ", list.Select(Address)),
            _ => "",
        };

        Header("From", Addresses(f[FieldCatalog.From]));
        Header("To", Addresses(f[FieldCatalog.To]));
        Header("Cc", Addresses(f[FieldCatalog.Cc]));
        Header("Subject", HeaderText(f[FieldCatalog.Subject] as string ?? ""));
        if (f[FieldCatalog.DateSent] is DateTimeOffset sent)
        {
            string offset = (sent.Offset < TimeSpan.Zero ? "-" : "+") + sent.Offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);
            Header("Date", sent.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + offset);
        }

        Header("Message-ID", Sanitize(f[FieldCatalog.MessageId] as string ?? ""));
        Header("In-Reply-To", Sanitize(f[FieldCatalog.InReplyTo] as string ?? ""));
        Header("X-Opportunity-Content-Key", key);
        Header("MIME-Version", "1.0");
        Header("Content-Type", "text/plain; charset=utf-8");
        Header("Content-Transfer-Encoding", "base64");
        sb.Append("\r\n");
        string body = Convert.ToBase64String(Utf8.GetBytes(excerpt));
        for (int i = 0; i < body.Length; i += 76)
        {
            sb.Append(body, i, Math.Min(76, body.Length - i)).Append("\r\n");
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Address(string formatted)
    {
        int lt = formatted.LastIndexOf('<');
        if (lt <= 0)
        {
            return HeaderText(formatted);
        }

        string name = Sanitize(formatted[..lt]).Trim();
        string address = Sanitize(formatted[lt..]);
        string display = IsAscii(name) ? "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : EncodedWord(name);
        return display + " " + address;
    }

    private static string HeaderText(string value)
    {
        value = Sanitize(value);
        return IsAscii(value) ? value : EncodedWord(value);
    }

    private static string EncodedWord(string value) => "=?UTF-8?B?" + Convert.ToBase64String(Utf8.GetBytes(value)) + "?=";

    private static bool IsAscii(string value) => value.All(c => c < 0x80);

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }

    private static byte[] Csv(string excerpt, string key)
    {
        var sb = new StringBuilder("Row,Text\r\n");
        int row = 0;
        foreach (string line in Lines(excerpt))
        {
            sb.Append(++row).Append(",\"").Append(line.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"\r\n");
        }

        sb.Append("#,\"content-key ").Append(key).Append("\"\r\n");
        return Utf8.GetBytes(sb.ToString());
    }

    private static byte[] Html(DocumentContent content, string excerpt, string key)
    {
        string title = content.Fields[FieldCatalog.Title] as string ?? Field(content, FieldCatalog.FileName);
        string html = "<!DOCTYPE html>\r\n<html><head><meta charset=\"utf-8\"><meta name=\"opportunity-content-key\" content=\"" + key + "\"><title>"
            + Xml(title) + "</title></head>\r\n<body><pre>" + Xml(excerpt) + "</pre></body></html>\r\n";
        return Utf8.GetBytes(html);
    }

    private static byte[] Pdf(string excerpt, string key)
    {
        var text = new StringBuilder("BT /F1 9 Tf 54 750 Td 11 TL\n");
        text.Append('(').Append(PdfString("content-key " + key)).Append(") Tj\n");
        int lines = 0;
        foreach (string line in Lines(excerpt))
        {
            for (int i = 0; i < line.Length && lines < 60; i += 100, lines++)
            {
                text.Append("T* (").Append(PdfString(line.Substring(i, Math.Min(100, line.Length - i)))).Append(") Tj\n");
            }
        }

        text.Append("ET");
        string stream = text.ToString();
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + stream.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + stream + "\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        foreach ((string body, int i) in objects.Select((o, i) => (o, i)))
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{body}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        // Latin-1 keeps one byte per char, so the xref offsets (char positions) are byte offsets.
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string PdfString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '\\' or '(' or ')')
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c is >= ' ' and < '\u007F' ? c : '?');
            }
        }

        return sb.ToString();
    }

    private const string ContentTypesHead = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>";
    private const string RelsHead = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">";
    private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private static byte[] Docx(string excerpt, string key)
    {
        var body = new StringBuilder();
        foreach (string line in Lines(excerpt).Append("content-key " + key))
        {
            body.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(Xml(line)).Append("</w:t></w:r></w:p>");
        }

        return StoredZip.Create(
        [
            ("[Content_Types].xml", Utf8.GetBytes(ContentTypesHead + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")),
            ("_rels/.rels", Utf8.GetBytes(RelsHead + "<Relationship Id=\"rId1\" Type=\"" + OfficeDocument + "\" Target=\"word/document.xml\"/></Relationships>")),
            ("word/document.xml", Utf8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>")),
        ]);
    }

    private static byte[] Xlsx(string excerpt, string key)
    {
        var rows = new StringBuilder();
        int r = 0;
        foreach (string line in Lines(excerpt).Append("content-key " + key))
        {
            r++;
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{r}\"><c r=\"A{r}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Xml(line)).Append("</t></is></c></row>");
        }

        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return StoredZip.Create(
        [
            ("[Content_Types].xml", Utf8.GetBytes(ContentTypesHead
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>")),
            ("_rels/.rels", Utf8.GetBytes(RelsHead + "<Relationship Id=\"rId1\" Type=\"" + OfficeDocument + "\" Target=\"xl/workbook.xml\"/></Relationships>")),
            ("xl/workbook.xml", Utf8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<workbook xmlns=\"" + Main + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", Utf8.GetBytes(RelsHead + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>")),
            ("xl/worksheets/sheet1.xml", Utf8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<worksheet xmlns=\"" + Main + "\"><sheetData>" + rows + "</sheetData></worksheet>")),
        ]);
    }

    /// <summary>XML-escapes text and drops characters XML 1.0 does not allow.</summary>
    private static string Xml(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[++i]);
                    }
                    else if ((c >= ' ' && !char.IsSurrogate(c) && c is not ('￾' or '￿')) || c is '\t' or '\n' or '\r')
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
