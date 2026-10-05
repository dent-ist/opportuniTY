using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

using Opportunity.Core.SearchTermReports;

namespace Opportunity.Application.Search.TermReports;

/// <summary>
/// The CSV and XLSX forms of a completed search term report (E07-T10, Q-30: exchanged in meet-and-confer). Both hold the
/// same rows: the report's provenance (scope, snapshot, search generation, whether the index was current, executor and
/// time), one row per term and the totals. The output is a pure function of the stored report, so exporting the same
/// run twice gives identical bytes. The XLSX is a minimal SpreadsheetML package written here (no third-party library):
/// one worksheet with inline strings, which a spreadsheet never evaluates as formulas.
/// </summary>
public static class SearchTermReportExport
{
    public const string CsvContentType = "text/csv; charset=utf-8";
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static readonly IReadOnlyList<string> TermColumns =
    [
        "Term", "Expression", "Documents with hits", "Documents with hits, including family", "Unique hits", "Unique hits, including family", "Error",
    ];

    private static readonly DateTimeOffset ZipTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The report as rows of cells (a string, a number, or null for an empty cell).</summary>
    public static IReadOnlyList<IReadOnlyList<object?>> Rows(SearchTermReportRecord report, IReadOnlyList<SearchTermRecord> terms)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(terms);
        var rows = new List<IReadOnlyList<object?>>
        {
            Row("Search Terms Report", report.Name),
            Row("Scope", report.ScopeKind switch
            {
                SearchTermReportScopeKind.SavedSearch => "Saved search: " + (report.ScopeName ?? report.ScopeId?.ToString()),
                SearchTermReportScopeKind.Snapshot => "Snapshot: " + (report.ScopeName ?? report.ScopeId?.ToString()),
                _ => "Workspace",
            }),
            Row("Snapshot ID", report.SnapshotId?.ToString("D")),
            Row("Search generation", report.SearchGeneration),
            Row("Index current at execution", report.IndexCurrent switch
            {
                true => "Yes",
                false => "No: changes were still being made searchable while the report ran; hits may not reflect them.",
                null => null,
            }),
            Row("Executed by", report.ExecutedByDisplay),
            Row("Executed at (UTC)", Time(report.ExecutedAt)),
            Row("Completed at (UTC)", Time(report.CompletedAt)),
            Row("Counts", "Documents the executor could not see (restricted or walled) are not counted. Family = the scope documents of a hit document's family."),
            Row(),
            TermColumns.Cast<object?>().ToArray(),
        };
        foreach (var term in terms.OrderBy(t => t.TermNo))
        {
            rows.Add(Row(term.Name, term.Expression, term.DocumentsWithHits, term.DocumentsWithHitsIncludingFamily, term.UniqueHits,
                term.UniqueHitsIncludingFamily, term.Error is { } e ? $"{e.Code}: {e.Message}" : null));
        }

        rows.Add(Row());
        rows.Add(Row("Total documents in scope", null, report.DocumentsInScope));
        rows.Add(Row("Documents with hits", null, report.DocumentsWithHits));
        rows.Add(Row("Documents with hits, including family", null, report.DocumentsWithHitsIncludingFamily));
        rows.Add(Row("Documents without hits", null, report.DocumentsWithoutHits));
        return rows;
    }

    /// <summary>RFC 4180 CSV in UTF-8 with a byte-order mark; text a spreadsheet would run as a formula gets a leading apostrophe.</summary>
    public static byte[] Csv(SearchTermReportRecord report, IReadOnlyList<SearchTermRecord> terms)
    {
        var text = new StringBuilder();
        foreach (var row in Rows(report, terms))
        {
            text.AppendJoin(',', row.Select(CsvValue)).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public static byte[] Xlsx(SearchTermReportRecord report, IReadOnlyList<SearchTermRecord> terms)
    {
        var rows = Rows(report, terms);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Add(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Add(zip, "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Search Terms Report" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Add(zip, "xl/styles.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>
                """);
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(rows));
        }

        return buffer.ToArray();
    }

    /// <summary>A download file name from the report name: letters, digits, space, dot, dash and underscore only.</summary>
    public static string FileName(SearchTermReportRecord report, string extension)
    {
        ArgumentNullException.ThrowIfNull(report);
        var safe = new string([.. report.Name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_')]).Trim();
        return (safe.Length == 0 ? "search-terms-report" : safe.Length > 100 ? safe[..100] : safe) + "." + extension;
    }

    private static string Sheet(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">")
            .Append("<cols><col min=\"1\" max=\"1\" width=\"40\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"50\" customWidth=\"1\"/>")
            .Append("<col min=\"3\" max=\"6\" width=\"20\" customWidth=\"1\"/><col min=\"7\" max=\"7\" width=\"50\" customWidth=\"1\"/></cols><sheetData>");
        var headerRow = rows.ToList().FindIndex(r => r.Count > 0 && Equals(r[0], TermColumns[0]) && r.Count == TermColumns.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Count; c++)
            {
                var reference = Column(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                var style = r == headerRow || (c == 0 && r < headerRow) ? " s=\"1\"" : string.Empty;
                switch (rows[r][c])
                {
                    case null:
                        break;
                    case long number:
                        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"{style}><v>{number}</v></c>");
                        break;
                    case var value:
                        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                            .Append(Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty))
                            .Append("</t></is></c>");
                        break;
                }
            }

            xml.Append("</row>");
        }

        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static string Escape(string value)
    {
        // XML 1.0 forbids most control characters even when escaped; they cannot be in names, but expressions are free text.
        var clean = new string([.. value.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ')]);
        return SecurityElement.Escape(clean);
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = ZipTimestamp;
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content.Trim());
        stream.Write(bytes);
    }

    private static string CsvValue(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is long number)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var v = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (v.Length > 0 && (v[0] is '=' or '+' or '@' or '\t' or '\r' || (v[0] == '-' && (v.Length == 1 || !char.IsDigit(v[1])))))
        {
            v = "'" + v;
        }

        return v.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    private static string? Time(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static object?[] Row(params object?[] cells) => cells;
}
