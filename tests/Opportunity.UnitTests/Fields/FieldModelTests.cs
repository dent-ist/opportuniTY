using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

/// <summary>Canonical values, metadata validation, definition rules and capabilities (ADR-003, ADR-007).</summary>
public class FieldModelTests
{
    private static readonly Choice[] Choices =
    [
        new() { FieldId = 1005, ChoiceId = 1, Name = "Yes" },
        new() { FieldId = 1005, ChoiceId = 2, Name = "No" },
        new() { FieldId = 1005, ChoiceId = 3, Name = "Old", IsActive = false },
    ];

    public static TheoryData<FieldType, string, string?> ValidCanonical => new()
    {
        { FieldType.Text, "\"memo\"", "\"memo\"" },
        { FieldType.Keyword, "\"ABC\"", "\"ABC\"" },
        { FieldType.Integer, "42", "42" },
        { FieldType.Integer, "-9007199254740991", "-9007199254740991" },
        { FieldType.Decimal, "12.50", "12.5" },
        { FieldType.Date, "\"2025-03-01T14:05:00Z\"", "\"2025-03-01T14:05:00Z\"" },
        { FieldType.Boolean, "false", "false" },
        { FieldType.SingleChoice, "2", "2" },
        { FieldType.MultiChoice, "[2,1]", "[1,2]" },
        { FieldType.User, "\"0199a7c2-0000-7000-8000-000000000001\"", "\"0199a7c2-0000-7000-8000-000000000001\"" },
    };

    [Theory]
    [MemberData(nameof(ValidCanonical))]
    public void Typed_values_canonicalize(FieldType type, string json, string? expected)
    {
        var ok = FieldValues.TryCanonicalize(Field(type), JsonNode.Parse(json), Choices, true, out var canonical, out var error);
        ok.Should().BeTrue(error?.Message);
        canonical?.ToJsonString().Should().Be(expected);
    }

    public static TheoryData<FieldType, string, string> InvalidTyped => new()
    {
        { FieldType.Text, "42", "invalid-type" },
        { FieldType.Text, "\"\"", "empty-value" },
        { FieldType.Text, "[\"a\"]", "not-multi-value" },
        { FieldType.Keyword, "\"a\\u0001\"", "control-characters" },
        { FieldType.Integer, "1.5", "invalid-integer" },
        { FieldType.Integer, "9007199254740992", "invalid-integer" },
        { FieldType.Integer, "\"1\"", "invalid-integer" },
        { FieldType.Decimal, "\"1.5\"", "invalid-decimal" },
        { FieldType.Decimal, "1234567890123", "invalid-decimal" },
        { FieldType.Date, "\"2025-03-01\"", "invalid-date" },
        { FieldType.Date, "\"2025-03-01T14:05:00+01:00\"", "invalid-date" },
        { FieldType.Boolean, "\"Y\"", "invalid-boolean" },
        { FieldType.SingleChoice, "99", "unknown-choice" },
        { FieldType.SingleChoice, "3", "inactive-choice" },
        { FieldType.SingleChoice, "\"Yes\"", "invalid-choice" },
        { FieldType.MultiChoice, "[1,1]", "invalid-choice" },
        { FieldType.MultiChoice, "1", "invalid-choice" },
        { FieldType.User, "\"alice\"", "invalid-user" },
    };

    [Theory]
    [MemberData(nameof(InvalidTyped))]
    public void Invalid_typed_values_are_rejected_with_a_field_level_error(FieldType type, string json, string code)
    {
        FieldValues.TryCanonicalize(Field(type), JsonNode.Parse(json), Choices, true, out _, out var error).Should().BeFalse();
        error!.Code.Should().Be(code);
        error.Field.Should().Be("f1005");
    }

    [Fact]
    public void Inactive_choices_remain_valid_for_existing_values()
    {
        FieldValues.TryCanonicalize(Field(FieldType.SingleChoice), JsonValue.Create(3), Choices, forAssignment: false, out _, out _)
            .Should().BeTrue();
    }

    [Fact]
    public void Metadata_validation_is_keyed_by_field_id_and_rejects_coding_and_unknown_keys()
    {
        var text = Field(FieldType.Text, 1001);
        var count = Field(FieldType.Integer, 1002);
        var coding = Field(FieldType.Boolean, 1003);
        coding.Storage = FieldStorage.Coding;
        var deleted = Field(FieldType.Text, 1004);
        deleted.IsDeleted = true;
        var catalog = new FieldCatalog([text, count, coding, deleted], []);

        MetadataValidator.Validate("""{"f1001": "Alice", "f1002": 3}""", catalog).Should().BeEmpty();

        var errors = MetadataValidator.Validate(
            """{"f1001": null, "f1002": "3", "f1003": true, "f1004": "x", "f9999": 1, "Custodian": "x", "f01001": "y"}""", catalog);
        errors.Select(e => (e.Field, e.Code)).Should().BeEquivalentTo(
        [
            ("f1001", "null-value"),
            ("f1002", "invalid-integer"),
            ("f1003", "not-metadata-field"),
            ("f1004", "unknown-field"),
            ("f9999", "unknown-field"),
            ("Custodian", "unknown-field"),
            ("f01001", "unknown-field"),
        ]);

        MetadataValidator.Validate("[]", catalog).Single().Code.Should().Be("not-an-object");
        MetadataValidator.Validate("""{"f1002": 3.0}""", catalog).Single().Code.Should().Be("invalid-integer");
    }

    [Fact]
    public void Metadata_validation_requires_canonical_form()
    {
        var dec = Field(FieldType.Decimal, 1001);
        var multi = Field(FieldType.MultiChoice, 1005);
        var catalog = new FieldCatalog([dec, multi], Choices);

        MetadataValidator.Validate("""{"f1001": 1.50, "f1005": [2, 1]}""", catalog)
            .Select(e => e.Code).Should().Equal("not-canonical", "not-canonical");
    }

    [Fact]
    public void Definition_rules_enforce_type_attributes()
    {
        Codes(new FieldDefinition { Name = "Date", Type = FieldType.Date, Storage = FieldStorage.Metadata }).Should().Contain("date-precision");
        Codes(new FieldDefinition { Name = "Amount", Type = FieldType.Decimal, Storage = FieldStorage.Metadata, DecimalPrecision = 19, DecimalScale = 2 })
            .Should().Contain("decimal-precision");
        Codes(new FieldDefinition { Name = "Amount", Type = FieldType.Decimal, Storage = FieldStorage.Metadata, DecimalPrecision = 4, DecimalScale = 5 })
            .Should().Contain("decimal-precision");
        Codes(new FieldDefinition { Name = "Flag", Type = FieldType.Boolean, Storage = FieldStorage.Coding, IsMultiValue = true })
            .Should().Contain("multi-value-not-supported");
        Codes(new FieldDefinition { Name = "Issues", Type = FieldType.MultiChoice, Storage = FieldStorage.Coding })
            .Should().Contain("multi-choice-is-multi-value");
        Codes(new FieldDefinition { Name = "Custom", Type = FieldType.Text, Storage = FieldStorage.Column })
            .Should().Contain("column-storage-reserved");
        Codes(new FieldDefinition { Name = " ", Type = FieldType.Text, Storage = FieldStorage.Metadata }).Should().Contain("invalid-name");
    }

    [Fact]
    public void Security_affecting_fields_need_a_class_and_are_coding_or_metadata_fields()
    {
        Codes(new FieldDefinition
        {
            Name = "Privilege Status",
            Type = FieldType.SingleChoice,
            Storage = FieldStorage.Coding,
            IsSecurityAffecting = true,
            SecurityClass = SecurityClass.PrivilegeStatus,
        }).Should().BeEmpty();
        Codes(new FieldDefinition { Name = "Privilege Status", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding, IsSecurityAffecting = true })
            .Should().Contain("security-class");
        Codes(new FieldDefinition
        {
            Name = "Wall",
            Type = FieldType.Keyword,
            Storage = FieldStorage.Column,
            IsSystem = true,
            IsSecurityAffecting = true,
            SecurityClass = SecurityClass.EthicalWall,
        }).Should().Contain("security-affecting-storage");
    }

    [Theory]
    [InlineData(FieldType.Text, TextAnalysis.Prose, "txt", FieldCapabilities.Sortable | FieldCapabilities.FullText | FieldCapabilities.Highlightable)]
    [InlineData(FieldType.Text, TextAnalysis.Identifier, "idt", FieldCapabilities.FullText)]
    [InlineData(FieldType.Keyword, null, "kw", FieldCapabilities.Aggregatable | FieldCapabilities.Wildcard)]
    [InlineData(FieldType.Date, null, "dt", FieldCapabilities.Rangeable | FieldCapabilities.Sortable)]
    [InlineData(FieldType.MultiChoice, null, "ch", FieldCapabilities.Aggregatable)]
    [InlineData(FieldType.User, null, "usr", FieldCapabilities.Filterable)]
    public void Capabilities_follow_the_slot_kind(FieldType type, TextAnalysis? analysis, string kind, FieldCapabilities expected)
    {
        FieldRules.SlotKind(type, analysis).Should().Be(kind);
        FieldRules.CapabilitiesForSlot(FieldRules.Slot(kind, 1)).Should().HaveFlag(expected);
    }

    [Fact]
    public void Choice_and_user_fields_are_not_sortable_and_overflow_is_reduced()
    {
        FieldRules.CapabilitiesForSlot("ch.s001").HasFlag(FieldCapabilities.Sortable).Should().BeFalse();
        FieldRules.CapabilitiesForSlot("usr.s001").HasFlag(FieldCapabilities.Sortable).Should().BeFalse();
        FieldRules.CapabilitiesForSlot("overflow").Should().Be(FieldCapabilities.Filterable | FieldCapabilities.Wildcard | FieldCapabilities.Exists);
        FieldRules.CapabilitiesForSlot(null).Should().Be(FieldCapabilities.None);
    }

    [Fact]
    public void System_fields_use_reserved_ids_and_column_storage_except_upstream_metadata()
    {
        var fields = SystemFields.Create(Guid.NewGuid());
        fields.Should().OnlyContain(f => f.IsSystem && f.FieldId >= 1 && f.FieldId < 1000);
        fields.Where(f => f.Storage != FieldStorage.Column).Select(f => f.FieldId).Should().BeEquivalentTo(SystemFields.ReservedSlots.Keys);
        fields.Select(f => f.FieldId).Should().OnlyHaveUniqueItems();
        fields.Should().OnlyContain(f => FieldRules.ValidateDefinition(f).Count == 0);
    }

    [Theory]
    [InlineData("f1017", true, 1017)]
    [InlineData("f1", true, 1)]
    [InlineData("f01", false, 0)]
    [InlineData("F1", false, 0)]
    [InlineData("f", false, 0)]
    [InlineData("f-1", false, 0)]
    public void Field_keys_are_f_plus_decimal_id(string key, bool valid, int id)
    {
        FieldKey.TryParse(key, out var parsed).Should().Be(valid);
        parsed.Should().Be(id);
        if (valid)
        {
            FieldKey.For(id).Should().Be(key);
        }
    }

    private static string[] Codes(FieldDefinition definition) => [.. FieldRules.ValidateDefinition(definition).Select(e => e.Code)];

    private static FieldDefinition Field(FieldType type, int id = 1005) => new()
    {
        FieldId = id,
        Name = type.ToString(),
        Type = type,
        Storage = FieldStorage.Metadata,
        IsMultiValue = type == FieldType.MultiChoice,
        DatePrecision = type == FieldType.Date ? DatePrecision.DateTime : null,
        DecimalPrecision = type == FieldType.Decimal ? (short)12 : null,
        DecimalScale = type == FieldType.Decimal ? (short)2 : null,
    };
}
