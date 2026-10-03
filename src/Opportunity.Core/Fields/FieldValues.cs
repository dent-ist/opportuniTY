using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Opportunity.Core.Fields;

/// <summary>
/// Canonical value representation of ADR-003 §3, for already-typed input (API writes, coding, JSONB validation).
/// Raw import strings go through <see cref="FieldValueCoercer"/> instead. <c>null</c> means "no value": JSON null,
/// empty strings and empty arrays are never stored.
/// </summary>
public static class FieldValues
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string DateTimeMillisFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Validates <paramref name="value"/> against <paramref name="definition"/> and returns its canonical form.
    /// </summary>
    /// <param name="choices">The field's choices (choice types only).</param>
    /// <param name="forAssignment">
    /// True for a new write: inactive choices are rejected. False when validating stored values, which may keep a
    /// choice that was deactivated after it was assigned.
    /// </param>
    public static bool TryCanonicalize(
        FieldDefinition definition,
        JsonNode? value,
        IReadOnlyList<Choice> choices,
        bool forAssignment,
        out JsonNode? canonical,
        out FieldError? error)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(choices);
        canonical = null;
        error = null;
        var key = definition.Key;
        if (value is null)
        {
            return true;
        }

        FieldError Fail(string code, string message) => new(key, code, message);

        switch (definition.Type)
        {
            case FieldType.Text or FieldType.Keyword:
                {
                    var max = definition.Type == FieldType.Text ? FieldLimits.MaxTextLength : FieldLimits.MaxKeywordLength;
                    if (value is JsonArray array)
                    {
                        if (!definition.IsMultiValue)
                        {
                            error = Fail("not-multi-value", $"Field {definition.Name} holds a single value.");
                            return false;
                        }

                        var items = new List<string>(array.Count);
                        foreach (var item in array)
                        {
                            if (!TryString(item, out var s) || !TryCheckText(definition, s, max, Fail, out error))
                            {
                                error ??= Fail("invalid-type", "Expected an array of strings.");
                                return false;
                            }

                            items.Add(s);
                        }

                        if (definition.Type == FieldType.Keyword
                            && items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
                        {
                            error = Fail("duplicate-value", "Keyword values must be unique (case-insensitively).");
                            return false;
                        }

                        canonical = items.Count == 0 ? null : new JsonArray([.. items.Select(i => (JsonNode)JsonValue.Create(i))]);
                        return true;
                    }

                    if (!TryString(value, out var single))
                    {
                        error = Fail("invalid-type", "Expected a string.");
                        return false;
                    }

                    if (!TryCheckText(definition, single, max, Fail, out error))
                    {
                        return false;
                    }

                    canonical = definition.IsMultiValue ? new JsonArray(JsonValue.Create(single)) : JsonValue.Create(single);
                    return true;
                }

            case FieldType.Integer:
                if (value is JsonValue iv && iv.GetValueKind() == JsonValueKind.Number && iv.TryGetValue<long>(out var l)
                    && Math.Abs(l) <= FieldLimits.MaxSafeInteger)
                {
                    canonical = JsonValue.Create(l);
                    return true;
                }

                error = Fail("invalid-integer", $"Expected an integer within ±{FieldLimits.MaxSafeInteger}.");
                return false;

            case FieldType.Decimal:
                if (value is JsonValue dv && dv.GetValueKind() == JsonValueKind.Number
                    && decimal.TryParse(dv.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    if (!TryCheckDecimal(definition, d, out var normalized, out var message))
                    {
                        error = Fail("invalid-decimal", message!);
                        return false;
                    }

                    canonical = JsonValue.Create(normalized);
                    return true;
                }

                error = Fail("invalid-decimal", "Expected a number.");
                return false;

            case FieldType.Date:
                if (TryString(value, out var ds) && IsCanonicalDate(definition.DatePrecision!.Value, ds))
                {
                    canonical = JsonValue.Create(ds);
                    return true;
                }

                error = Fail("invalid-date", definition.DatePrecision == DatePrecision.Date
                    ? "Expected a calendar date YYYY-MM-DD."
                    : "Expected a UTC instant YYYY-MM-DDTHH:mm:ss[.fff]Z.");
                return false;

            case FieldType.Boolean:
                if (value is JsonValue bv && bv.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                {
                    canonical = JsonValue.Create(bv.GetValue<bool>());
                    return true;
                }

                error = Fail("invalid-boolean", "Expected true or false.");
                return false;

            case FieldType.SingleChoice:
                if (TryChoiceId(value, out var id))
                {
                    error = CheckChoice(definition, id, choices, forAssignment, Fail);
                    canonical = error is null ? JsonValue.Create(id) : null;
                    return error is null;
                }

                error = Fail("invalid-choice", "Expected one choice id.");
                return false;

            case FieldType.MultiChoice:
                {
                    if (value is not JsonArray ids)
                    {
                        error = Fail("invalid-choice", "Expected an array of choice ids.");
                        return false;
                    }

                    var set = new SortedSet<int>();
                    foreach (var item in ids)
                    {
                        if (!TryChoiceId(item, out var cid) || !set.Add(cid))
                        {
                            error = Fail("invalid-choice", "Expected unique choice ids.");
                            return false;
                        }

                        error = CheckChoice(definition, cid, choices, forAssignment, Fail);
                        if (error is not null)
                        {
                            return false;
                        }
                    }

                    canonical = ChoiceArray(set);
                    return true;
                }

            case FieldType.User:
                if (TryString(value, out var us) && Guid.TryParseExact(us, "D", out var userId))
                {
                    // Workspace membership is checked by the caller once memberships exist (E05-T02).
                    canonical = JsonValue.Create(userId.ToString("D"));
                    return true;
                }

                error = Fail("invalid-user", "Expected a user id (UUID).");
                return false;

            default:
                error = Fail("invalid-type", "Unknown field type.");
                return false;
        }
    }

    /// <summary>Canonical MultiChoice value (ascending, unique), or null when empty.</summary>
    public static JsonNode? ChoiceArray(IEnumerable<int> choiceIds)
    {
        ArgumentNullException.ThrowIfNull(choiceIds);
        var sorted = choiceIds.Distinct().Order().ToList();
        return sorted.Count == 0 ? null : new JsonArray([.. sorted.Select(i => (JsonNode)JsonValue.Create(i))]);
    }

    /// <summary>Choice ids held by a canonical SingleChoice/MultiChoice value.</summary>
    public static IReadOnlyList<int> ChoiceIds(JsonNode? canonical) => canonical switch
    {
        null => [],
        JsonArray array => [.. array.Select(n => n!.GetValue<int>())],
        _ => [canonical.GetValue<int>()],
    };

    /// <summary>Equality of two canonical values (canonical forms are unique, so their JSON text is compared).</summary>
    public static bool AreEqual(JsonNode? left, JsonNode? right) =>
        string.Equals(left?.ToJsonString(), right?.ToJsonString(), StringComparison.Ordinal);

    public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string FormatInstant(DateTime utc) => utc.ToString(
        utc.Millisecond == 0 ? DateTimeFormat : DateTimeMillisFormat, CultureInfo.InvariantCulture);

    /// <summary>Removes trailing zeros so equal decimals have one canonical JSON form.</summary>
    public static decimal Normalize(decimal value) => value / 1.000000000000000000000000000000000m;

    internal static bool TryCheckDecimal(FieldDefinition definition, decimal value, out decimal normalized, out string? message)
    {
        var scale = definition.DecimalScale ?? FieldLimits.MaxDecimalScale;
        var precision = definition.DecimalPrecision ?? FieldLimits.MaxDecimalPrecision;
        normalized = Normalize(Math.Round(value, scale, MidpointRounding.ToEven));
        var integerDigits = Math.Truncate(Math.Abs(normalized)) == 0
            ? 0
            : Math.Truncate(Math.Abs(normalized)).ToString(CultureInfo.InvariantCulture).Length;
        if (integerDigits > precision - scale)
        {
            message = $"At most {precision - scale} digits before the decimal point (precision {precision}, scale {scale}).";
            return false;
        }

        message = null;
        return true;
    }

    private static bool TryCheckText(
        FieldDefinition definition, string value, int max, Func<string, string, FieldError> fail, out FieldError? error)
    {
        error = null;
        if (value.Length == 0)
        {
            error = fail("empty-value", "Empty strings are not stored; omit the value instead.");
        }
        else if (value.Length > max)
        {
            error = fail("too-long", $"At most {max} characters.");
        }
        else if (definition.Type == FieldType.Text && value.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
        {
            error = fail("control-characters", "Control characters other than tab and newlines are not allowed.");
        }
        else if (definition.Type == FieldType.Keyword && value.Any(char.IsControl))
        {
            error = fail("control-characters", "Keyword values cannot contain control characters.");
        }

        return error is null;
    }

    private static FieldError? CheckChoice(
        FieldDefinition definition, int choiceId, IReadOnlyList<Choice> choices, bool forAssignment,
        Func<string, string, FieldError> fail)
    {
        var choice = choices.FirstOrDefault(c => c.ChoiceId == choiceId && c.FieldId == definition.FieldId);
        if (choice is null)
        {
            return fail("unknown-choice", $"Choice {choiceId} does not belong to field {definition.Name}.");
        }

        return forAssignment && !choice.IsActive
            ? fail("inactive-choice", $"Choice '{choice.Name}' is inactive and cannot be assigned.")
            : null;
    }

    private static bool TryString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            value = v.GetValue<string>();
            return true;
        }

        return false;
    }

    private static bool TryChoiceId(JsonNode? node, out int id)
    {
        id = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out id) && id > 0;
    }

    private static bool IsCanonicalDate(DatePrecision precision, string value)
    {
        if (precision == DatePrecision.Date)
        {
            return DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }

        return DateTime.TryParseExact(
                   value, [DateTimeFormat, DateTimeMillisFormat], CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
               && FormatInstant(instant) == value;
    }
}
