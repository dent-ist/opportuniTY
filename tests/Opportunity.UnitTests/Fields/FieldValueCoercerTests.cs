using AwesomeAssertions;

using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

/// <summary>Golden tests for every row of the ADR-003 §3 coercion table (R10).</summary>
public class FieldValueCoercerTests
{
    private static readonly CoercionSettings Us = new()
    {
        Locale = ImportLocale.EnUs,
        SourceTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"),
    };

    private static readonly CoercionSettings Gb = new()
    {
        Locale = ImportLocale.EnGb,
        SourceTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London"),
    };

    private static readonly Choice[] Choices =
    [
        new() { FieldId = 1100, ChoiceId = 7, Name = "Responsive", SortOrder = 0 },
        new() { FieldId = 1100, ChoiceId = 3, Name = "Not Responsive", SortOrder = 1 },
        new() { FieldId = 1100, ChoiceId = 9, Name = "Retired", SortOrder = 2, IsActive = false },
    ];

    [Theory]
    [InlineData("  hello  ", "\"hello\"")]
    [InlineData("line1®line2", "\"line1\\nline2\"")]
    [InlineData("tab\tok", "\"tab\\tok\"")]
    public void Text_is_trimmed_and_newline_markers_become_newlines(string raw, string expected)
    {
        var result = Coerce(Field(FieldType.Text), raw, Us);
        result.Status.Should().Be(CoercionStatus.Value);
        result.Value!.ToJsonString().Should().Be(expected);
        result.KeepRaw.Should().BeFalse();
    }

    [Fact]
    public void Text_control_characters_are_stripped_with_a_warning_and_overlong_text_is_an_error()
    {
        var stripped = Coerce(Field(FieldType.Text), "a\u0001b", Us);
        stripped.Value!.GetValue<string>().Should().Be("ab");
        stripped.Warnings.Should().ContainSingle();

        Coerce(Field(FieldType.Text), new string('x', 100_001), Us).Error!.Code.Should().Be("too-long");
        Coerce(Field(FieldType.Text), new string('x', 100_000), Us).Status.Should().Be(CoercionStatus.Value);
    }

    [Fact]
    public void Multi_value_text_splits_on_the_profile_delimiter()
    {
        var result = Coerce(Field(FieldType.Text, multi: true), " a ; b;;c ", Us);
        result.Value!.ToJsonString().Should().Be("""["a","b","c"]""");
    }

    [Fact]
    public void Keyword_multi_values_are_deduplicated_case_insensitively_keeping_first_spelling_and_order()
    {
        var result = Coerce(Field(FieldType.Keyword, multi: true), "Smith, J; Doe; smith, j ;DOE;Alpha", Us);
        result.Value!.ToJsonString().Should().Be("""["Smith, J","Doe","Alpha"]""");
    }

    [Fact]
    public void Keyword_rejects_values_over_8191_characters()
    {
        Coerce(Field(FieldType.Keyword), new string('k', 8_192), Us).Error!.Code.Should().Be("too-long");
        Coerce(Field(FieldType.Keyword), new string('k', 8_191), Us).Status.Should().Be(CoercionStatus.Value);
    }

    [Theory]
    [InlineData("1000", 1000L, false)]
    [InlineData("-42", -42L, false)]
    [InlineData("1,000,000", 1_000_000L, true)]
    [InlineData("9007199254740991", 9_007_199_254_740_991L, false)]
    public void Integer_removes_en_us_thousands_separators(string raw, long expected, bool keepRaw)
    {
        var result = Coerce(Field(FieldType.Integer), raw, Us);
        result.Value!.GetValue<long>().Should().Be(expected);
        result.KeepRaw.Should().Be(keepRaw);
    }

    [Theory]
    [InlineData("1.000", 1000L)]
    [InlineData("1 000 000", 1_000_000L)]
    public void Integer_removes_en_gb_thousands_separators(string raw, long expected)
    {
        Coerce(Field(FieldType.Integer), raw, Gb).Value!.GetValue<long>().Should().Be(expected);
    }

    [Theory]
    [InlineData("1.5", "invalid-integer")]
    [InlineData("12a", "invalid-integer")]
    [InlineData("9007199254740992", "out-of-range")]
    [InlineData("-99999999999999999999", "out-of-range")]
    public void Integer_rejects_decimals_and_out_of_range_values(string raw, string code)
    {
        Coerce(Field(FieldType.Integer), raw, Us).Error!.Code.Should().Be(code);
    }

    [Theory]
    [InlineData("1,234.567", "1234.57")]
    [InlineData("1234.565", "1234.56")] // half-even
    [InlineData("1234.575", "1234.58")]
    [InlineData("-0.5", "-0.5")]
    [InlineData("10.00", "10")]
    public void Decimal_rounds_half_even_to_the_field_scale(string raw, string expected)
    {
        Coerce(Decimal(precision: 12, scale: 2), raw, Us).Value!.ToJsonString().Should().Be(expected);
    }

    [Theory]
    [InlineData("1.234,5", "1234.5")]
    [InlineData("12,75", "12.75")]
    [InlineData("1.5", "1.5")]
    public void Decimal_accepts_en_gb_or_invariant_input(string raw, string expected)
    {
        Coerce(Decimal(precision: 12, scale: 2), raw, Gb).Value!.ToJsonString().Should().Be(expected);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e5")]
    [InlineData("1234567890123456789")] // 19 significant digits
    [InlineData("12345678901")] // 11 integer digits > precision 12 - scale 2
    public void Decimal_rejects_non_finite_values_and_too_many_digits(string raw)
    {
        Coerce(Decimal(precision: 12, scale: 2), raw, Us).Status.Should().Be(CoercionStatus.Error);
    }

    [Fact]
    public void DateTime_values_convert_from_the_source_zone_to_utc_and_keep_the_raw_string()
    {
        var result = Coerce(Date(DatePrecision.DateTime), "03/01/2025 09:05", Us);

        result.Value!.GetValue<string>().Should().Be("2025-03-01T14:05:00Z");
        result.KeepRaw.Should().BeTrue();
        result.Format.Should().Be("M/d/yyyy H:mm");
    }

    [Theory]
    [InlineData("2025-03-01T14:05:00Z", "2025-03-01T14:05:00Z")]
    [InlineData("2025-03-01T09:05:00-05:00", "2025-03-01T14:05:00Z")]
    [InlineData("2025-07-01 12:00:00", "2025-07-01T16:00:00Z")] // EDT
    [InlineData("3/1/2025 9:05:07 PM", "2025-03-02T02:05:07Z")]
    [InlineData("2025-03-01T14:05:00.25Z", "2025-03-01T14:05:00.250Z")]
    public void DateTime_accepts_iso_8601_and_locale_formats(string raw, string expected)
    {
        Coerce(Date(DatePrecision.DateTime), raw, Us).Value!.GetValue<string>().Should().Be(expected);
    }

    [Theory]
    [InlineData("01/03/2025", "2025-03-01")]
    [InlineData("1/3/2025 23:30", "2025-03-01")]
    [InlineData("2025-03-01", "2025-03-01")]
    public void Date_precision_keeps_the_calendar_date_without_zone_conversion(string raw, string expected)
    {
        Coerce(Date(DatePrecision.Date), raw, Gb).Value!.GetValue<string>().Should().Be(expected);
    }

    [Theory]
    [InlineData("00/00/0000")]
    [InlineData("0000-00-00")]
    [InlineData("   ")]
    [InlineData("")]
    public void Zero_and_blank_dates_are_absent_without_error(string raw)
    {
        var result = Coerce(Date(DatePrecision.DateTime), raw, Us);
        result.Status.Should().Be(CoercionStatus.Absent);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Unparseable_dates_are_errors_unless_the_absent_option_is_set()
    {
        Coerce(Date(DatePrecision.DateTime), "next Tuesday", Us).Error!.Code.Should().Be("invalid-date");

        var lenient = Coerce(Date(DatePrecision.DateTime), "next Tuesday", Us with { UnparseableDateAsAbsent = true });
        lenient.Status.Should().Be(CoercionStatus.Absent);
        lenient.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void Custom_date_formats_are_used_in_order()
    {
        var settings = Us with { DateFormats = ["yyyyMMdd"] };
        Coerce(Date(DatePrecision.Date), "20250301", settings).Value!.GetValue<string>().Should().Be("2025-03-01");
    }

    [Fact]
    public void Local_times_in_a_daylight_saving_gap_are_errors()
    {
        Coerce(Date(DatePrecision.DateTime), "3/9/2025 2:30", Us).Error!.Code.Should().Be("invalid-local-time");
    }

    [Theory]
    [InlineData("Y", true, true)]
    [InlineData("yes", true, true)]
    [InlineData("TRUE", true, true)]
    [InlineData("t", true, true)]
    [InlineData("1", true, true)]
    [InlineData("true", true, false)]
    [InlineData("N", false, true)]
    [InlineData("No", false, true)]
    [InlineData("False", false, true)]
    [InlineData("f", false, true)]
    [InlineData("0", false, true)]
    [InlineData("false", false, false)]
    public void Boolean_accepts_the_adr003_spellings(string raw, bool expected, bool keepRaw)
    {
        var result = Coerce(Field(FieldType.Boolean), raw, Us);
        result.Value!.GetValue<bool>().Should().Be(expected);
        result.KeepRaw.Should().Be(keepRaw);
    }

    [Fact]
    public void Boolean_rejects_other_values_and_blank_is_absent()
    {
        Coerce(Field(FieldType.Boolean), "maybe", Us).Error!.Code.Should().Be("invalid-boolean");
        Coerce(Field(FieldType.Boolean), " ", Us).Status.Should().Be(CoercionStatus.Absent);
    }

    [Fact]
    public void SingleChoice_resolves_names_case_insensitively_to_choice_ids()
    {
        Coerce(Field(FieldType.SingleChoice), "  responsive ", Us, Choices).Value!.GetValue<int>().Should().Be(7);
        Coerce(Field(FieldType.SingleChoice), "Hot", Us, Choices).Error!.Code.Should().Be("unknown-choice");
        Coerce(Field(FieldType.SingleChoice), "retired", Us, Choices).Error!.Code.Should().Be("inactive-choice");
    }

    [Fact]
    public void Unknown_choices_are_reported_for_creation_when_the_admin_option_is_set()
    {
        var result = Coerce(Field(FieldType.MultiChoice, multi: true), "Responsive;Hot;Cold", Us with { CreateMissingChoices = true }, Choices);
        result.Status.Should().Be(CoercionStatus.MissingChoices);
        result.MissingChoiceNames.Should().Equal("Hot", "Cold");
    }

    [Fact]
    public void MultiChoice_is_an_ascending_unique_array_of_choice_ids()
    {
        Coerce(Field(FieldType.MultiChoice, multi: true), "Responsive; Not Responsive;RESPONSIVE", Us, Choices)
            .Value!.ToJsonString().Should().Be("[3,7]");
    }

    [Fact]
    public void User_resolves_to_a_member_id_or_fails()
    {
        var alice = Guid.CreateVersion7();
        var settings = Us with { ResolveUser = s => s.Equals("alice@example.test", StringComparison.OrdinalIgnoreCase) ? alice : null };

        var result = Coerce(Field(FieldType.User), "Alice@Example.test", settings);
        result.Value!.GetValue<string>().Should().Be(alice.ToString("D"));
        Coerce(Field(FieldType.User), "bob@example.test", settings).Error!.Code.Should().Be("unknown-user");
        Coerce(Field(FieldType.User), "bob@example.test", settings with { UnknownUserAsAbsent = true }).Status.Should().Be(CoercionStatus.Absent);
    }

    [Theory]
    [InlineData(FieldType.Text, "Hello world")]
    [InlineData(FieldType.Keyword, "ABC-123")]
    [InlineData(FieldType.Integer, "-123456")]
    [InlineData(FieldType.Boolean, "true")]
    public void Coerce_is_idempotent_on_canonical_output(FieldType type, string raw)
    {
        var first = Coerce(Field(type), raw, Us);
        var second = Coerce(Field(type), first.Value!.ToString(), Us);
        second.Value!.ToJsonString().Should().Be(first.Value.ToJsonString());
    }

    [Theory]
    [InlineData("03/01/2025 09:05")]
    [InlineData("2025-03-01T14:05:00.250Z")]
    public void Coerce_is_idempotent_on_canonical_dates_and_decimals(string raw)
    {
        var first = Coerce(Date(DatePrecision.DateTime), raw, Us);
        Coerce(Date(DatePrecision.DateTime), first.Value!.GetValue<string>(), Us).Value!.ToJsonString().Should().Be(first.Value.ToJsonString());

        var dec = Coerce(Decimal(18, 6), "1,234.5", Us);
        Coerce(Decimal(18, 6), dec.Value!.ToJsonString(), Us).Value!.ToJsonString().Should().Be(dec.Value.ToJsonString());
        Coerce(Decimal(18, 6), dec.Value!.ToJsonString(), Gb).Value!.ToJsonString().Should().Be(dec.Value.ToJsonString());
    }

    private static CoercionResult Coerce(FieldDefinition field, string raw, CoercionSettings settings, IReadOnlyList<Choice>? choices = null) =>
        FieldValueCoercer.Coerce(field, raw, settings, choices);

    private static FieldDefinition Field(FieldType type, bool multi = false) => new()
    {
        FieldId = 1100,
        Name = type.ToString(),
        Type = type,
        Storage = FieldStorage.Metadata,
        IsMultiValue = multi,
    };

    private static FieldDefinition Decimal(short precision, short scale)
    {
        var field = Field(FieldType.Decimal);
        field.DecimalPrecision = precision;
        field.DecimalScale = scale;
        return field;
    }

    private static FieldDefinition Date(DatePrecision precision)
    {
        var field = Field(FieldType.Date);
        field.DatePrecision = precision;
        return field;
    }
}
