using System.Text;

using AwesomeAssertions;

using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;

using static Opportunity.UnitTests.Import.Mapping.MappingTestSupport;

namespace Opportunity.UnitTests.Import.Mapping;

/// <summary>
/// E08-T02 mapping preview on messy, production-style (synthetic) load files: coerced values, per-column error
/// counts, explicit treatment of unmapped columns, rejected rows and choices/fields to be created.
/// </summary>
public class MappingPreviewTests
{
    private static readonly string[] Header =
    [
        "DOCID", "BEGATTACH", "ENDATTACH", "PARENT_ID", "CUSTODIAN", "AllCustodians", "DATESENT", "TIMESENT", "EMAIL_SUBJECT",
        "Doc Type", "MD5Hash", "NATIVELINK", "TEXTLINK", "Vendor Internal ID", " FILESIZE ",
    ];

    private static string MessyDat() => Concordance(
        Header,
        ["ABC0001", "ABC0001", "ABC0003", "", "Smith, John", "Smith, John; Doe, Jane; Smith, John", "03/15/2019", "2:15 PM",
            "Re: Q1 numbers" + Reg + Reg + "see below", "Email", "D41D8CD98F00B204E9800998ECF8427E", @"NATIVES\001\ABC0001.msg", @"TEXT\001\ABC0001.txt", "v-1", "1,024"],
        ["ABC0002", "ABC0001", "ABC0003", "ABC0001", "Smith, John", "Smith, John", "00/00/0000", "", "Budget þ draft", "Attachment",
            "", @"NATIVES\001\ABC0002.xlsx", @"TEXT\001\ABC0002.txt", "v-2", ""],
        ["ABC0003", "ABC0001", "ABC0003", "ABC0001", "Smith, John", "", "13/45/2019", "10:00", "", "Attachment", "xyz",
            "", @"TEXT\001\ABC0003.txt", "v-3", "77"],
        ["ABC0004", "ABC0004", "ABC0004", "", "Doe, Jane", "Doe, Jane", "", "", "short row"],
        ["abc0001", "", "", "", "Doe, Jane", "Doe, Jane;Doe, Jane", "2019-03-16T09:00:00Z", "", "dup", "Email", "", "", "", "v-5", ""]);

    private static ImportProfileDefinition Profile(UnmappedColumnPolicy unmapped = UnmappedColumnPolicy.Ignore) => new()
    {
        UnmappedColumns = unmapped,
        Parsing = new ParsingDefaults { SourceTimeZone = "America/New_York" },
        Columns = [Map("Doc Type", new ColumnParsing { CreateMissingChoices = true }, NewField("Doc Type", ImportFieldType.SingleChoice))],
    };

    [Fact]
    public async Task Preview_shows_coerced_values_and_per_column_error_counts()
    {
        var result = await PreviewAsync(MessyDat(), Profile(), Catalog());

        result.File.Encoding.Should().Be("utf-8 (BOM)");
        result.File.Column.Decimal.Should().Be(20);
        result.File.Quote!.Decimal.Should().Be(254);
        result.File.Newline!.Decimal.Should().Be(174);
        result.Rows.Should().HaveCount(5);

        var first = result.Rows[0];
        first.ErrorCount.Should().Be(0);
        first.Cell("DATESENT").Value!.GetValue<string>().Should().Be("2019-03-15T18:15:00Z");
        first.Cell("DATESENT").Raw.Should().Be("03/15/2019 2:15 PM");
        first.Cell("AllCustodians").Value!.ToJsonString().Should().Be("""["Smith, John","Doe, Jane"]""");
        first.Cell("EMAIL_SUBJECT").Value!.GetValue<string>().Should().Be("Re: Q1 numbers\n\nsee below");
        first.Cell("MD5Hash").Value!.GetValue<string>().Should().Be("d41d8cd98f00b204e9800998ecf8427e");
        first.Cell("FILESIZE").Value!.GetValue<long>().Should().Be(1024);
        first.Cell("Doc Type").Value!.GetValue<string>().Should().Be("Email");

        var second = result.Rows[1];
        second.Cell("DATESENT").Value.Should().BeNull();
        second.Cell("DATESENT").Error.Should().BeNull();
        second.Cell("EMAIL_SUBJECT").Value!.GetValue<string>().Should().Be("Budget þ draft");
        second.Cell("PARENT_ID").Value!.GetValue<string>().Should().Be("ABC0001");

        result.Rows[2].ErrorCount.Should().Be(2);
        result.Rows[2].Cell("DATESENT").Error!.Code.Should().Be("invalid-date");
        result.Rows[2].Cell("MD5Hash").Error!.Code.Should().Be("invalid-hash");

        var rejected = result.Rows[3];
        rejected.Rejected.Should().BeTrue();
        rejected.ParserIssues.Should().ContainSingle(i => i.Code == "field-count-mismatch");
        rejected.Cells.Should().BeEmpty();

        result.Rows[4].Cell("DOCID").Error!.Code.Should().Be("duplicate-control-number");
        result.Rows[4].Cell("AllCustodians").Value!.ToJsonString().Should().Be("""["Doe, Jane"]""");

        result.Column("DATESENT").ErrorCount.Should().Be(1);
        result.Column("MD5Hash").ErrorCount.Should().Be(1);
        result.Column("DOCID").ErrorCount.Should().Be(1);
        result.Column("CUSTODIAN").ErrorCount.Should().Be(0);
        result.Column("DATESENT").SampleValues.Should().Equal("03/15/2019", "00/00/0000", "13/45/2019");
        result.Column("DATESENT").BlankCount.Should().Be(1);
        result.Column("TIMESENT").Status.Should().Be(ColumnStatus.MergedIntoDate);
        result.Column("TIMESENT").MergedInto.Should().Be("DATESENT");
        result.CanImport.Should().BeTrue();
    }

    [Fact]
    public async Task Unmapped_columns_are_explicitly_ignored_or_stored_as_new_text_fields()
    {
        var ignored = await PreviewAsync(MessyDat(), Profile(), Catalog());
        ignored.Column("Vendor Internal ID").Status.Should().Be(ColumnStatus.Unmapped);
        ignored.Column("Vendor Internal ID").Targets.Should().BeEmpty();
        ignored.EffectiveProfile.Columns.Single(c => c.Column == "Vendor Internal ID").Ignore.Should().BeTrue();

        var stored = await PreviewAsync(MessyDat(), Profile(UnmappedColumnPolicy.CreateTextField), Catalog());
        var column = stored.Column("Vendor Internal ID");
        column.Status.Should().Be(ColumnStatus.StoredAsNewTextField);
        column.Targets.Single().Resolution.Should().Be(TargetResolution.WillCreate);
        stored.Rows[0].Cell("Vendor Internal ID").Value!.GetValue<string>().Should().Be("v-1");
        stored.NewFields.Should().Contain(f => f.Name == "Vendor Internal ID" && f.Type == ImportFieldType.Text);
    }

    [Fact]
    public async Task New_fields_and_choices_to_create_are_previewed_with_counts()
    {
        var result = await PreviewAsync(MessyDat(), Profile(), Catalog());

        var docType = result.NewFields.Single(f => f.Name == "Doc Type");
        docType.Type.Should().Be(ImportFieldType.SingleChoice);
        docType.ChoicesToCreate.Should().Be(2);
        docType.SampleChoices.Should().Equal("Email", "Attachment");
        result.NewFields.Should().Contain(f => f.Name == "Custodian").And.Contain(f => f.Name == "All Custodians" && f.IsMultiValue);
        result.Column("AllCustodians").Targets.Single().MatchedBy.Should().Be(MatchKind.NormalizedName);
        result.Column("PARENT_ID").Targets.Single().MatchedBy.Should().Be(MatchKind.NormalizedName);
        result.Column("NATIVELINK").Targets.Single().MatchedBy.Should().Be(MatchKind.Alias);
        result.Column("NATIVELINK").Targets.Single().Alias.Should().Be("NativeLink");
    }

    [Fact]
    public async Task Existing_choice_fields_report_how_many_choices_a_load_would_create()
    {
        var docType = Custom(1000, "Doc Type", FieldType.SingleChoice);
        var catalog = Catalog([docType], [new Choice { FieldId = 1000, ChoiceId = 1, Name = "Email", SortOrder = 0 }]);
        var profile = Profile() with { Columns = [Map("Doc Type", new ColumnParsing { CreateMissingChoices = true }, FieldTarget(1000, "Doc Type"))] };

        var result = await PreviewAsync(MessyDat(), profile, catalog);

        result.Issues.Should().ContainSingle(i => i.Code == "choices-to-create").Which.Message.Should().Contain("Will create 1 choice(s) in Doc Type: Attachment");
        result.Rows[0].Cell("Doc Type").Value!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task A_partial_sample_drops_its_cut_off_last_record()
    {
        var dat = MessyDat();
        var bytes = Encoding.UTF8.GetBytes(dat);
        var cut = bytes[..(bytes.Length - 20)];

        var full = await MappingPreviewer.PreviewAsync(new MemoryStream(cut), Profile(), Catalog(), cancellationToken: TestContext.Current.CancellationToken);
        full.Rows[^1].Rejected.Should().BeTrue("the cut-off record has too few fields");

        var partial = await MappingPreviewer.PreviewAsync(new MemoryStream(cut), Profile(), Catalog(),
            new MappingPreviewOptions { SampleIsPartial = true }, TestContext.Current.CancellationToken);
        partial.Rows.Should().HaveCount(4);
        partial.Rows[^1].RowNumber.Should().Be(4);
    }

    [Fact]
    public async Task Row_limit_is_respected()
    {
        var result = await MappingPreviewer.PreviewAsync(Utf8(MessyDat()), Profile(), Catalog(), new MappingPreviewOptions { Rows = 2 },
            TestContext.Current.CancellationToken);

        result.Rows.Select(r => r.RowNumber).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_utf8_file_read_as_windows_1252_is_flagged_as_misdecoded()
    {
        var profile = Profile() with { LoadFile = new LoadFileSettings { DatEncoding = "windows-1252" } };
        var result = await MappingPreviewer.PreviewAsync(Utf8(MessyDat(), bom: false), profile, Catalog(), cancellationToken: TestContext.Current.CancellationToken);

        result.File.MisdecodeSuspected.Should().BeTrue();
        result.File.Encoding.Should().Be("windows-1252");
    }

    [Fact]
    public async Task Pilcrow_files_with_custom_decimal_delimiters_and_a_csv_preset_parse_with_the_same_mapping()
    {
        string[][] rows =
        [
            ["BegBates", "EndBates", "Date Sent", "All Custodians"],
            ["PROD-000001", "PROD-000003", "20190704", "A;B;A"],
        ];
        var pilcrow = string.Concat(rows.Select(r => string.Join('¶', r.Select(v => "þ" + v + "þ")) + "\n"));
        var csv = string.Concat(rows.Select(r => string.Join(',', r.Select(v => "\"" + v + "\"")) + "\r\n"));
        ImportProfileDefinition custom = new()
        {
            LoadFile = new LoadFileSettings { Delimiters = "custom", Column = "182", Quote = "254", Newline = "U+00AE" },
            Parsing = new ParsingDefaults { DateFormats = ["YYYYMMDD"] },
        };

        var a = await PreviewAsync(pilcrow, custom, Catalog());
        var b = await PreviewAsync(csv, custom with { LoadFile = new LoadFileSettings { Delimiters = "csv" } }, Catalog());

        foreach (var result in new[] { a, b })
        {
            result.CanImport.Should().BeTrue();
            var row = result.Rows.Single();
            row.Cell("BegBates", "Control Number").Value!.GetValue<string>().Should().Be("PROD-000001");
            row.Cell("BegBates", "Beg Bates").Value!.GetValue<string>().Should().Be("PROD-000001");
            row.Cell("Date Sent").Value!.GetValue<string>().Should().Be("2019-07-04T00:00:00Z");
            row.Cell("All Custodians").Value!.ToJsonString().Should().Be("""["A","B"]""");
        }

        a.File.Column.Should().Be(new DelimiterInfo("¶", "U+00B6", 182));
    }

    [Fact]
    public async Task Unusable_delimiter_settings_are_reported_without_parsing()
    {
        var profile = new ImportProfileDefinition { LoadFile = new LoadFileSettings { Delimiters = "custom", Column = "abc", Quote = "254", Newline = "254" } };

        var result = await PreviewAsync(MessyDat(), profile, Catalog());

        result.CanImport.Should().BeFalse();
        result.Rows.Should().BeEmpty();
        result.Issues.Select(i => i.Code).Should().Contain("invalid-delimiter");
    }

    [Fact]
    public async Task Duplicate_headers_are_a_preflight_error_that_blocks_the_import()
    {
        var dat = Concordance(["DOCID", "Custodian", "custodian"], ["A1", "x", "y"]);

        var result = await PreviewAsync(dat, null, Catalog());

        result.CanImport.Should().BeFalse();
        result.File.ParserIssues.Should().Contain(i => i.Code == "duplicate-header");
    }

    [Fact]
    public async Task Header_less_files_get_generated_column_names_that_can_be_mapped()
    {
        var dat = Concordance(["A1", "Smith"], ["A2", "Doe"]);
        var profile = new ImportProfileDefinition
        {
            LoadFile = new LoadFileSettings { FirstLineContainsFieldNames = false },
            Columns = [Map("Column1", FieldTarget(SystemFields.ControlNumber)), Map("Column2", NewField("Custodian", ImportFieldType.Keyword))],
        };

        var result = await PreviewAsync(dat, profile, Catalog());

        result.File.Header.Should().Equal("Column1", "Column2");
        result.Rows.Select(r => r.ControlNumber).Should().Equal("A1", "A2");
        result.Rows[1].Cell("Column2").Value!.GetValue<string>().Should().Be("Doe");
    }

    [Fact]
    public void Practitioner_delimiter_codes_parse()
    {
        LoadFileSettingsResolver.TryParseDelimiter("020", out var dc4).Should().BeTrue();
        dc4.Should().Be('\u0014');
        LoadFileSettingsResolver.TryParseDelimiter("254", out var thorn).Should().BeTrue();
        thorn.Should().Be('þ');
        LoadFileSettingsResolver.TryParseDelimiter("U+00AE", out var reg).Should().BeTrue();
        reg.Should().Be('®');
        LoadFileSettingsResolver.TryParseDelimiter(";", out var semi).Should().BeTrue();
        semi.Should().Be(';');
        LoadFileSettingsResolver.TryParseDelimiter("abc", out _).Should().BeFalse();
        LoadFileSettingsResolver.TryParseDelimiter("99999", out _).Should().BeFalse();
        DelimiterProfile.TryGetPreset("concordance", out _).Should().BeTrue();
    }
}
