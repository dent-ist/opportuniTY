using System.Globalization;

using Opportunity.Contracts.Import;
using Opportunity.Import.LoadFiles;

namespace Opportunity.Import.Mapping;

/// <summary>Turns a profile's <see cref="LoadFileSettings"/> into parser settings.</summary>
public static class LoadFileSettingsResolver
{
    public const string Custom = "custom";
    public const string Auto = "auto";

    /// <summary>
    /// Parses a delimiter as practitioners give it: the character itself, a decimal code (<c>020</c>, <c>254</c>,
    /// <c>174</c>, at least two digits) or a code point (<c>U+00FE</c>).
    /// </summary>
    public static bool TryParseDelimiter(string? text, out char value)
    {
        value = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Length == 1)
        {
            value = text[0];
            return true;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("U+", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)
            && hex is >= 0 and <= 0xFFFF)
        {
            value = (char)hex;
            return true;
        }

        if (trimmed.Length is >= 2 and <= 5 && trimmed.All(char.IsAsciiDigit)
            && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code <= 0xFFFF)
        {
            // Decimal codes 128-255 are the "ANSI" code points practitioners quote (254 = þ, 174 = ®), i.e. Latin-1.
            value = (char)code;
            return true;
        }

        if (trimmed.Length == 1)
        {
            value = trimmed[0];
            return true;
        }

        return false;
    }

    public static DelimiterInfo Describe(char c) =>
        new(c.ToString(), string.Create(CultureInfo.InvariantCulture, $"U+{(int)c:X4}"), c);

    /// <summary>The delimiter profile; errors name the offending setting.</summary>
    public static DelimiterProfile ResolveProfile(LoadFileSettings settings, List<MappingIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(issues);
        var name = string.IsNullOrWhiteSpace(settings.Delimiters) ? DelimiterProfile.Concordance.Name : settings.Delimiters.Trim();
        DelimiterProfile profile;
        if (DelimiterProfile.TryGetPreset(name, out var preset))
        {
            profile = preset;
        }
        else
        {
            if (!string.Equals(name, Custom, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new MappingIssue(MappingIssueSeverity.Error, "unknown-delimiter-preset",
                    $"Unknown delimiter preset '{name}'. Use {string.Join(", ", DelimiterProfile.Presets.Select(p => p.Name))} or custom."));
            }

            profile = DelimiterProfile.Concordance with { Name = Custom };
        }

        char? Optional(string? text, string role, char? fallback)
        {
            if (text is null)
            {
                return fallback;
            }

            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (TryParseDelimiter(text, out var c))
            {
                return c;
            }

            issues.Add(new MappingIssue(MappingIssueSeverity.Error, "invalid-delimiter",
                $"The {role} character '{text}' is not a character, decimal code or U+ code point."));
            return fallback;
        }

        char Required(string? text, string role, char fallback) =>
            text is null ? fallback
            : TryParseDelimiter(text, out var c) ? c
            : Fail(role, text, fallback);

        char Fail(string role, string text, char fallback)
        {
            issues.Add(new MappingIssue(MappingIssueSeverity.Error, "invalid-delimiter",
                $"The {role} character '{text}' is not a character, decimal code or U+ code point."));
            return fallback;
        }

        profile = profile with
        {
            Column = Required(settings.Column, "column", profile.Column),
            Quote = Optional(settings.Quote, "quote", profile.Quote),
            Newline = Optional(settings.Newline, "newline", profile.Newline),
            MultiValue = Required(settings.MultiValue, "multi-value", profile.MultiValue),
            NestedValue = Required(settings.NestedValue, "nested-value", profile.NestedValue),
        };
        foreach (var error in profile.Validate())
        {
            issues.Add(new MappingIssue(MappingIssueSeverity.Error, "invalid-delimiter", error));
        }

        return profile;
    }

    /// <summary>Null for <c>auto</c> (detect); otherwise the parsed encoding.</summary>
    public static LoadFileEncodingKind? ResolveEncoding(string? name, string role, List<MappingIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (LoadFileEncodings.TryParse(name, out var kind))
        {
            return kind;
        }

        issues.Add(new MappingIssue(MappingIssueSeverity.Error, "unknown-encoding",
            $"Unknown {role} encoding '{name}'. Use auto, utf-8, utf-16le, utf-16be or windows-1252."));
        return null;
    }

    /// <summary>Reader options for the DAT of a profile (preview or import).</summary>
    public static DatReaderOptions ReaderOptions(LoadFileSettings settings, List<MappingIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var profile = ResolveProfile(settings, issues);
        return new DatReaderOptions
        {
            Profile = profile,
            EncodingOverride = ResolveEncoding(settings.DatEncoding, "DAT", issues),
            HasHeader = settings.FirstLineContainsFieldNames,
            ConvertNewlineCharacter = settings.ConvertNewlineCharacter,
        };
    }
}
