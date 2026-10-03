using AwesomeAssertions;

using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Import.Mapping;

using static Opportunity.UnitTests.Import.Mapping.MappingTestSupport;

namespace Opportunity.UnitTests.Import.Mapping;

/// <summary>E08-T02 auto-map: exact and alias names, with the reason recorded; never coding or privilege fields.</summary>
public class AutoMapTests
{
    private static CompiledMapping Compile(string[] header, FieldCatalog? catalog = null, ImportProfileDefinition? profile = null) =>
        MappingCompiler.Compile(profile, header, catalog ?? Catalog());

    private static TargetBinding Single(CompiledMapping mapping, string column) => mapping.Columns.Single(c => c.Column == column).Targets.Single();

    [Theory]
    [InlineData("BEGDOC", MatchKind.Alias, "BEGDOC")]
    [InlineData("BegBates", MatchKind.Alias, "BegBates")]
    [InlineData("Control Number", MatchKind.ExactName, null)]
    [InlineData("CONTROL_NUMBER", MatchKind.NormalizedName, null)]
    [InlineData("DocID", MatchKind.Alias, "DocID")]
    public void Control_number_columns_auto_map_and_record_why(string header, MatchKind kind, string? alias)
    {
        var mapping = Compile([header, "Custodian"]);

        var target = mapping.Columns[0].Targets.Single(t => t.FieldId == SystemFields.ControlNumber);
        target.MatchedBy.Should().Be(kind);
        target.Alias.Should().Be(alias);
        mapping.Issues.Should().NotContain(i => i.Code == "control-number-unmapped");
    }

    [Fact]
    public void A_production_number_column_maps_to_both_control_number_and_beg_bates_when_it_is_the_only_identifier()
    {
        var mapping = Compile(["BEGDOC", "ENDDOC", "BEGATTACH", "ENDATTACH"]);

        mapping.Columns[0].Targets.Select(t => t.FieldId).Should().BeEquivalentTo([SystemFields.ControlNumber, SystemFields.BegBates]);
        Single(mapping, "ENDDOC").FieldId.Should().Be(SystemFields.EndBates);
        Single(mapping, "BEGATTACH").FieldId.Should().Be(SystemFields.BegAttach);
        Single(mapping, "ENDATTACH").FieldId.Should().Be(SystemFields.EndAttach);
        mapping.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void A_document_id_column_wins_control_number_and_the_bates_column_keeps_beg_bates()
    {
        var mapping = Compile(["BegBates", "EndBates", "DOCID"]);

        Single(mapping, "DOCID").FieldId.Should().Be(SystemFields.ControlNumber);
        Single(mapping, "BegBates").FieldId.Should().Be(SystemFields.BegBates);
        Single(mapping, "BegBates").MatchedBy.Should().Be(MatchKind.NormalizedName);
        mapping.Issues.Where(i => i.Severity == MappingIssueSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void Family_path_hash_and_upstream_columns_map_to_structural_targets()
    {
        var mapping = Compile(["ProdBeg", "Parent_ID", "Group Identifier", "NATIVELINK", "TEXTLINK", "Folder Path", "MD5Hash", "SHA1", "DupGroupID", "EmailThreadGroup"]);

        Single(mapping, "Parent_ID").Structural.Should().Be(StructuralTarget.ParentId);
        Single(mapping, "Group Identifier").Structural.Should().Be(StructuralTarget.GroupId);
        Single(mapping, "NATIVELINK").Structural.Should().Be(StructuralTarget.NativePath);
        Single(mapping, "TEXTLINK").Structural.Should().Be(StructuralTarget.TextPath);
        Single(mapping, "Folder Path").Structural.Should().Be(StructuralTarget.FolderPath);
        Single(mapping, "Folder Path").MatchedBy.Should().Be(MatchKind.ExactName);
        Single(mapping, "MD5Hash").FieldId.Should().Be(SystemFields.Md5);
        Single(mapping, "SHA1").FieldId.Should().Be(SystemFields.Sha1);
        Single(mapping, "DupGroupID").Structural.Should().Be(StructuralTarget.DuplicateGroupId);
        Single(mapping, "EmailThreadGroup").Structural.Should().Be(StructuralTarget.EmailThreadId);
    }

    [Theory]
    [InlineData("DATESENT", "TIMESENT")]
    [InlineData("Date Sent", "Time Sent")]
    [InlineData("SentDate", "SentTime")]
    public void Date_columns_auto_map_and_pick_up_their_companion_time_column(string date, string time)
    {
        var mapping = Compile(["DOCID", date, time]);

        var dateColumn = mapping.Columns[1];
        dateColumn.Targets.Single().FieldId.Should().Be(SystemFields.DateSent);
        dateColumn.TimeColumn.Should().Be(time);
        mapping.Columns[2].Status.Should().Be(ColumnStatus.MergedIntoDate);
        mapping.Columns[2].MergedInto.Should().Be(date);
        mapping.EffectiveProfile.Columns.Single(c => c.Column == date).Parsing!.TimeColumn.Should().Be(time);
    }

    [Fact]
    public void Well_known_columns_without_a_field_are_proposed_as_new_fields_and_existing_fields_are_reused()
    {
        var catalog = Catalog(Custom(1000, "Custodian", FieldType.Keyword));
        var mapping = Compile(["DOCID", "CUSTODIAN", "AllCustodians", "EMAIL_SUBJECT", "Some Vendor Column"], catalog);

        var custodian = Single(mapping, "CUSTODIAN");
        custodian.FieldId.Should().Be(1000);
        custodian.MatchedBy.Should().Be(MatchKind.ExactName);
        var all = Single(mapping, "AllCustodians");
        all.FieldId.Should().Be(SystemFields.AllCustodians, "All Custodians is a system field (ADR-009 R13)");
        all.MatchedBy.Should().Be(MatchKind.NormalizedName);
        Single(mapping, "EMAIL_SUBJECT").CreatesField!.Name.Should().Be("Subject");
        Single(mapping, "EMAIL_SUBJECT").Alias.Should().Be("EmailSubject");
        mapping.Columns.Single(c => c.Column == "Some Vendor Column").Status.Should().Be(ColumnStatus.Unmapped);
    }

    [Fact]
    public void Auto_map_never_targets_coding_or_privilege_fields()
    {
        var catalog = Catalog(
            Custom(1000, "Responsive", FieldType.SingleChoice, storage: FieldStorage.Coding),
            Custom(1001, "Privilege", FieldType.SingleChoice, storage: FieldStorage.Metadata, security: SecurityClass.PrivilegeStatus),
            Custom(1002, "Confidentiality", FieldType.Keyword, storage: FieldStorage.Coding, security: SecurityClass.ConfidentialityDesignation));
        var mapping = Compile(["DOCID", "Responsive", "PRIVILEGE", "Confidentiality"], catalog);

        mapping.Columns.Skip(1).Should().OnlyContain(c => c.Status == ColumnStatus.Unmapped && c.Targets.Count == 0);
    }

    [Fact]
    public void Explicitly_mapping_a_coding_field_is_an_error_unless_coding_overlay_is_allowed()
    {
        var catalog = Catalog(Custom(1000, "Responsive", FieldType.Keyword, storage: FieldStorage.Coding));
        ImportProfileDefinition profile = new() { Columns = [Map("DOCID", FieldTarget(1)), Map("Responsive", FieldTarget(1000, "Responsive"))] };

        Compile(["DOCID", "Responsive"], catalog, profile).Issues.Should().Contain(i => i.Code == "coding-field" && i.Severity == MappingIssueSeverity.Error);

        var allowed = Compile(["DOCID", "Responsive"], catalog, profile with { Overlay = new OverlaySettings { AllowCodingFieldOverlay = true } });
        allowed.Issues.Should().Contain(i => i.Code == "coding-field-overlay" && i.Severity == MappingIssueSeverity.Warning);
        allowed.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Profile_mappings_take_precedence_and_their_targets_are_not_auto_mapped_again()
    {
        ImportProfileDefinition profile = new() { Columns = [Map("PRODBEG", FieldTarget(SystemFields.ControlNumber)), new ColumnMapping { Column = "BEGDOC", Ignore = true }] };
        var mapping = Compile(["BEGDOC", "PRODBEG", "DocID"], profile: profile);

        mapping.Columns[0].Status.Should().Be(ColumnStatus.Ignored);
        Single(mapping, "PRODBEG").MatchedBy.Should().Be(MatchKind.Profile);
        mapping.Columns[2].Targets.Should().NotContain(t => t.FieldId == SystemFields.ControlNumber);
    }

    [Fact]
    public void Auto_map_can_be_turned_off_and_a_missing_control_number_is_a_profile_error()
    {
        var mapping = MappingCompiler.Compile(null, ["DOCID", "CUSTODIAN"], Catalog(), new MappingOptions { AutoMap = false });

        mapping.Columns.Should().OnlyContain(c => c.Status == ColumnStatus.Unmapped);
        mapping.Issues.Should().ContainSingle(i => i.Code == "control-number-unmapped");
    }

    [Fact]
    public void Computed_system_fields_are_not_targets()
    {
        var mapping = Compile(["DOCID", "Text Truncated", "Native Missing"]);

        mapping.Columns.Skip(1).Should().OnlyContain(c => c.Targets.Count == 0);
        ImportTargetCatalog.List(Catalog()).Should().NotContain(t => t.Target.FieldId == SystemFields.TextTruncated);
    }

    [Fact]
    public void The_target_list_puts_structural_targets_first_and_flags_coding_fields()
    {
        var catalog = Catalog(Custom(1000, "Responsive", FieldType.SingleChoice, storage: FieldStorage.Coding), Custom(1001, "Custodian", FieldType.Keyword));
        var targets = ImportTargetCatalog.List(catalog);

        targets.Take(10).Select(t => t.Label).Should().Equal(
            "Control Number", "Beg Bates", "End Bates", "Beg Attach", "End Attach", "Parent ID", "Family/Group ID", "Native Path", "Extracted Text Path", "Folder Path");
        targets.Single(t => t.Label == "Responsive").Should().Match<ImportTargetResource>(t => t.IsCodingField && !t.AutoMapEligible);
        targets.Single(t => t.Label == "Custodian").Aliases.Should().Contain("CustodianName");
        targets.Single(t => t.Label == "Control Number").Aliases.Should().Contain("BEGDOC");
    }

    [Fact]
    public void Two_columns_mapped_to_one_target_is_an_error()
    {
        ImportProfileDefinition profile = new() { Columns = [Map("A", FieldTarget(1)), Map("B", FieldTarget(1))] };

        Compile(["A", "B"], profile: profile).Issues.Should().Contain(i => i.Code == "duplicate-target");
    }
}
