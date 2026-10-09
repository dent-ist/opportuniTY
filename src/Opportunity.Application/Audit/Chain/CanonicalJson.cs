using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Opportunity.Application.Audit.Chain;

/// <summary>
/// RFC 8785 JSON Canonicalization Scheme (JCS): object members sorted by their UTF-16 code units, no whitespace,
/// strings with the minimal escapes, numbers in the ECMAScript <c>Number.prototype.toString</c> form. The audit hash
/// chain (ADR-013 §3.2) hashes envelopes in this form, so the sealer and <c>audit verify</c> agree byte for byte.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Parses <paramref name="json"/> and returns its canonical form.</summary>
    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder(json.Length);
        WriteValue(builder, document.RootElement);
        return builder.ToString();
    }

    public static void WriteValue(StringBuilder builder, JsonElement element)
    {
        ArgumentNullException.ThrowIfNull(builder);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = element.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                builder.Append('{');
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                    {
                        if (members[i].Name == members[i - 1].Name)
                        {
                            throw new FormatException($"Duplicate JSON member '{members[i].Name}' cannot be canonicalized.");
                        }

                        builder.Append(',');
                    }

                    WriteString(builder, members[i].Name);
                    builder.Append(':');
                    WriteValue(builder, members[i].Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteValue(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(element.GetDouble()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new FormatException($"JSON value kind {element.ValueKind} cannot be canonicalized.");
        }
    }

    /// <summary>A JSON string literal with the RFC 8785 §3.2.2.2 escapes.</summary>
    public static void WriteString(StringBuilder builder, string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>ECMAScript number serialization (RFC 8785 §3.2.2.3) of an IEEE 754 double.</summary>
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new FormatException("NaN and infinite numbers are not valid JSON.");
        }

        if (value == 0)
        {
            return "0";
        }

        // Shortest round-trip digits, e.g. "-1.5E-07", "123.45", "1E+21".
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        var exponent = 0;
        var e = text.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var digits = dot >= 0 ? text.Remove(dot, 1) : text;
        var integerDigits = dot >= 0 ? dot : text.Length;
        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0')
        {
            leading++;
        }

        digits = digits[leading..].TrimEnd('0');
        integerDigits -= leading;

        // value = 0.d1d2...dk × 10^n
        var k = digits.Length;
        var n = integerDigits + exponent;
        string result;
        if (k <= n && n <= 21)
        {
            result = digits + new string('0', n - k);
        }
        else if (n is > 0 and <= 21)
        {
            result = digits[..n] + "." + digits[n..];
        }
        else if (n is > -6 and <= 0)
        {
            result = "0." + new string('0', -n) + digits;
        }
        else
        {
            var power = n - 1;
            result = digits[..1] + (k > 1 ? "." + digits[1..] : string.Empty) + "e" + (power < 0 ? "-" : "+")
                + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
        }

        return negative ? "-" + result : result;
    }
}
