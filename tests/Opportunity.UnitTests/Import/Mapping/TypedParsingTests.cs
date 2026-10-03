using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Import.Mapping;

using static Opportunity.UnitTests.Import.Mapping.MappingTestSupport;

namespace Opportunity.UnitTests.Import.Mapping;

/// <summary>E08-T02 typed parsing: dates and time zones, companion time columns, multi-values, booleans, identifiers.</summary>
public class TypedParsingTests
{
    private static MappedRow MapOne(ImportProfileDefinition profile, string[] header, string[] values, FieldCatalog? catalog = null)
    {
        var mapping = MappingCompiler.Compile(profile, header, catalog ?? Catalog());
        mapping.Issues.Where(i => i.Severity == MappingIssueSeverity.Error).Should().BeEmpty();
        return mapping.Map(1, values);
    }

    private static MappedCell CellFor(MappedRow row, string column, int? fieldId = null) =>
        row.Cells.Single(c => c.Column.Column == column && (fieldId is null || c.Target.FieldId == fieldId));

    private static ImportProfileDefinition Profile(params ColumnMapping[] columns) =>
        new() { Columns = [Map("DOCID", FieldTarget(SystemFields.ControlNumber)), .. columns] };

    [Theory]
    [InlineData("00/00/0000")]
    [InlineData("0000-00-00")]
    [InlineData("00000000")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00/00/0000 00:00:00")]
    public void Zero_and_blank_dates_store_null_without_error(string raw)
    {
        var catalog = Catalog(Custom(1000, "Date Only", FieldType.Date, precision: DatePrecision.Date));
        var row = MapOne(
            Profile(Map("DATESENT", FieldTarget(SystemFields.DateSent)), Map("DOCDATE", FieldTarget(1000, "Date Only"))),
            ["DOCID", "DATESENT", "DOCDATE"], ["ABC0001", raw, raw], catalog);

        row.HasErrors.Should().BeFalse();
        CellFor(row, "DATESENT").Value.Should().BeNull();
        CellFor(row, "DATESENT").Status.Should().Be(CoercionStatus.Absent);
        CellFor(row, "DOCDATE").Value.Should().BeNull();
    }

    [Fact]
    public void Zero_date_with_a_zero_time_column_stores_null_without_error_or_warning()
    {
        var row = MapOne(Profile(), ["DOCID", "DateSent", "TimeSent"], ["ABC0001", "00/00/0000", "00:00:00"]);

        var cell = CellFor(row, "DateSent");
        cell.Value.Should().BeNull();
        cell.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("en-US", "03/04/2019", "2019-03-04T05:00:00Z")]
    [InlineData("en-GB", "03/04/2019", "2019-04-02T23:00:00Z")]
    public void Locale_decides_the_day_month_order_and_the_source_zone_converts_to_utc(string locale, string raw, string utc)
    {
        var zone = locale == "en-US" ? "America/New_York" : "Europe/London";
        var profile = Profile(Map("DATESENT", FieldTarget(SystemFields.DateSent))) with
        {
            Parsing = new ParsingDefaults { Locale = locale, SourceTimeZone = zone },
        };
        var cell = CellFor(MapOne(profile, ["DOCID", "DATESENT"], ["A1", raw]), "DATESENT");

        cell.Value!.GetValue<string>().Should().Be(utc);
        cell.KeepRaw.Should().BeTrue();
        cell.Raw.Should().Be(raw);
    }

    [Theory]
    [InlineData("MM/DD/YYYY", "12/31/2019", "11:15 PM", "2020-01-01T04:15:00Z")]
    [InlineData("DD/MM/YYYY", "31/12/2019", "23:15:00", "2020-01-01T04:15:00Z")]
    [InlineData("YYYYMMDD", "20191231", "11:15:00 PM", "2020-01-01T04:15:00Z")]
    [InlineData("YYYYMMDD", "20190704", "", "2019-07-04T04:00:00Z")]
    [InlineData("ISO 8601", "2019-12-31T23:15:00-05:00", "", "2020-01-01T04:15:00Z")]
    [InlineData("MM/DD/YYYY", "2019-12-31T23:15:00+01:00", "", "2019-12-31T22:15:00Z")]
    [InlineData("dd.MM.yyyy", "31.12.2019", "23:15", "2020-01-01T04:15:00Z")]
    [InlineData("DD.MM.YYYY", "31.12.2019", "23:15", "2020-01-01T04:15:00Z")]
    public void Per_column_date_format_merges_the_time_column_in_the_source_zone(string format, string date, string time, string utc)
    {
        var profile = Profile(Map("DateSent", new ColumnParsing { DateFormats = [format], TimeColumn = "TimeSent", SourceTimeZone = "America/New_York" },
            FieldTarget(SystemFields.DateSent)));
        var cell = CellFor(MapOne(profile, ["DOCID", "DateSent", "TimeSent"], ["A1", date, time]), "DateSent");

        cell.Error.Should().BeNull();
        cell.Value!.GetValue<string>().Should().Be(utc);
        cell.Raw.Should().Be(string.IsNullOrEmpty(time) ? date : date + " " + time);
    }

    [Fact]
    public void A_wrong_date_format_is_a_row_error_with_a_code_unless_unparseable_dates_are_blanked()
    {
        var profile = Profile(Map("DateSent", new ColumnParsing { DateFormats = ["MM/DD/YYYY"] }, FieldTarget(SystemFields.DateSent)));
        var error = CellFor(MapOne(profile, ["DOCID", "DateSent"], ["A1", "31/12/2019"]), "DateSent");
        error.Error!.Code.Should().Be("invalid-date");

        var blanked = CellFor(MapOne(profile with { Parsing = new ParsingDefaults { UnparseableDatesAsBlank = true } }, ["DOCID", "DateSent"], ["A1", "31/12/2019"]), "DateSent");
        blanked.Error.Should().BeNull();
        blanked.Value.Should().BeNull();
        blanked.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void A_local_time_inside_a_daylight_saving_gap_is_an_error_and_a_time_without_a_date_is_a_warning()
    {
        var profile = Profile(Map("DateSent", new ColumnParsing { TimeColumn = "TimeSent" }, FieldTarget(SystemFields.DateSent))) with
        {
            Parsing = new ParsingDefaults { SourceTimeZone = "America/New_York" },
        };
        var mapping = MappingCompiler.Compile(profile, ["DOCID", "DateSent", "TimeSent"], Catalog());

        CellFor(mapping.Map(1, ["A1", "03/10/2019", "2:30 AM"]), "DateSent").Error!.Code.Should().Be("invalid-local-time");
        var orphan = CellFor(mapping.Map(2, ["A2", "", "10:00"]), "DateSent");
        orphan.Value.Should().BeNull();
        orphan.Warnings.Should().ContainSingle().Which.Should().Contain("without a date");
    }

    [Fact]
    public void Invalid_time_zone_and_date_format_are_profile_errors()
    {
        var profile = Profile(Map("DateSent", new ColumnParsing { DateFormats = ["QQ/ZZ"], SourceTimeZone = "Mars/Olympus" }, FieldTarget(SystemFields.DateSent))) with
        {
            Parsing = new ParsingDefaults { SourceTimeZone = "Not/AZone", Locale = "fr-FR" },
        };
        var issues = MappingCompiler.Compile(profile, ["DOCID", "DateSent"], Catalog()).Issues.Select(i => i.Code).ToList();

        issues.Should().Contain(["invalid-time-zone", "invalid-date-format", "unknown-locale"]);
    }

    [Fact]
    public void Multi_values_split_on_the_profile_delimiter_trimmed_and_de_duplicated()
    {
        var catalog = Catalog(Custom(1000, "All Custodians", FieldType.Keyword, multi: true), Custom(1001, "All Paths", FieldType.Text, multi: true));
        var row = MapOne(Profile(), ["DOCID", "AllCustodians", "AllPaths"], ["A1", "A; B; A", @" \\srv\a ; \\srv\b;\\srv\a;; "], catalog);

        CellFor(row, "AllCustodians").Value!.ToJsonString().Should().Be("""["A","B"]""");
        CellFor(row, "AllPaths").Value!.ToJsonString().Should().Be("""["\\\\srv\\a","\\\\srv\\b"]""");
    }

    [Fact]
    public void A_column_can_override_the_multi_value_delimiter()
    {
        var profile = Profile(Map("To", new ColumnParsing { MultiValueDelimiter = "," }, NewField("To", ImportFieldType.Text, multi: true)));

        CellFor(MapOne(profile, ["DOCID", "To"], ["A1", "a@x.test, b@x.test,a@x.test"]), "To").Value!.ToJsonString()
            .Should().Be("""["a@x.test","b@x.test"]""");
    }

    [Theory]
    [InlineData("Y", true)]
    [InlineData("n", false)]
    [InlineData("Yes", true)]
    [InlineData("NO", false)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("X", true)]
    [InlineData("-", false)]
    public void Booleans_accept_standard_and_profile_tokens(string raw, bool expected)
    {
        var profile = Profile(Map("Inclusive", new ColumnParsing { TrueValues = ["X"], FalseValues = ["-"] }, NewField("Inclusive Email", ImportFieldType.Boolean)));

        CellFor(MapOne(profile, ["DOCID", "Inclusive"], ["A1", raw]), "Inclusive").Value!.GetValue<bool>().Should().Be(expected);
    }

    [Fact]
    public void An_unknown_boolean_token_is_a_coded_row_error()
    {
        var profile = Profile(Map("Inclusive", NewField("Inclusive Email", ImportFieldType.Boolean)));

        CellFor(MapOne(profile, ["DOCID", "Inclusive"], ["A1", "maybe"]), "Inclusive").Error!.Code.Should().Be("invalid-boolean");
    }

    [Fact]
    public void Control_numbers_are_required_validated_and_take_the_import_prefix()
    {
        var mapping = MappingCompiler.Compile(Profile(Map("PARENT", Structural(StructuralTarget.ParentId))) with { ControlNumberPrefix = "VOL1-" },
            ["DOCID", "PARENT"], Catalog());

        var ok = mapping.Map(1, ["  abc 0001 ", "abc0000"]);
        ok.ControlNumber.Should().Be("VOL1-abc 0001");
        ok.ControlNumberNorm.Should().Be("VOL1-ABC 0001");
        CellFor(ok, "PARENT").Value!.GetValue<string>().Should().Be("VOL1-abc0000");
        CellFor(mapping.Map(2, ["", ""]), "DOCID").Error!.Code.Should().Be("control-number-missing");
        CellFor(mapping.Map(3, ["A\u0001B", ""]), "DOCID").Error!.Code.Should().Be("control-characters");
        CellFor(mapping.Map(4, [new string('X', 300), ""]), "DOCID").Error!.Code.Should().Be("invalid-control-number");
    }

    [Fact]
    public void A_parent_id_equal_to_the_own_control_number_means_no_parent()
    {
        var row = MapOne(Profile(Map("ParentID", Structural(StructuralTarget.ParentId))), ["DOCID", "ParentID"], ["ABC0001", "abc0001"]);

        CellFor(row, "ParentID").Value.Should().BeNull();
        row.HasErrors.Should().BeFalse();
    }

    [Theory]
    [InlineData("D41D8CD98F00B204E9800998ECF8427E", "d41d8cd98f00b204e9800998ecf8427e", null)]
    [InlineData("d41d8cd98f00b204e9800998ecf8427", null, "invalid-hash")]
    [InlineData("not-a-hash-value-not-a-hash-valu", null, "invalid-hash")]
    [InlineData("", null, null)]
    public void Hashes_are_lower_case_hex_of_the_right_length(string raw, string? expected, string? error)
    {
        var cell = CellFor(MapOne(Profile(), ["DOCID", "MD5Hash"], ["A1", raw]), "MD5Hash");

        cell.Value?.GetValue<string>().Should().Be(expected);
        cell.Error?.Code.Should().Be(error);
    }

    [Fact]
    public void Attachment_ranges_with_one_end_only_carry_a_warning()
    {
        var row = MapOne(Profile(), ["DOCID", "BegAttach", "EndAttach"], ["A1", "A1", ""]);

        CellFor(row, "BegAttach").Warnings.Should().ContainSingle().Which.Should().Contain("attachment range");
    }

    [Fact]
    public void Choice_values_resolve_by_name_and_missing_choices_are_reported_for_creation_when_allowed()
    {
        var field = Custom(1000, "Doc Type", FieldType.MultiChoice);
        Choice[] choices = [new() { FieldId = 1000, ChoiceId = 5, Name = "Email", SortOrder = 0 }];
        var catalog = Catalog([field], choices);
        var strict = MappingCompiler.Compile(Profile(Map("DocType", FieldTarget(1000, "Doc Type"))), ["DOCID", "DocType"], catalog);
        CellFor(strict.Map(1, ["A1", "email; Spreadsheet"]), "DocType").Error!.Code.Should().Be("unknown-choice");
        CellFor(strict.Map(1, ["A1", "EMAIL;Email"]), "DocType").Value!.ToJsonString().Should().Be("[5]");

        var create = MappingCompiler.Compile(Profile(Map("DocType", new ColumnParsing { CreateMissingChoices = true }, FieldTarget(1000, "Doc Type"))),
            ["DOCID", "DocType"], catalog);
        var cell = CellFor(create.Map(1, ["A1", "email; Spreadsheet; spreadsheet"]), "DocType");
        cell.Error.Should().BeNull();
        cell.MissingChoices.Should().Equal("Spreadsheet");
        cell.Value!.ToJsonString().Should().Be("""["Email","Spreadsheet"]""");
    }

    [Fact]
    public void Integers_and_decimals_coerce_with_locale_grouping_and_report_errors()
    {
        var amount = Custom(1000, "Amount", FieldType.Decimal);
        amount.DecimalPrecision = 18;
        amount.DecimalScale = 2;
        var catalog = Catalog(amount);
        var profile = Profile(Map("FileSize", FieldTarget(SystemFields.FileSize)), Map("Amount", FieldTarget(1000, "Amount")));
        var mapping = MappingCompiler.Compile(profile, ["DOCID", "FileSize", "Amount"], catalog);

        var row = mapping.Map(1, ["A1", "1,048,576", "1,234.50"]);
        CellFor(row, "FileSize").Value!.GetValue<long>().Should().Be(1_048_576);
        CellFor(row, "Amount").Value!.ToJsonString().Should().Be("1234.5");
        var bad = mapping.Map(2, ["A2", "12 KB", "abc"]);
        CellFor(bad, "FileSize").Error!.Code.Should().Be("invalid-integer");
        CellFor(bad, "Amount").Error!.Code.Should().Be("invalid-decimal");
    }

    [Fact]
    public void One_column_can_feed_two_targets_with_their_own_coercion()
    {
        var profile = new ImportProfileDefinition
        {
            Columns = [Map("BEGDOC", FieldTarget(SystemFields.ControlNumber), FieldTarget(SystemFields.BegBates))],
        };
        var row = MapOne(profile, ["BEGDOC"], ["PROD000001"]);

        row.Cells.Select(c => c.Target.Label).Should().Equal("Control Number", "Beg Bates");
        row.Cells.Should().OnlyContain(c => c.Value!.GetValue<string>() == "PROD000001");
    }

    [Fact]
    public void Text_values_keep_converted_line_breaks_and_strip_control_characters_with_a_warning()
    {
        var row = MapOne(Profile(Map("Body", NewField("Body", ImportFieldType.Text))), ["DOCID", "Body"], ["A1", "Line 1\nLine 2\u0007"]);

        var cell = CellFor(row, "Body");
        cell.Value!.GetValue<string>().Should().Be("Line 1\nLine 2");
        cell.Warnings.Should().ContainSingle();
        JsonNode.DeepEquals(cell.Value, JsonValue.Create("Line 1\nLine 2")).Should().BeTrue();
    }
}
