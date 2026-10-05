using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Core.Fields;

namespace Opportunity.Correctness.ShadowLedger;

/// <summary>
/// Canonical comparison form of a coding value on either side, written independently of the product's projection
/// builder: an ordered list of strings. Choices compare as their ids, numbers by value, booleans as <c>true</c>/<c>false</c>,
/// dates as UTC instants; an absent or empty value is the empty list. Overflow slots keep every leaf as a string
/// (ADR-007 R6), which this form absorbs.
/// </summary>
public static class CodingValues
{
    /// <summary>The PostgreSQL value: <c>document_coding_field.value</c>, or the field's choice ids for choice types.</summary>
    public static IReadOnlyList<string> FromPostgres(CodingFieldInfo field, JsonNode? value, IReadOnlyCollection<int>? choiceIds)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Type is FieldType.SingleChoice or FieldType.MultiChoice)
        {
            return choiceIds is null ? [] : [.. choiceIds.Order().Select(id => id.ToString(CultureInfo.InvariantCulture))];
        }

        return Canonical(field, value);
    }

    /// <summary>The indexed value at the candidate's path.</summary>
    public static IReadOnlyList<string> FromIndex(CodingFieldInfo field, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        var values = Canonical(field, value);
        return field.Type is FieldType.SingleChoice or FieldType.MultiChoice ? [.. values.Order(StringComparer.Ordinal)] : values;
    }

    public static bool Equal(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.SequenceEqual(right, StringComparer.Ordinal);

    private static List<string> Canonical(CodingFieldInfo field, JsonNode? value)
    {
        var result = new List<string>();
        if (value is JsonArray array)
        {
            foreach (var item in array)
            {
                if (Leaf(field, item) is { } leaf)
                {
                    result.Add(leaf);
                }
            }
        }
        else if (Leaf(field, value) is { } leaf)
        {
            result.Add(leaf);
        }

        return result;
    }

    private static string? Leaf(CodingFieldInfo field, JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        var kind = value.GetValueKind();
        var text = kind == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
        return field.Type switch
        {
            FieldType.Boolean => kind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => bool.TryParse(text, out var b) ? (b ? "true" : "false") : text,
            },
            FieldType.Integer or FieldType.Decimal or FieldType.SingleChoice or FieldType.MultiChoice =>
                decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? (number / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)
                    : text,
            FieldType.Date => Instant(text, field.DatePrecision),
            _ => text,
        };
    }

    private static string Instant(string text, DatePrecision? precision)
    {
        if (precision == DatePrecision.Date
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
            ? instant.ToString("O", CultureInfo.InvariantCulture)
            : text;
    }
}
