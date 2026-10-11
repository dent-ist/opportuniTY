using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Opportunity.Contracts.Api;

namespace Opportunity.Production.PrivilegeLogs;

/// <summary>
/// The files of a privilege log version (E13-T03 AC 2): pure functions of the version's frozen metadata and entries, so
/// rendering a version again gives the same bytes. No times, user names or random identifiers go into a file.
/// <list type="bullet">
/// <item>CSV: RFC 4180, UTF-8 with byte-order mark, CRLF; a header row and one row per entry. Text a spreadsheet would
/// run as a formula gets a leading apostrophe (threat model T-48).</item>
/// <item>XLSX: a minimal SpreadsheetML package written here (no third-party library) with inline strings, which a
/// spreadsheet never evaluates: sheet "Privilege Log" (the same rows) and sheet "Log Information" (source, template,
/// privacy handling, exclusion rules with their counts, totals). Zip entries carry a fixed timestamp.</item>
/// </list>
/// </summary>
public static class PrivilegeLogFiles
{
    public const int FormatVersion = 1;
    public const string CsvContentType = "text/csv; charset=utf-8";
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly DateTimeOffset ZipTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>The canonical JSON of the metadata (stored with the version; part of its content SHA-256).</summary>
    public static string SerializeMetadata(PrivilegeLogMetadata metadata) => JsonSerializer.Serialize(metadata, Json);

    public static PrivilegeLogMetadata DeserializeMetadata(string json) =>
        JsonSerializer.Deserialize<PrivilegeLogMetadata>(json, Json) ?? throw new JsonException("Empty privilege log metadata.");

    /// <summary>SHA-256 of the canonical metadata followed by the CSV: identifies a version's content.</summary>
    public static byte[] ContentSha256(string metadataJson, byte[] csv)
    {
        ArgumentNullException.ThrowIfNull(metadataJson);
        ArgumentNullException.ThrowIfNull(csv);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(metadataJson));
        hash.AppendData([(byte)'\n']);
        hash.AppendData(csv);
        return hash.GetHashAndReset();
    }

    public static byte[] Csv(PrivilegeLogMetadata metadata, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.AppendJoin(',', metadata.Columns.Select(CsvValue)).Append("\r\n");
        foreach (var row in rows)
        {
            text.AppendJoin(',', row.Select(CsvValue)).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public static byte[] Xlsx(PrivilegeLogMetadata metadata, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rows);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Add(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Add(zip, "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Privilege Log" sheetId="1" r:id="rId1"/><sheet name="Log Information" sheetId="2" r:id="rId2"/></sheets></workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Add(zip, "xl/styles.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf></cellXfs></styleSheet>
                """);
            Add(zip, "xl/worksheets/sheet1.xml", LogSheet(metadata.Columns, rows));
            Add(zip, "xl/worksheets/sheet2.xml", InformationSheet(Information(metadata)));
        }

        return buffer.ToArray();
    }

    /// <summary>The "Log Information" rows: label and value.</summary>
    public static IReadOnlyList<(string Label, string Value)> Information(PrivilegeLogMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string D(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        var rows = new List<(string, string)>
        {
            ("Privilege Log", metadata.Source == PrivilegeLogSourceKind.Production
                ? $"Production: {metadata.ProductionName} (version {N(metadata.ProductionVersion ?? 1)})"
                : "Frozen set"),
        };
        if (metadata.ProductionId is { } production)
        {
            rows.Add(("Production ID", production.ToString("D")));
        }

        rows.Add(("Snapshot ID", metadata.SnapshotId.ToString("D")));
        if (metadata.ReviewSetSnapshotId is { } scope)
        {
            rows.Add(("Review set snapshot ID", scope.ToString("D")));
        }

        rows.Add(("Template", metadata.TemplateName));
        rows.Add(("Privacy-only redactions", metadata.PrivacyRedactionsIncluded ? "Listed as Redacted (Privacy)" : "Not listed"));
        rows.Add(("Documents listed", N(metadata.Entries)));
        rows.Add(("Withheld", N(metadata.Withheld)));
        rows.Add(("Redacted", N(metadata.Redacted)));
        if (metadata.PrivacyRedactionsIncluded)
        {
            rows.Add(("Redacted (Privacy)", N(metadata.RedactedPrivacy)));
        }

        rows.Add(("Excluded by rules", N(metadata.ExcludedByRules)));
        foreach (var rule in metadata.ExclusionRules)
        {
            var conditions = new List<string>();
            if (rule.OnOrAfter is not null || rule.Before is not null)
            {
                conditions.Add($"{rule.DateField}"
                    + (rule.OnOrAfter is null ? string.Empty : $" on or after {D(rule.OnOrAfter)}")
                    + (rule.Before is null ? string.Empty : $" before {D(rule.Before)}"));
            }

            if (rule.LogCategories.Count > 0)
            {
                conditions.Add("Log Category: " + string.Join(", ", rule.LogCategories));
            }

            if (rule.AttorneysInvolved.Count > 0)
            {
                conditions.Add("Attorneys Involved: " + string.Join(", ", rule.AttorneysInvolved));
            }

            rows.Add(("Exclusion rule: " + rule.Label, string.Join("; ", conditions) + $" ({N(rule.ExcludedDocuments)} documents excluded)"));
        }

        return rows;
    }

    private static string LogSheet(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">")
            .Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>")
            .Append(CultureInfo.InvariantCulture, $"<cols><col min=\"1\" max=\"{Math.Max(1, headers.Count)}\" width=\"24\" customWidth=\"1\"/></cols><sheetData>");
        AppendRow(xml, 1, headers, style: 1);
        var r = 1;
        foreach (var row in rows)
        {
            AppendRow(xml, ++r, row, style: 2);
        }

        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static string InformationSheet(IReadOnlyList<(string Label, string Value)> rows)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">")
            .Append("<cols><col min=\"1\" max=\"1\" width=\"40\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"90\" customWidth=\"1\"/></cols><sheetData>");
        for (var i = 0; i < rows.Count; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{i + 1}\">");
            AppendCell(xml, "A" + (i + 1).ToString(CultureInfo.InvariantCulture), rows[i].Label, 1);
            AppendCell(xml, "B" + (i + 1).ToString(CultureInfo.InvariantCulture), rows[i].Value, 2);
            xml.Append("</row>");
        }

        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static void AppendRow(StringBuilder xml, int r, IReadOnlyList<string> cells, int style)
    {
        var number = r.ToString(CultureInfo.InvariantCulture);
        xml.Append("<row r=\"").Append(number).Append("\">");
        for (var c = 0; c < cells.Count; c++)
        {
            if (!string.IsNullOrEmpty(cells[c]))
            {
                AppendCell(xml, Column(c) + number, cells[c], style);
            }
        }

        xml.Append("</row>");
    }

    private static void AppendCell(StringBuilder xml, string reference, string value, int style) =>
        xml.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(style.ToString(CultureInfo.InvariantCulture))
            .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Escape(value)).Append("</t></is></c>");

    private static string Escape(string value)
    {
        // XML 1.0 forbids most control characters even when escaped.
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
        stream.Write(new UTF8Encoding(false).GetBytes(content.Trim()));
    }

    private static string CsvValue(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && (v[0] is '=' or '+' or '@' or '\t' or '\r' || (v[0] == '-' && (v.Length == 1 || !char.IsDigit(v[1])))))
        {
            v = "'" + v;
        }

        return v.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
