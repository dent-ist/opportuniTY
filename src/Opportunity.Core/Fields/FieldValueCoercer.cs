using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Opportunity.Core.Fields;

/// <summary>Number and date conventions of an import (Q-28: en-US and en-GB in the MVP).</summary>
public enum ImportLocale
{
    EnUs,
    EnGb,
}

/// <summary>Per-import coercion settings (delimiter profile, locale, date formats, source zone; ADR-003 §3).</summary>
public sealed record CoercionSettings
{
    public ImportLocale Locale { get; init; } = ImportLocale.EnUs;

    /// <summary>Multi-value delimiter of the delimiter profile.</summary>
    public char MultiValueDelimiter { get; init; } = ';';

    /// <summary>DAT newline marker replaced by <c>\n</c> in Text values; null disables it.</summary>
    public char? NewlineMarker { get; init; } = '®';

    /// <summary>.NET exact date formats tried in order; empty means the locale defaults. ISO 8601 is always accepted.</summary>
    public IReadOnlyList<string> DateFormats { get; init; } = [];

    /// <summary>Zone of DateTime values without an offset.</summary>
    public TimeZoneInfo SourceTimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>Unparseable dates become absent with a warning instead of a row error.</summary>
    public bool UnparseableDateAsAbsent { get; init; }

    /// <summary>Admin option: unknown choice names are reported as choices to create instead of a row error.</summary>
    public bool CreateMissingChoices { get; init; }

    /// <summary>Resolves a user's email or IdP subject to a workspace member's id; null when unknown.</summary>
    public Func<string, Guid?>? ResolveUser { get; init; }

    /// <summary>Unknown users become absent with a warning instead of a row error.</summary>
    public bool UnknownUserAsAbsent { get; init; }

    public int MaxTextLength { get; init; } = FieldLimits.MaxTextLength;
}

public enum CoercionStatus
{
    /// <summary><see cref="CoercionResult.Value"/> holds the canonical value.</summary>
    Value,

    /// <summary>No value: blank input, a zero date, or an absent-with-warning option.</summary>
    Absent,

    /// <summary>Row error (<see cref="CoercionResult.Error"/>).</summary>
    Error,

    /// <summary>Choice names must be created first (<see cref="CoercionSettings.CreateMissingChoices"/>); then coerce again.</summary>
    MissingChoices,
}

/// <param name="Status">Outcome.</param>
/// <param name="Value">Canonical JSONB value when <see cref="CoercionStatus.Value"/>.</param>
/// <param name="KeepRaw">The canonical text differs from the input: keep the original in <c>MetadataRaw</c> (R9).</param>
/// <param name="Format">The date format that matched (for <c>MetadataRaw.fmt</c>).</param>
/// <param name="Error">Field-level error when <see cref="CoercionStatus.Error"/>.</param>
/// <param name="Warnings">Non-fatal notes (stripped control characters, absent-with-warning).</param>
/// <param name="MissingChoiceNames">Names to create when <see cref="CoercionStatus.MissingChoices"/>.</param>
public sealed record CoercionResult(
    CoercionStatus Status,
    JsonNode? Value,
    bool KeepRaw,
    string? Format,
    FieldError? Error,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> MissingChoiceNames);

/// <summary>
/// The one coercion function of ADR-003 R10: raw import string → canonical JSONB value. Shared by the import preview,
/// import worker, API write validator and overlay. Pure: no I/O, no clock.
/// </summary>
public static partial class FieldValueCoercer
{
    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm:ss",
    ];

    private static readonly string[] UsFormats =
    [
        "M/d/yyyy", "M/d/yyyy H:mm", "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm tt", "M/d/yyyy h:mm:ss tt", "M/d/yy",
    ];

    private static readonly string[] GbFormats =
    [
        "d/M/yyyy", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "d/M/yyyy h:mm tt", "d/M/yyyy h:mm:ss tt", "d/M/yy",
    ];

    public static CoercionResult Coerce(
        FieldDefinition definition, string? raw, CoercionSettings settings, IReadOnlyList<Choice>? choices = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(settings);
        var input = raw?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            return Absent();
        }

        var context = new Context(definition, settings, choices ?? []);
        return definition.Type switch
        {
            FieldType.Text => context.Text(input),
            FieldType.Keyword => context.Keyword(input),
            FieldType.Integer => context.Integer(input),
            FieldType.Decimal => context.Decimal(input),
            FieldType.Date => context.Date(input),
            FieldType.Boolean => context.Boolean(input),
            FieldType.SingleChoice or FieldType.MultiChoice => context.Choices(input),
            FieldType.User => context.User(input),
            _ => context.Error("invalid-type", "Unknown field type."),
        };
    }

    private static CoercionResult Absent(params string[] warnings) =>
        new(CoercionStatus.Absent, null, false, null, null, warnings, []);

    [GeneratedRegex("^-?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex("^[0/.\\-: ]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ZeroDatePattern();

    private sealed class Context(FieldDefinition definition, CoercionSettings settings, IReadOnlyList<Choice> choices)
    {
        private readonly List<string> _warnings = [];

        public CoercionResult Error(string code, string message) =>
            new(CoercionStatus.Error, null, false, null, new FieldError(definition.Key, code, message), _warnings, []);

        public CoercionResult Text(string input)
        {
            if (settings.NewlineMarker is { } marker)
            {
                input = input.Replace(marker, '\n');
            }

            var values = Split(input);
            var cleaned = new List<string>(values.Count);
            foreach (var value in values)
            {
                var builder = new StringBuilder(value.Length);
                foreach (var c in value)
                {
                    if (!char.IsControl(c) || c is '\t' or '\n' or '\r')
                    {
                        builder.Append(c);
                    }
                }

                if (builder.Length != value.Length)
                {
                    _warnings.Add("Control characters were removed.");
                }

                if (builder.Length > settings.MaxTextLength)
                {
                    return Error("too-long", $"Text values are limited to {settings.MaxTextLength} characters.");
                }

                // Multi-value text is de-duplicated exactly (first occurrence and order kept), like Keyword.
                if (builder.Length > 0 && !cleaned.Contains(builder.ToString(), StringComparer.Ordinal))
                {
                    cleaned.Add(builder.ToString());
                }
            }

            return Strings(cleaned);
        }

        public CoercionResult Keyword(string input)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = new List<string>();
            foreach (var value in Split(input))
            {
                var normalized = value.Normalize(NormalizationForm.FormC);
                if (normalized.Length > FieldLimits.MaxKeywordLength)
                {
                    return Error("too-long", $"Keyword values are limited to {FieldLimits.MaxKeywordLength} characters.");
                }

                if (normalized.Any(char.IsControl))
                {
                    return Error("control-characters", "Keyword values cannot contain control characters.");
                }

                if (seen.Add(normalized))
                {
                    values.Add(normalized);
                }
            }

            return Strings(values);
        }

        public CoercionResult Integer(string input)
        {
            var digits = RemoveGroupSeparators(input);
            if (!IntegerPattern().IsMatch(digits))
            {
                return Error("invalid-integer", "Not an integer.");
            }

            if (!long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                || Math.Abs(value) > FieldLimits.MaxSafeInteger)
            {
                return Error("out-of-range", $"Integers are limited to ±{FieldLimits.MaxSafeInteger}; use a Keyword field.");
            }

            return Value(JsonValue.Create(value), value.ToString(CultureInfo.InvariantCulture) != input);
        }

        public CoercionResult Decimal(string input)
        {
            if (!TryParseDecimal(input, out var parsed))
            {
                return Error("invalid-decimal", "Not a decimal number.");
            }

            var significant = FieldValues.Normalize(parsed).ToString(CultureInfo.InvariantCulture).TrimStart('-').Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
            if (significant.Length > FieldLimits.MaxDecimalPrecision)
            {
                return Error("too-many-digits", $"More than {FieldLimits.MaxDecimalPrecision} significant digits.");
            }

            if (!FieldValues.TryCheckDecimal(definition, parsed, out var normalized, out var message))
            {
                return Error("invalid-decimal", message!);
            }

            return Value(JsonValue.Create(normalized), normalized.ToString(CultureInfo.InvariantCulture) != input);
        }

        public CoercionResult Date(string input)
        {
            if (ZeroDatePattern().IsMatch(input))
            {
                return Absent();
            }

            var formats = settings.DateFormats.Count > 0
                ? settings.DateFormats
                : settings.Locale == ImportLocale.EnGb ? GbFormats : UsFormats;
            foreach (var format in formats.Concat(IsoFormats))
            {
                var hasOffset = format.Contains('K', StringComparison.Ordinal) || format.Contains('z', StringComparison.Ordinal);
                if (hasOffset)
                {
                    if (DateTimeOffset.TryParseExact(input, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
                    {
                        return DateValue(DateOnly.FromDateTime(withOffset.DateTime), withOffset.UtcDateTime, format);
                    }

                    continue;
                }

                if (!DateTime.TryParseExact(input, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    continue;
                }

                if (definition.DatePrecision == DatePrecision.Date)
                {
                    return DateValue(DateOnly.FromDateTime(local), default, format);
                }

                var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                if (settings.SourceTimeZone.IsInvalidTime(unspecified))
                {
                    return Error("invalid-local-time", $"{input} does not exist in {settings.SourceTimeZone.Id} (daylight saving gap).");
                }

                return DateValue(default, TimeZoneInfo.ConvertTimeToUtc(unspecified, settings.SourceTimeZone), format);
            }

            if (settings.UnparseableDateAsAbsent)
            {
                _warnings.Add($"Unparseable date '{input}' was left empty.");
                return Absent([.. _warnings]);
            }

            return Error("invalid-date", $"'{input}' matches none of the import's date formats.");
        }

        public CoercionResult Boolean(string input)
        {
            bool? value = input.ToUpperInvariant() switch
            {
                "Y" or "YES" or "TRUE" or "T" or "1" => true,
                "N" or "NO" or "FALSE" or "F" or "0" => false,
                _ => null,
            };
            if (value is not { } b)
            {
                return Error("invalid-boolean", "Expected Y/N, Yes/No, True/False, T/F or 1/0.");
            }

            return Value(JsonValue.Create(b), (b ? "true" : "false") != input);
        }

        public CoercionResult Choices(string input)
        {
            var names = definition.Type == FieldType.MultiChoice ? Split(input) : [input];
            var ids = new List<int>();
            var missing = new List<string>();
            foreach (var name in names)
            {
                var choice = choices.FirstOrDefault(c =>
                    c.FieldId == definition.FieldId && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
                if (choice is null)
                {
                    missing.Add(name);
                }
                else if (!choice.IsActive)
                {
                    return Error("inactive-choice", $"Choice '{choice.Name}' is inactive.");
                }
                else
                {
                    ids.Add(choice.ChoiceId);
                }
            }

            if (missing.Count > 0)
            {
                return settings.CreateMissingChoices
                    ? new CoercionResult(CoercionStatus.MissingChoices, null, false, null, null, _warnings, missing)
                    : Error("unknown-choice", $"Unknown choice '{missing[0]}'.");
            }

            var value = definition.Type == FieldType.MultiChoice ? FieldValues.ChoiceArray(ids) : JsonValue.Create(ids[0]);
            return Value(value, keepRaw: false);
        }

        public CoercionResult User(string input)
        {
            var userId = settings.ResolveUser?.Invoke(input);
            if (userId is { } id)
            {
                return Value(JsonValue.Create(id.ToString("D")), keepRaw: true);
            }

            if (settings.UnknownUserAsAbsent)
            {
                _warnings.Add($"Unknown user '{input}' was left empty.");
                return Absent([.. _warnings]);
            }

            return Error("unknown-user", $"'{input}' is not a member of the workspace.");
        }

        private CoercionResult DateValue(DateOnly date, DateTime utc, string format)
        {
            var text = definition.DatePrecision == DatePrecision.Date
                ? FieldValues.FormatDate(date == default ? DateOnly.FromDateTime(utc) : date)
                : FieldValues.FormatInstant(utc);
            return new CoercionResult(CoercionStatus.Value, JsonValue.Create(text), true, format, null, _warnings, []);
        }

        private CoercionResult Value(JsonNode? value, bool keepRaw) =>
            new(CoercionStatus.Value, value, keepRaw, null, null, _warnings, []);

        private CoercionResult Strings(List<string> values)
        {
            if (values.Count == 0)
            {
                return Absent([.. _warnings]);
            }

            JsonNode node = definition.IsMultiValue
                ? new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))])
                : JsonValue.Create(values[0]);
            return Value(node, keepRaw: false);
        }

        private List<string> Split(string input)
        {
            if (!definition.IsMultiValue)
            {
                return [input];
            }

            return [.. input.Split(settings.MultiValueDelimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        }

        private string RemoveGroupSeparators(string input)
        {
            var separators = settings.Locale == ImportLocale.EnGb ? ". \u00A0" : ",";
            return string.Concat(input.Where(c => !separators.Contains(c, StringComparison.Ordinal)));
        }

        private bool TryParseDecimal(string input, out decimal value)
        {
            // Locale grouping is honoured only in well-formed groups of three, so "1.5" stays a decimal in en-GB.
            var (group, point) = settings.Locale == ImportLocale.EnGb ? ("[. \u00A0]", ",") : (",", "\\.");
            var grouped = $"^-?[0-9]{{1,3}}({group}[0-9]{{3}})+({point}[0-9]+)?$";
            var candidate = input;
            if (Regex.IsMatch(input, grouped, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                candidate = RemoveGroupSeparators(input).Replace(',', '.');
            }
            else if (settings.Locale == ImportLocale.EnGb && Regex.IsMatch(input, "^-?[0-9]+,[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                candidate = input.Replace(',', '.');
            }

            return Regex.IsMatch(candidate, "^-?([0-9]+(\\.[0-9]*)?|\\.[0-9]+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                && decimal.TryParse(candidate, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
                || Fail(out value);
        }

        private static bool Fail(out decimal value)
        {
            value = 0;
            return false;
        }
    }
}
