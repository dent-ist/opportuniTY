using System.Text;

namespace Opportunity.Application.Storage;

/// <summary>Builds the forced <c>attachment</c> disposition for downloads (ADR-011 §5.3, RFC 6266 / RFC 8187).</summary>
public static class ContentDispositionHeader
{
    public const string OctetStream = "application/octet-stream";
    public const string NoStore = "private, no-store";

    private const int MaxFileNameLength = 200;
    private const string Fallback = "download";

    /// <summary><c>attachment; filename="ascii-fallback"; filename*=UTF-8''percent-encoded</c>.</summary>
    public static string Attachment(string? fileName)
    {
        var name = Sanitize(fileName);
        var ascii = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            ascii.Append(c is >= ' ' and < (char)127 and not '"' and not '\\' and not '%' ? c : '_');
        }

        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{EncodeRfc8187(name)}";
    }

    /// <summary>Removes control characters, path separators and quoting characters; trims dots and spaces.</summary>
    public static string Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Fallback;
        }

        var sb = new StringBuilder(Math.Min(fileName.Length, MaxFileNameLength));
        foreach (var rune in fileName.EnumerateRunes())
        {
            if (sb.Length >= MaxFileNameLength)
            {
                break;
            }

            if (Rune.IsControl(rune) || rune.Value is '/' or '\\' or '"' or ':' or '*' or '?' or '<' or '>' or '|' or ';'
                || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format)
            {
                sb.Append('_');
                continue;
            }

            sb.Append(rune.ToString());
        }

        var result = sb.ToString().Trim(' ', '.');
        return result.Length == 0 ? Fallback : result;
    }

    private static string EncodeRfc8187(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '!' or '#' or '$' or '&' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }
}
