namespace Opportunity.Import.LoadFiles;

/// <summary>
/// DAT/CSV delimiter profile as litigation-support tools configure it (Q-26, Q-47, Q-51: vendor-neutral preset IDs;
/// <see cref="Concordance"/> is compatible with exports from common review/processing platforms): column separator, text qualifier,
/// newline-in-value character, multi-value and nested-value separators. The parser uses column, qualifier and
/// newline; the multi-value and nested-value separators travel with the profile for field mapping (E08-T02).
/// A doubled qualifier inside a qualified value is a literal qualifier.
/// </summary>
/// <param name="Name">Preset key or a user label.</param>
/// <param name="Column">Column separator.</param>
/// <param name="Quote">Text qualifier; null for unqualified files (values cannot then contain the column separator).</param>
/// <param name="Newline">Character standing for a line break inside a value (Concordance <c>®</c>); null when values carry literal line breaks inside qualifiers (CSV).</param>
/// <param name="MultiValue">Separator between values of a multi-value field.</param>
/// <param name="NestedValue">Separator between levels of a nested (hierarchical) choice value.</param>
public sealed record DelimiterProfile(string Name, char Column, char? Quote, char? Newline, char MultiValue = ';', char NestedValue = '\\')
{
    /// <summary>Concordance default: column U+0014 (ASCII 020), qualifier <c>þ</c> (254), newline <c>®</c> (174), multi <c>;</c>, nested <c>\</c>.</summary>
    public static readonly DelimiterProfile Concordance = new("concordance", '\u0014', 'þ', '®');

    /// <summary>Concordance with a pilcrow column: column <c>¶</c> (U+00B6), qualifier <c>þ</c>, newline <c>®</c>.</summary>
    public static readonly DelimiterProfile ConcordancePilcrow = new("concordance-pilcrow", '¶', 'þ', '®');

    /// <summary>RFC 4180 CSV: comma, double quote (doubled to escape), literal line breaks inside quoted values.</summary>
    public static readonly DelimiterProfile Csv = new("csv", ',', '"', null);

    public static IReadOnlyList<DelimiterProfile> Presets { get; } = [Concordance, ConcordancePilcrow, Csv];

    public static bool TryGetPreset(string name, out DelimiterProfile profile)
    {
        profile = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return profile != null;
    }

    /// <summary>Profile problems; empty when the profile is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        List<char> structural = [Column];
        if (Quote is { } q)
        {
            structural.Add(q);
        }

        if (Newline is { } n)
        {
            structural.Add(n);
        }

        if (structural.Distinct().Count() != structural.Count)
        {
            errors.Add("column, qualifier and newline characters must differ");
        }

        if (structural.Any(c => c is '\r' or '\n' or '\0' || char.IsSurrogate(c)))
        {
            errors.Add("column, qualifier and newline characters cannot be CR, LF, NUL or surrogates");
        }

        return errors;
    }

    /// <summary>Errors for delimiter characters that cannot be represented in <paramref name="encoding"/>.</summary>
    public IReadOnlyList<string> ValidateFor(LoadFileEncodingKind encoding)
    {
        var errors = new List<string>();
        foreach ((string role, char? c) in new[] { ("column", (char?)Column), ("qualifier", Quote), ("newline", Newline) })
        {
            if (c is { } ch && !LoadFileEncodings.CanEncode(encoding, ch))
            {
                errors.Add($"{role} character U+{(int)ch:X4} cannot be represented in {LoadFileEncodings.Name(encoding)}");
            }
        }

        return errors;
    }
}
