using System.Globalization;
using System.Security.Cryptography;

using Opportunity.Application.Exports;
using Opportunity.Application.Productions;
using Opportunity.Production.Exports;

namespace Opportunity.Production.Volumes;

/// <summary>
/// The burn-in verification of a production volume (E12-T06, ADR-012 §5, Q-22): its checks on what a run stored for
/// a redacted or withheld member, the rows of its QC report and their summary. The pixel checks of produced pages run
/// in the render sandbox (<see cref="IProducedPageSession.VerifyAsync"/>); the checks here compare registered files.
/// <list type="bullet">
/// <item><b>Image</b> (redacted members, one row per page that carries redactions): the page read back from storage holds
/// every box opaque in its burned colour, none reproduces the source page, and the file holds nothing but the image.</item>
/// <item><b>Text</b> (redacted and withheld members): the text file is the fixed replacement text, never the document's
/// own text.</item>
/// <item><b>Native</b> (redacted and withheld members): no native is in the volume. A native-redaction method cannot be
/// recorded yet (Q-22: native redaction is post-MVP), so every such native is a finding.</item>
/// </list>
/// Rows are deterministic (no times or run identifiers), so every run of a production writes the same report.
/// </summary>
public static class BurnInVerification
{
    public const string CheckImage = "Image";
    public const string CheckText = "Text";
    public const string CheckNative = "Native";
    public const string Passed = "Passed";
    public const string Failed = "Failed";

    /// <summary>The header of the burn-in QC report (one row per check).</summary>
    public static readonly string[] ReportHeader = ["ProdBegBates", "PageBates", "Check", "Outcome", "Boxes", "Findings"];

    /// <summary>One report row: findings as <c>code[#box][:pixels]</c> separated by <c>;</c>.</summary>
    public static string Row(string prodBeg, string? pageBates, string check, int boxes, IReadOnlyList<BurnInFinding> findings) =>
        LoadFileText.CsvRow(
            prodBeg, pageBates ?? string.Empty, check, findings.Count == 0 ? Passed : Failed, boxes.ToString(CultureInfo.InvariantCulture),
            string.Join(';', findings.Select(f => f.Code
                + (f.Box is { } box ? "#" + box.ToString(CultureInfo.InvariantCulture) : string.Empty)
                + (f.Pixels is { } pixels ? ":" + pixels.ToString(CultureInfo.InvariantCulture) : string.Empty))));

    /// <summary>
    /// The text file of a redacted or withheld member must hold exactly <paramref name="expected"/> (the replacement
    /// text in the volume's encoding); no text file at all is fine.
    /// </summary>
    public static IReadOnlyList<BurnInFinding> Text(NewExportFile? text, byte[] expected, ExportSourceObject? original)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (text is null || text.Sha256.AsSpan().SequenceEqual(SHA256.HashData(expected)))
        {
            return [];
        }

        return [new BurnInFinding(original is not null && text.Sha256.AsSpan().SequenceEqual(original.Sha256)
            ? BurnInCodes.OriginalTextShipped
            : BurnInCodes.TextNotReplaced)];
    }

    /// <summary>A redacted or withheld member's native is a finding unless a native-redaction method was recorded for it.</summary>
    public static IReadOnlyList<BurnInFinding> Native(NewExportFile? native, string? recordedMethod = null) =>
        native is not null && string.IsNullOrEmpty(recordedMethod) ? [new BurnInFinding(BurnInCodes.NativeShipped)] : [];

    /// <summary>Totals of a report (rows without the header), as the manifest and the run record them.</summary>
    public static BurnInSummary Summarize(IEnumerable<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var documents = new HashSet<string>(StringComparer.Ordinal);
        var failedDocuments = new HashSet<string>(StringComparer.Ordinal);
        long pages = 0, boxes = 0, failures = 0;
        foreach (var row in rows)
        {
            var cells = ParseRow(row);
            if (cells.Count != ReportHeader.Length)
            {
                throw new FormatException("A burn-in report row has the wrong number of cells.");
            }

            documents.Add(cells[0]);
            if (cells[2] == CheckImage)
            {
                pages++;
                boxes += long.Parse(cells[4], NumberStyles.None, CultureInfo.InvariantCulture);
            }

            if (cells[3] != Passed)
            {
                failures++;
                failedDocuments.Add(cells[0]);
            }
        }

        return new BurnInSummary(documents.Count, pages, boxes, failures, failedDocuments.Count);
    }

    /// <summary>The cells of one RFC 4180 row as <see cref="LoadFileText.CsvRow"/> writes it (without its CRLF).</summary>
    internal static List<string> ParseRow(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}

/// <summary>Totals of a burn-in verification: documents checked, pages and boxes inspected, failed checks and documents.</summary>
public sealed record BurnInSummary(long Documents, long Pages, long Boxes, long Failures, long FailedDocuments)
{
    public bool Passed => Failures == 0;
}
