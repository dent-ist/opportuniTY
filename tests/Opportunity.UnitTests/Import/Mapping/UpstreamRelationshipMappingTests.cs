using AwesomeAssertions;

using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Import.Mapping;

using static Opportunity.UnitTests.Import.Mapping.MappingTestSupport;

namespace Opportunity.UnitTests.Import.Mapping;

/// <summary>E09-T02: upstream duplicate and thread identifiers through auto-map, validation and extraction.</summary>
public class UpstreamRelationshipMappingTests
{
    private const string IndexHex = "01D9A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4" + "0011223344";

    private static readonly string[] Header =
    [
        "DOCID", "BEGATTACH", "PARENT_ID", "DupGroupID", "DedupeHash", "EmailHash", "EmailThreadGroup", "ConversationIndex",
        "ConversationTopic", "InclusiveEmail", "ThreadSortOrder", "AllCustodians", "DuplicateCustodians", "AllPaths", "DuplicatePaths",
    ];

    private static CompiledMapping Compile(ImportProfileDefinition? profile = null)
    {
        var mapping = MappingCompiler.Compile(profile, Header, Catalog());
        mapping.Issues.Where(i => i.Severity == MappingIssueSeverity.Error).Should().BeEmpty();
        return mapping;
    }

    private static string[] Row(
        string id = "ABC0001", string begAttach = "ABC0001", string parent = "", string group = "DG-1", string dedupe = "0A1B",
        string email = "", string thread = "T-1", string index = IndexHex) =>
        [id, begAttach, parent, group, dedupe, email, thread, index, "Budget", "Y", "0001", "Smith; Doe", "Doe", @"\\a\x.msg; \\b\x.msg", @"\\b\x.msg"];

    private static TargetBinding Target(CompiledMapping mapping, string column) => mapping.Columns.Single(c => c.Column == column).Targets.Single();

    [Fact]
    public void Upstream_dedupe_and_thread_columns_auto_map_to_structural_targets_and_system_fields()
    {
        var mapping = Compile();

        Target(mapping, "DupGroupID").Structural.Should().Be(StructuralTarget.DuplicateGroupId);
        Target(mapping, "DedupeHash").Structural.Should().Be(StructuralTarget.DedupeHash);
        Target(mapping, "EmailHash").Structural.Should().Be(StructuralTarget.EmailHash);
        Target(mapping, "EmailThreadGroup").Structural.Should().Be(StructuralTarget.EmailThreadId);
        Target(mapping, "ConversationIndex").FieldId.Should().Be(SystemFields.ConversationIndex);
        Target(mapping, "ConversationTopic").FieldId.Should().Be(SystemFields.ConversationTopic);
        Target(mapping, "InclusiveEmail").FieldId.Should().Be(SystemFields.InclusiveEmail);
        Target(mapping, "ThreadSortOrder").FieldId.Should().Be(SystemFields.ThreadSortOrder);
        Target(mapping, "AllCustodians").FieldId.Should().Be(SystemFields.AllCustodians);
        Target(mapping, "DuplicateCustodians").FieldId.Should().Be(SystemFields.DuplicateCustodians);
        Target(mapping, "AllPaths").FieldId.Should().Be(SystemFields.AllPaths);
        Target(mapping, "DuplicatePaths").FieldId.Should().Be(SystemFields.DuplicatePaths);
        mapping.Targets.Should().NotContain(t => t.CreatesField != null, "these are system fields now, not fields an import creates");
    }

    [Fact]
    public void Derived_relationship_columns_are_not_load_targets()
    {
        var targets = ImportTargetCatalog.List(Catalog());

        targets.Should().NotContain(t => t.Target.FieldId == SystemFields.DuplicateGroup || t.Target.FieldId == SystemFields.DuplicatePrimary
            || t.Target.FieldId == SystemFields.EmailThreadGroup);
        targets.Should().Contain(t => t.Target.Structural == StructuralTarget.EmailHash);

        var profile = new ImportProfileDefinition { Columns = [Map("DOCID", FieldTarget(SystemFields.ControlNumber)), Map("X", FieldTarget(SystemFields.EmailThreadGroup))] };
        MappingCompiler.Compile(profile, ["DOCID", "X"], Catalog()).Issues.Should().Contain(i => i.Code == "not-mappable");
    }

    [Fact]
    public void Values_are_typed_hashes_lower_cased_and_multi_values_split()
    {
        var row = Compile().Map(1, Row());

        row.HasErrors.Should().BeFalse();
        row.Cell("f" + SystemFields.AllCustodians)!.Value!.ToJsonString().Should().Be("""["Smith","Doe"]""");
        row.Cell("f" + SystemFields.AllPaths)!.Value!.ToJsonString().Should().Be("""["\\\\a\\x.msg","\\\\b\\x.msg"]""");
        row.Cell("f" + SystemFields.InclusiveEmail)!.Value!.GetValue<bool>().Should().BeTrue();
        row.Cell("f" + SystemFields.ConversationIndex)!.Value!.GetValue<string>().Should().Be(IndexHex);
        var dedupe = row.Cell("s:" + StructuralTarget.DedupeHash)!;
        dedupe.Value!.GetValue<string>().Should().Be("0a1b");
        dedupe.KeepRaw.Should().BeTrue("the original spelling is kept as the raw value");
    }

    [Theory]
    [InlineData("DedupeHash", "not-hex", "invalid-hash")]
    [InlineData("EmailHash", "zz11", "invalid-hash")]
    [InlineData("ConversationIndex", "01D9", "invalid-conversation-index")]
    public void Invalid_hashes_and_conversation_indexes_are_row_errors(string column, string value, string code)
    {
        var mapping = Compile();
        var values = Row();
        values[Array.IndexOf(Header, column)] = value;

        var row = mapping.Map(1, values);

        row.Cells.Single(c => c.Column.Column == column).Error!.Code.Should().Be(code);
    }

    [Fact]
    public void Overlong_upstream_identifiers_are_row_errors()
    {
        var row = Compile().Map(1, Row(group: new string('G', RelationshipIds.MaxUpstreamValueLength + 1)));

        row.Cells.Single(c => c.Column.Column == "DupGroupID").Error!.Code.Should().Be("identifier-too-long");
    }

    [Fact]
    public void Base64_conversation_indexes_are_stored_as_hex_with_a_warning()
    {
        var cell = Compile().Map(1, Row(index: "AdmhssPU5fYHGCk6S1xtfo+QobLD1AARIjNE")).Cell("f" + SystemFields.ConversationIndex)!;

        cell.Value!.GetValue<string>().Should().Be(IndexHex);
        cell.Warnings.Should().ContainSingle(w => w.Contains("base64", StringComparison.Ordinal));
    }

    [Fact]
    public void Extracted_values_feed_the_relationship_rules()
    {
        var mapping = Compile();

        var parent = UpstreamRelationshipExtractor.Extract(mapping, mapping.Map(1, Row(email: "FFEE")));
        parent.Should().Be(new UpstreamRelationshipValues
        {
            DuplicateGroup = "DG-1",
            DedupeHash = "0a1b",
            EmailHash = "ffee",
            EmailThreadGroup = "T-1",
            ConversationIndex = IndexHex,
            IsAttachment = false,
        });

        UpstreamRelationshipExtractor.Extract(mapping, mapping.Map(2, Row(id: "ABC0002", parent: "ABC0001"))).IsAttachment.Should().BeTrue();
        UpstreamRelationshipExtractor.Extract(mapping, mapping.Map(3, Row(id: "ABC0003", begAttach: "ABC0001", thread: ""))).IsAttachment.Should().BeTrue();
        UpstreamRelationshipExtractor.Extract(mapping, mapping.Map(4, Row(id: "ABC0004", begAttach: ""))).IsAttachment.Should().BeFalse();
    }

    [Fact]
    public void The_attachment_test_honours_the_control_number_prefix_and_options_come_from_the_profile()
    {
        var mapping = Compile(new ImportProfileDefinition
        {
            ControlNumberPrefix = "VOL1-",
            Relationships = new RelationshipSettings { DeriveEmailThreadFromConversationIndex = true },
        });

        UpstreamRelationshipExtractor.Extract(mapping, mapping.Map(1, Row())).IsAttachment.Should().BeFalse();
        UpstreamRelationshipExtractor.Options(mapping).DeriveEmailThreadFromConversationIndex.Should().BeTrue();
        UpstreamRelationshipExtractor.Options(Compile()).DeriveEmailThreadFromConversationIndex.Should().BeFalse();
    }
}
