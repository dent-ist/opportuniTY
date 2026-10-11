using System.Globalization;
using System.Text;

namespace Opportunity.Application.ListExports;

/// <summary>
/// The document list export file (#181): RFC 4180 CSV, UTF-8 with a byte-order mark (so spreadsheet programs read it as
/// UTF-8), a header row with the columns' names and one row per exported document. The file is the header followed by
/// the parts the export's chunks wrote. Cells a spreadsheet would run as a formula get a leading apostrophe, as in the
/// other CSV exports; negative numbers stay numbers.
/// </summary>
public static class ListExportCsv
{
    public const string ContentType = "text/csv; charset=utf-8";

    /// <summary>The byte-order mark and the header row.</summary>
    public static byte[] Header(IReadOnlyList<ListExportColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var text = new StringBuilder();
        AppendRow(text, [.. columns.Select(c => c.Header)]);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>Appends one row (CRLF-terminated).</summary>
    public static void AppendRow(StringBuilder text, IReadOnlyList<string?> cells)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(cells);
        text.AppendJoin(',', cells.Select(Cell)).Append("\r\n");
    }

    /// <summary>"export-list-20261011-104200-1a2b3c4d.csv".</summary>
    public static string FileName(Guid exportId, DateTimeOffset createdAt) =>
        "export-list-" + createdAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + exportId.ToString("N")[..8] + ".csv";

    /// <summary>One cell: neutralized when a spreadsheet would treat it as a formula, quoted when it holds a separator.</summary>
    public static string Cell(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && (v[0] is '=' or '+' or '@' or '\t' or '\r' || (v[0] == '-' && (v.Length == 1 || !char.IsDigit(v[1])))))
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
