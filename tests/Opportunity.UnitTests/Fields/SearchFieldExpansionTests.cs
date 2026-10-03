using AwesomeAssertions;

using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

/// <summary>E09-T02: upstream dedupe/thread system fields are searchable; <c>custodian:</c> optionally includes All Custodians.</summary>
public class SearchFieldExpansionTests
{
    private static readonly Guid Workspace = Guid.Parse("0199a8a0-0000-7000-8000-00000000aaaa");

    private static readonly FieldDefinition Custodian = new()
    {
        WorkspaceId = Workspace,
        FieldId = 1000,
        Name = "Custodian",
        Type = FieldType.Keyword,
        Storage = FieldStorage.Metadata,
        IsSearchable = true,
        SearchSlot = "kw.s001",
    };

    private static FieldCatalog Catalog(params FieldDefinition[] custom) => new([.. SystemFields.Create(Workspace), .. custom], []);

    [Fact]
    public void Upstream_dedupe_and_thread_fields_are_searchable_system_fields_with_reserved_slots()
    {
        var fields = SystemFields.Create(Workspace).ToDictionary(f => f.FieldId);

        foreach (var (id, slot) in SystemFields.ReservedSlots)
        {
            var field = fields[id];
            field.IsSystem.Should().BeTrue();
            field.Storage.Should().Be(FieldStorage.Metadata);
            field.SearchSlot.Should().Be(slot);
            field.Capabilities.Should().Be(FieldRules.CapabilitiesForSlot(slot));
            slot.Should().StartWith(FieldRules.SlotKind(field.Type, field.TextAnalysis) + ".");
            int.Parse(slot[(slot.IndexOf(".s", StringComparison.Ordinal) + 2)..], System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeLessThanOrEqualTo(FieldRules.SlotBudgets[slot[..slot.IndexOf('.', StringComparison.Ordinal)]]);
        }

        SystemFields.ReservedSlots.Values.Should().OnlyHaveUniqueItems();
        fields[SystemFields.AllCustodians].IsMultiValue.Should().BeTrue();
        fields[SystemFields.AllCustodians].Capabilities.Should().HaveFlag(FieldCapabilities.Filterable).And.HaveFlag(FieldCapabilities.Aggregatable);
        fields[SystemFields.AllPaths].IsMultiValue.Should().BeTrue();
        fields[SystemFields.AllPaths].Capabilities.Should().HaveFlag(FieldCapabilities.FullText, "paths are tokenized identifiers (ADR-007 R5)");
        fields[SystemFields.ConversationTopic].Capabilities.Should().HaveFlag(FieldCapabilities.FullText);
    }

    [Fact]
    public void Email_thread_and_duplicate_group_are_groupable_columns()
    {
        var fields = SystemFields.Create(Workspace).ToDictionary(f => f.FieldId);

        fields[SystemFields.EmailThreadGroup].ColumnName.Should().Be("email_thread_id");
        fields[SystemFields.EmailThreadGroup].Capabilities.Should().HaveFlag(FieldCapabilities.Aggregatable).And.HaveFlag(FieldCapabilities.Sortable);
        fields[SystemFields.DuplicateGroup].ColumnName.Should().Be("duplicate_group_id");
        fields[SystemFields.DuplicateGroup].Capabilities.Should().HaveFlag(FieldCapabilities.Aggregatable);
        fields[SystemFields.DuplicatePrimary].ColumnName.Should().Be("is_duplicate_primary");
        fields[SystemFields.DuplicatePrimary].Type.Should().Be(FieldType.Boolean);
    }

    [Fact]
    public void Custodian_searches_only_itself_unless_the_option_adds_all_custodians()
    {
        var catalog = Catalog(Custodian);

        SearchFieldExpansion.Expand(catalog, Custodian).Should().Equal(Custodian);
        SearchFieldExpansion.Expand(catalog, Custodian, new SearchFieldOptions(CustodianIncludesAllCustodians: false)).Should().Equal(Custodian);
        SearchFieldExpansion.Expand(catalog, Custodian, new SearchFieldOptions(CustodianIncludesAllCustodians: true))
            .Select(f => f.FieldId).Should().Equal(1000, SystemFields.AllCustodians);
    }

    [Fact]
    public void The_option_touches_only_the_custodian_field()
    {
        var author = new FieldDefinition { FieldId = 1001, Name = "Author", Type = FieldType.Keyword, Storage = FieldStorage.Metadata, IsSearchable = true };
        var catalog = Catalog(Custodian, author);
        var options = new SearchFieldOptions(CustodianIncludesAllCustodians: true);

        SearchFieldExpansion.Expand(catalog, author, options).Should().Equal(author);
        var all = catalog.Find(SystemFields.AllCustodians)!;
        SearchFieldExpansion.Expand(catalog, all, options).Should().Equal(all);
    }

    [Theory]
    [InlineData("Custodian", "custodian")]
    [InlineData(" Primary Custodian ", "primary_custodian")]
    [InlineData("Date (Sent)", "date_sent")]
    [InlineData("All  Custodians", "all_custodians")]
    public void Default_query_names_follow_ADR_008_R11(string name, string expected) =>
        SearchFieldExpansion.DefaultQueryName(name).Should().Be(expected);
}
