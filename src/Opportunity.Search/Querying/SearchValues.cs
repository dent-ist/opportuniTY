using System.Globalization;

using Opportunity.Core.Documents;

namespace Opportunity.Search.Querying;

/// <summary>
/// A date literal of ADR-008 R8 as the half-open UTC interval it covers: <c>YYYY</c>, <c>YYYY-MM</c> and
/// <c>YYYY-MM-DD</c> cover the whole year, month or day in the zone; ISO 8601 date-times cover one instant
/// (<see cref="IsInstant"/>), read in the zone when they carry no offset.
/// </summary>
internal readonly record struct DateLiteral(DateTime Start, DateTime End, bool IsInstant)
{
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <param name="zone">Zone of date-only literals and offset-less date-times; calendar-date fields pass UTC (R8).</param>
    /// <param name="calendarDate">The field holds calendar dates: an instant literal means its own calendar day.</param>
    public static bool TryParse(string text, TimeZoneInfo zone, bool calendarDate, out DateLiteral literal)
    {
        literal = default;
        var value = text.Trim();
        if (DateTime.TryParseExact(value, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var year))
        {
            literal = Whole(year, year.AddYears(1), zone);
            return true;
        }

        if (DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            literal = Whole(month, month.AddMonths(1), zone);
            return true;
        }

        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            literal = Whole(day, day.AddDays(1), zone);
            return true;
        }

        if (!DateTimeOffset.TryParseExact(value, InstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        if (calendarDate)
        {
            literal = Whole(parsed.DateTime.Date, parsed.DateTime.Date.AddDays(1), zone);
            return true;
        }

        var hasOffset = value.EndsWith('Z') || value.LastIndexOfAny(['+', '-']) > value.IndexOf('T', StringComparison.Ordinal);
        var instant = hasOffset ? parsed.UtcDateTime : ToUtc(DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified), zone);
        literal = new DateLiteral(instant, instant, IsInstant: true);
        return true;
    }

    public static string Format(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateLiteral Whole(DateTime start, DateTime end, TimeZoneInfo zone) =>
        new(ToUtc(start, zone), ToUtc(end, zone), IsInstant: false);

    /// <summary>Local wall-clock time → UTC; a time in a DST gap moves forward to the first valid minute.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone == TimeZoneInfo.Utc)
        {
            return DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);
        }

        for (var i = 0; zone.IsInvalidTime(unspecified) && i < 24 * 60; i++)
        {
            unspecified = unspecified.AddMinutes(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }
}

/// <summary>Value conversions of the planner (ADR-008 R9, R10).</summary>
internal static class SearchValues
{
    public static bool TryInteger(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    public static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    /// <summary><c>true/false/yes/no/y/n</c>, case-insensitive (ADR-008 §2).</summary>
    public static bool TryBoolean(string text, out bool value)
    {
        switch (text.Trim().ToUpperInvariant())
        {
            case "TRUE" or "YES" or "Y":
                value = true;
                return true;
            case "FALSE" or "NO" or "N":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>The natural-sort key a keyword range bound compares as (same rule as the projection builder, ADR-009 R5).</summary>
    public static string SortKey(string value) =>
        ControlNumber.SortKey(ControlNumber.TryNormalize(value, caseSensitive: false, null, out var normalized, out _) ? normalized : value);
}
