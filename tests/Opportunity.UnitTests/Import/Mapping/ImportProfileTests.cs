using AwesomeAssertions;

using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Import.Mapping;

using static Opportunity.UnitTests.Import.Mapping.MappingTestSupport;

namespace Opportunity.UnitTests.Import.Mapping;

/// <summary>E08-T02 import profiles: saved, exported as JSON, re-applied to later loads and copied between workspaces.</summary>
public class ImportProfileTests
{
    private static readonly string[] FirstVolumeHeader = ["BEGDOC", "ENDDOC", "CUSTODIAN", "AllCustodians", "DATESENT", "TIMESENT", "Vendor Flag"];

    private static string FirstVolume() => Concordance(
        FirstVolumeHeader,
        ["VOL001-0001", "VOL001-0002", "Smith", "Smith; Doe; Smith", "07/04/2019", "09:30:00", "Y"]);

    private static async Task<ImportProfileDefinition> SavedProfileFromFirstVolumeAsync()
    {
        var first = await PreviewAsync(FirstVolume(), new ImportProfileDefinition
        {
            Parsing = new ParsingDefaults { SourceTimeZone = "America/Chicago" },
            Columns = [Map("Vendor Flag", NewField("Vendor Flag", ImportFieldType.Boolean))],
        }, Catalog());
        first.CanImport.Should().BeTrue();

        // "Save as Import profile": the effective profile round-trips through its stored JSON.
        var json = ImportProfileRules.Serialize(first.EffectiveProfile);
        return ImportProfileRules.Deserialize(json);
    }

    [Fact]
    public void Profiles_store_as_camel_case_json_with_enum_names_and_round_trip()
    {
        ImportProfileDefinition profile = new()
        {
            Mode = ImportMode.AppendOverlay,
            ControlNumberPrefix = "P1-",
            LoadFile = new LoadFileSettings { Delimiters = "custom", Column = "020", Quote = "254", Newline = "174", DatEncoding = "utf-16le" },
            Columns =
            [
                Map("ParentID", Structural(StructuralTarget.ParentId)),
                Map("DateSent", new ColumnParsing { DateFormats = ["DD/MM/YYYY"], TimeColumn = "TimeSent", SourceTimeZone = "Europe/London" },
                    FieldTarget(SystemFields.DateSent, "Date Sent")),
            ],
        };

        var json = ImportProfileRules.Serialize(profile);

        json.Should().Contain("\"kind\":\"structural\"").And.Contain("\"structural\":\"parentId\"").And.Contain("\"mode\":\"appendOverlay\"");
        var back = ImportProfileRules.Deserialize(json);
        ImportProfileRules.Serialize(back).Should().Be(json);
        back.Columns[1].Parsing!.TimeColumn.Should().Be("TimeSent");
    }

    [Fact]
    public async Task A_saved_profile_re_applies_to_a_later_load_with_the_same_results()
    {
        var saved = await SavedProfileFromFirstVolumeAsync();
        // The first load created the proposed fields; the second load finds them.
        var catalog = Catalog(
            Custom(1000, "Custodian", FieldType.Keyword),
            Custom(1002, "Vendor Flag", FieldType.Boolean));
        var second = Concordance(FirstVolumeHeader, ["VOL002-0001", "VOL002-0001", "Doe", "Doe;Doe", "12/01/2019", "11:00:00", "N"]);

        var result = await PreviewAsync(second, saved with { Columns = [.. saved.Columns] }, catalog, new MappingPreviewOptions { AutoMap = false });

        result.CanImport.Should().BeTrue();
        result.MissingColumns.Should().BeEmpty();
        result.NewColumns.Should().BeEmpty();
        result.Column("BEGDOC").Targets.Select(t => t.Label).Should().Equal("Control Number", "Beg Bates");
        result.Column("CUSTODIAN").Targets.Single().Resolution.Should().Be(TargetResolution.ExistingField);
        result.Column("TIMESENT").Status.Should().Be(ColumnStatus.MergedIntoDate);
        var row = result.Rows.Single();
        row.Cell("DATESENT").Value!.GetValue<string>().Should().Be("2019-12-01T17:00:00Z");
        row.Cell("AllCustodians").Value!.ToJsonString().Should().Be("""["Doe"]""");
        row.Cell("Vendor Flag").Value!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Re_applying_to_a_file_with_different_headers_lists_missing_and_new_columns_and_keeps_mappings()
    {
        var saved = await SavedProfileFromFirstVolumeAsync();
        var renamed = Concordance(
            ["BEGDOC", "ENDDOC", "Custodian Name", "AllCustodians", "DATESENT", "Extra Column"],
            ["VOL003-0001", "VOL003-0001", "Roe", "Roe", "01/02/2020", "x"]);

        var result = await PreviewAsync(renamed, saved, Catalog());

        result.MissingColumns.Should().BeEquivalentTo(["CUSTODIAN", "TIMESENT", "Vendor Flag"]);
        result.NewColumns.Should().BeEquivalentTo(["Custodian Name", "Extra Column"]);
        result.Issues.Should().Contain(i => i.Code == "column-missing" && i.Column == "CUSTODIAN");
        result.Issues.Should().Contain(i => i.Code == "time-column-missing" && i.Column == "DATESENT");
        // New columns are auto-mapped where they can be; the missing rows stay in the profile for the next volume.
        result.Column("Custodian Name").Targets.Single().MatchedBy.Should().Be(MatchKind.Alias);
        result.Column("Extra Column").Status.Should().Be(ColumnStatus.Unmapped);
        result.EffectiveProfile.Columns.Select(c => c.Column).Should().Contain(["CUSTODIAN", "TIMESENT", "Vendor Flag"]);
        result.EffectiveProfile.Columns.Single(c => c.Column == "CUSTODIAN").Targets.Should().ContainSingle();
    }

    [Fact]
    public void A_profile_copied_to_another_workspace_resolves_custom_fields_by_name()
    {
        ImportProfileDefinition profile = new()
        {
            Columns = [Map("DOCID", FieldTarget(SystemFields.ControlNumber, "Control Number")), Map("CUST", FieldTarget(1000, "Custodian"))],
        };
        var other = Catalog(Custom(1000, "Matter Code", FieldType.Keyword), Custom(1042, "custodian", FieldType.Keyword));

        var mapping = MappingCompiler.Compile(profile, ["DOCID", "CUST"], other);

        var target = mapping.Columns[1].Targets.Single();
        target.FieldId.Should().Be(1042);
        target.Resolution.Should().Be(TargetResolution.ResolvedByName);
        mapping.EffectiveProfile.Columns[1].Targets.Single().FieldId.Should().Be(1042);
        mapping.Issues.Should().Contain(i => i.Code == "field-resolved-by-name");
        mapping.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void A_renamed_field_is_still_found_by_id_and_an_unknown_field_is_an_error_not_a_silent_drop()
    {
        ImportProfileDefinition profile = new()
        {
            Columns = [Map("DOCID", FieldTarget(SystemFields.ControlNumber)), Map("CUST", FieldTarget(1000, "Custodian")), Map("GONE", FieldTarget(1500, "Retired"))],
        };

        var mapping = MappingCompiler.Compile(profile, ["DOCID", "CUST", "GONE"], Catalog(Custom(1000, "Custodian (primary)", FieldType.Keyword)));

        mapping.Columns[1].Targets.Single().Resolution.Should().Be(TargetResolution.ResolvedById);
        mapping.Columns[2].Targets.Single().Resolution.Should().Be(TargetResolution.Unresolved);
        mapping.Issues.Should().Contain(i => i.Code == "unknown-field" && i.Column == "GONE" && i.Severity == MappingIssueSeverity.Error);
    }

    [Fact]
    public void A_new_field_whose_name_exists_with_another_type_is_a_conflict()
    {
        ImportProfileDefinition profile = new() { Columns = [Map("DOCID", FieldTarget(1)), Map("Flag", NewField("Flag", ImportFieldType.Boolean))] };

        var mapping = MappingCompiler.Compile(profile, ["DOCID", "Flag"], Catalog(Custom(1000, "flag", FieldType.Keyword)));

        mapping.Issues.Should().Contain(i => i.Code == "new-field-conflict");
    }

    [Fact]
    public void Profiles_to_save_are_validated_without_a_file()
    {
        ImportProfileRules.Validate(new ImportProfileWrite { Name = "Vendor volumes", Definition = new ImportProfileDefinition() }).Should().BeEmpty();

        var errors = ImportProfileRules.Validate(new ImportProfileWrite
        {
            Name = " ",
            Definition = new ImportProfileDefinition
            {
                Parsing = new ParsingDefaults { SourceTimeZone = "Nowhere/Land" },
                LoadFile = new LoadFileSettings { Delimiters = "vendor-x", DatEncoding = "ebcdic" },
                Columns =
                [
                    Map("A", new MappingTarget { Kind = MappingTargetKind.Field }),
                    Map("a", Structural(StructuralTarget.GroupId)),
                    Map("B", new ColumnParsing { SourceTimeZone = "X/Y", MultiValueDelimiter = "ab" },
                        new MappingTarget { Kind = MappingTargetKind.NewField, NewField = new NewFieldSpec { Name = "Amount", Type = ImportFieldType.Decimal, DecimalPrecision = 40 } }),
                ],
            },
        }).Select(e => e.Code).ToList();

        errors.Should().Contain(["invalid-name", "invalid-target", "duplicate-column-mapping", "invalid-time-zone", "invalid-delimiter",
            "unknown-delimiter-preset", "unknown-encoding"]);
        ImportProfileRules.Validate(new ImportProfileWrite { Name = "x" }).Should().ContainSingle(e => e.Code == "required");
    }

    [Fact]
    public void Only_control_number_can_be_the_overlay_key_and_it_must_be_mapped()
    {
        var catalog = Catalog(Custom(1000, "Doc Key", FieldType.Keyword));
        ImportProfileDefinition overlay = new()
        {
            Mode = ImportMode.Overlay,
            Overlay = new OverlaySettings { KeyFieldId = 1000 },
            Columns = [Map("KEY", FieldTarget(1000, "Doc Key"))],
        };

        // Rows are matched by control number; no other field is declared unique (E08-T07).
        MappingCompiler.Compile(overlay, ["KEY"], catalog, new MappingOptions { AutoMap = false })
            .Issues.Select(i => i.Code).Should().Contain(["overlay-key-not-unique", "overlay-key-unmapped"]);
        MappingCompiler.Compile(overlay with { Overlay = new OverlaySettings { KeyField = "Doc Key" } }, ["KEY"], catalog, new MappingOptions { AutoMap = false })
            .Issues.Should().Contain(i => i.Code == "overlay-key-not-unique");
        foreach (var key in new[] { "ControlNumber", "Control Number", "controlnumber", "1", null })
        {
            MappingCompiler.IsControlNumberKey(new OverlaySettings { KeyField = key }).Should().BeTrue(key);
        }

        MappingCompiler.Compile(overlay with { Overlay = new OverlaySettings() }, ["KEY"], catalog, new MappingOptions { AutoMap = false })
            .Issues.Select(i => i.Code).Should().Contain("overlay-key-unmapped").And.NotContain("overlay-key-not-unique");
    }
}
