using System.Globalization;

namespace Opportunity.Import.Mapping;

/// <summary>
/// Date and time formats as practitioners name them (<c>MM/DD/YYYY</c>, <c>DD/MM/YYYY</c>, <c>YYYYMMDD</c>, ISO 8601)
/// expanded to the .NET patterns the coercer tries. Anything else is taken as a .NET custom pattern.
/// </summary>
public static class DateFormats
{
    private static readonly string[] UsDates = ["M/d/yyyy", "M/d/yy"];
    private static readonly string[] GbDates = ["d/M/yyyy", "d/M/yy"];
    private static readonly string[] CompactDates = ["yyyyMMdd"];
    private static readonly string[] IsoDates = ["yyyy-MM-dd"];

    /// <summary>Time formats of a separate time column (and of times inside a date value).</summary>
    public static readonly IReadOnlyList<string> DefaultTimeFormats =
        ["H:mm:ss", "H:mm", "h:mm:ss tt", "h:mm tt", "H:mm:ss.FFF", "h:mmtt", "h:mm:sstt", "HHmmss"];

    private static readonly Dictionary<string, string[]> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MM/DD/YYYY"] = UsDates,
        ["M/D/YYYY"] = UsDates,
        ["MDY"] = UsDates,
        ["DD/MM/YYYY"] = GbDates,
        ["D/M/YYYY"] = GbDates,
        ["DMY"] = GbDates,
        ["YYYYMMDD"] = CompactDates,
        ["YMD"] = CompactDates,
        ["ISO 8601"] = IsoDates,
        ["ISO8601"] = IsoDates,
        ["ISO"] = IsoDates,
        ["YYYY-MM-DD"] = IsoDates,
    };

    private static readonly Dictionary<string, string[]> TimePresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HH:mm:ss"] = ["H:mm:ss", "H:mm:ss.FFF"],
        ["HH:mm"] = ["H:mm"],
        ["hh:mm tt"] = ["h:mm tt", "h:mmtt"],
        ["hh:mm:ss tt"] = ["h:mm:ss tt", "h:mm:sstt"],
        ["HHmmss"] = ["HHmmss"],
    };

    private static readonly DateTime Probe = new(2001, 11, 23, 14, 5, 6, DateTimeKind.Unspecified);

    /// <summary>Date-only patterns for the given names; empty names fall back to the locale's.</summary>
    public static IReadOnlyList<string> ExpandDates(IReadOnlyList<string>? names, bool enGb, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (names is null || names.Count == 0)
        {
            return enGb ? GbDates : UsDates;
        }

        var result = new List<string>();
        foreach (var name in names)
        {
            // Practitioners write YYYY/DD; .NET reads upper-case Y and D as literals, so translate them.
            var pattern = name.Trim().Replace('Y', 'y').Replace('D', 'd');
            if (Presets.TryGetValue(name.Trim(), out var patterns))
            {
                result.AddRange(patterns);
            }
            else if (pattern.Contains('y', StringComparison.Ordinal) && pattern.Contains('M', StringComparison.Ordinal)
                && pattern.Contains('d', StringComparison.Ordinal) && IsUsablePattern(pattern))
            {
                result.Add(pattern);
            }
            else
            {
                errors.Add($"'{name}' is not a known date format (MM/DD/YYYY, DD/MM/YYYY, YYYYMMDD, ISO 8601) or a usable custom pattern.");
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> ExpandTimes(IReadOnlyList<string>? names, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (names is null || names.Count == 0)
        {
            return DefaultTimeFormats;
        }

        var result = new List<string>();
        foreach (var name in names)
        {
            if (TimePresets.TryGetValue(name.Trim(), out var patterns))
            {
                result.AddRange(patterns);
            }
            else if (name.AsSpan().IndexOfAny('H', 'h') >= 0 && IsUsablePattern(name.Trim()))
            {
                result.Add(name.Trim());
            }
            else
            {
                errors.Add($"'{name}' is not a known time format (HH:mm:ss, HH:mm, hh:mm tt, hh:mm:ss tt) or a usable custom pattern.");
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Patterns for a value that may carry a time: each date pattern alone and followed by a space and each time
    /// pattern (a date column holding <c>03/15/2019 2:15 PM</c>, or a date merged with its time column).
    /// </summary>
    public static IReadOnlyList<string> Combine(IReadOnlyList<string> dates, IReadOnlyList<string> times)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(times);
        var result = new List<string>(dates.Count * (times.Count + 1));
        foreach (var date in dates)
        {
            result.Add(date);
            result.AddRange(times.Select(t => date + " " + t));
            if (date == "yyyyMMdd")
            {
                result.Add("yyyyMMddHHmmss");
            }
        }

        return result;
    }

    /// <summary>A custom pattern must round-trip a probe date-time, so a typo does not silently match nothing.</summary>
    private static bool IsUsablePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 64)
        {
            return false;
        }

        try
        {
            var text = Probe.ToString(pattern, CultureInfo.InvariantCulture);
            return DateTime.TryParseExact(text, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
