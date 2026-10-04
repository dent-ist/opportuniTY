using System.Globalization;
using System.Text;

using Opportunity.Import.LoadFiles;

namespace Opportunity.Production.Exports;

/// <summary>
/// Writes DAT, OPT and CSV report rows with the conventions the importer reads (E08-T01, ticket review E12-T01): every
/// value qualified, a qualifier inside a value doubled, line breaks inside values written as the profile's newline
/// character (Concordance <c>®</c>) or kept literally inside the qualifier (CSV), rows ending in CRLF. Cells that a
/// spreadsheet would evaluate as a formula (leading <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return)
/// are neutralized with a leading apostrophe.
/// </summary>
public static class LoadFileText
{
    public const string RowEnd = "\r\n";

    /// <summary>A DAT row (header or data) with its CRLF.</summary>
    /// <param name="neutralize">Per value: whether it is text that must be formula-neutralized.</param>
    public static string DatRow(DelimiterProfile profile, IReadOnlyList<string> values, IReadOnlyList<bool>? neutralize = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(values);
        var row = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                row.Append(profile.Column);
            }

            var value = values[i] ?? string.Empty;
            AppendQualified(row, profile, neutralize?[i] == true ? Neutralize(value) : value);
        }

        return row.Append(RowEnd).ToString();
    }

    /// <summary>One Opticon row: <c>ImageKey,Volume,Path,DocBreak,FolderBreak,BoxBreak,PageCount</c>.</summary>
    public static string OptRow(string imageKey, string volume, string path, bool documentBreak, int? pageCount) =>
        string.Join(',', imageKey, volume, path, documentBreak ? "Y" : string.Empty, string.Empty, string.Empty,
            pageCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + RowEnd;

    /// <summary>An RFC 4180 CSV row (reports), formula-neutralized.</summary>
    public static string CsvRow(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join(',', values.Select(v =>
        {
            var value = Neutralize(v ?? string.Empty);
            return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
        })) + RowEnd;
    }

    /// <summary>Prefixes an apostrophe to a value a spreadsheet would read as a formula (CSV injection).</summary>
    public static string Neutralize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }

    /// <summary>Encodes text for a load file (no byte-order mark: the DAT header carries it).</summary>
    public static byte[] Encode(LoadFileEncodingKind encoding, string text) => LoadFileEncodings.Get(encoding).GetBytes(text);

    private static void AppendQualified(StringBuilder row, DelimiterProfile profile, string value)
    {
        if (profile.Quote is { } quote)
        {
            row.Append(quote);
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (profile.Quote is { } q && c == q)
            {
                row.Append(q).Append(q);
            }
            else if (c is '\r' or '\n')
            {
                if (profile.Newline is { } newline)
                {
                    row.Append(newline);
                    if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else
                {
                    row.Append(c);
                }
            }
            else if (profile.Quote is null && c == profile.Column)
            {
                // Unqualified profiles cannot carry the separator; the presets always qualify.
                row.Append(' ');
            }
            else
            {
                row.Append(c);
            }
        }

        if (profile.Quote is { } closing)
        {
            row.Append(closing);
        }
    }
}
