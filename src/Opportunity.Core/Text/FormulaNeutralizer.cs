namespace Opportunity.Core.Text;

/// <summary>
/// Output neutralization (ADR-015 D15.5, T-48, E08-T09): a cell of a platform-produced CSV, DAT or XLSX that a
/// spreadsheet would read as a formula is prefixed with an apostrophe. Leading <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>,
/// tab and CR (and their full-width forms) trigger it; a plain negative number such as <c>-12.5</c> is left as it is,
/// while <c>-2+3+cmd|…</c> is not a plain number and is neutralized. The one rule every writer uses.
/// </summary>
public static class FormulaNeutralizer
{
    /// <summary>Whether a spreadsheet could evaluate the value as a formula.</summary>
    public static bool IsFormulaLike(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        return value[0] switch
        {
            '=' or '+' or '@' or '\t' or '\r' or '＝' or '＋' or '＠' => true,
            '-' or '－' => !IsPlainNegativeNumber(value),
            _ => false,
        };
    }

    /// <summary>The value, prefixed with an apostrophe when it is formula-like; null becomes empty.</summary>
    public static string Neutralize(string? value)
    {
        var v = value ?? string.Empty;
        return IsFormulaLike(v) ? "'" + v : v;
    }

    /// <summary><c>-</c>, one or more ASCII digits and optionally <c>.</c> and more digits, nothing else.</summary>
    private static bool IsPlainNegativeNumber(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] != '-')
        {
            return false;
        }

        var digits = 0;
        var dot = false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (c == '.' && !dot && digits > 0 && i < value.Length - 1)
            {
                dot = true;
            }
            else
            {
                return false;
            }
        }

        return digits > 0;
    }
}
