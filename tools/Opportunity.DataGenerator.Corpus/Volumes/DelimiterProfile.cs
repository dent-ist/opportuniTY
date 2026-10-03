using System.Text;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// DAT delimiter profile (eDiscovery review §12, E08-T01 presets). Every value is wrapped in <see cref="Quote"/>; a
/// quote character inside a value is escaped by doubling it. When <see cref="Newline"/> is set, line breaks inside
/// values are written as that character (CRLF, CR and LF each become one <see cref="Newline"/>); otherwise they
/// are written literally inside the qualified value (RFC 4180).
/// </summary>
public sealed record DelimiterProfile(string Name, char Column, char Quote, char? Newline, char MultiValue, char NestedValue)
{
    /// <summary>Column U+0014, quote þ (U+00FE), newline ® (U+00AE), multi-value <c>;</c>, nested <c>\</c>.</summary>
    public static readonly DelimiterProfile Concordance = new("concordance", '\u0014', 'þ', '®', ';', '\\');

    /// <summary>Common vendor variant: column ¶ (U+00B6), quote þ, newline ®.</summary>
    public static readonly DelimiterProfile Vendor = new("vendor", '¶', 'þ', '®', ';', '\\');

    /// <summary>RFC 4180 CSV: comma, double quote, literal line breaks inside quoted values.</summary>
    public static readonly DelimiterProfile Csv = new("csv", ',', '"', null, ';', '\\');

    public static IReadOnlyList<DelimiterProfile> Presets { get; } = [Concordance, Vendor, Csv];

    public static DelimiterProfile Preset(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown delimiter preset '{name}' (expected {string.Join(", ", Presets.Select(p => p.Name))}).", nameof(name));

    public IEnumerable<string> Validate()
    {
        char[] all = Newline is { } n ? [Column, Quote, n] : [Column, Quote];
        if (all.Distinct().Count() != all.Length)
        {
            yield return "column, quote and newline delimiters must differ";
        }

        if (Column is '\r' or '\n' || Quote is '\r' or '\n')
        {
            yield return "column and quote delimiters cannot be line breaks";
        }
    }

    /// <summary>Appends <paramref name="value"/> qualified and escaped.</summary>
    public void AppendQualified(StringBuilder row, string value)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(value);
        row.Append(Quote);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == Quote)
            {
                row.Append(Quote).Append(Quote);
            }
            else if (Newline is { } nl && c is '\r' or '\n')
            {
                row.Append(nl);
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

        row.Append(Quote);
    }
}
