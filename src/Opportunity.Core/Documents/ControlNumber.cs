using System.Text;

namespace Opportunity.Core.Documents;

/// <summary>ControlNumber normalization and natural sort key (ADR-009 R2, R5).</summary>
public static class ControlNumber
{
    public const int MaxLength = 255;

    /// <summary>Digit runs shorter than this are left-padded with zeros in the sort key; longer runs are kept.</summary>
    public const int SortKeyDigitWidth = 20;

    /// <summary>
    /// Prepends the optional import prefix, applies NFC, trims (including U+00A0 and U+FEFF), collapses internal
    /// whitespace to one space and upper-cases invariantly unless the workspace is case-sensitive.
    /// </summary>
    /// <remarks>
    /// Under <c>InvariantGlobalization</c> (no ICU, as the hosts run) <see cref="string.Normalize()"/> leaves
    /// non-ASCII text unchanged, so the document repository applies NFC again in PostgreSQL on insert.
    /// </remarks>
    /// <exception cref="ArgumentException">The result is empty, longer than 255 characters or has control characters.</exception>
    public static string Normalize(string raw, bool caseSensitive, string? prefix = null)
    {
        return TryNormalize(raw, caseSensitive, prefix, out var normalized, out var error)
            ? normalized
            : throw new ArgumentException(error, nameof(raw));
    }

    public static bool TryNormalize(
        string raw, bool caseSensitive, string? prefix, out string normalized, out string? error)
    {
        ArgumentNullException.ThrowIfNull(raw);
        normalized = string.Empty;

        var value = (prefix + raw).Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (IsSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(c))
            {
                error = "Control number contains control characters.";
                return false;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        var result = builder.ToString();
        if (!caseSensitive)
        {
            result = result.ToUpperInvariant().Normalize(NormalizationForm.FormC);
        }

        if (result.Length == 0)
        {
            error = "Control number is empty.";
            return false;
        }

        if (result.Length > MaxLength)
        {
            error = $"Control number is longer than {MaxLength} characters.";
            return false;
        }

        normalized = result;
        error = null;
        return true;
    }

    /// <summary>
    /// Natural sort key: every maximal ASCII digit run left-padded with zeros to 20 digits (longer runs kept as is),
    /// compared ordinally, so <c>ABC9 &lt; ABC10 &lt; ABC0011</c>. The database computes the same value in
    /// <c>opportunity.control_number_sort_key</c>; sorts break ties on DocumentId.
    /// </summary>
    public static string SortKey(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var builder = new StringBuilder(normalized.Length + SortKeyDigitWidth);
        var i = 0;
        while (i < normalized.Length)
        {
            var start = i;
            if (char.IsAsciiDigit(normalized[i]))
            {
                while (i < normalized.Length && char.IsAsciiDigit(normalized[i]))
                {
                    i++;
                }

                var run = normalized.AsSpan(start, i - start);
                if (run.Length < SortKeyDigitWidth)
                {
                    builder.Append('0', SortKeyDigitWidth - run.Length);
                }

                builder.Append(run);
            }
            else
            {
                builder.Append(normalized[i]);
                i++;
            }
        }

        return builder.ToString();
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c == '\uFEFF';
}
