using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

public class CodingLayoutValidatorTests
{
    private const int Privileged = 1001;
    private const int PrivilegeBasis = 1002;
    private const int Notes = 1003;
    private const int Custodian = 1004;
    private const int Hot = 1005;
    private const int Yes = 11;
    private const int No = 12;
    private const int AttorneyClient = 21;
    private const int WorkProduct = 22;

    private static readonly FieldCatalog Catalog = new(
        [
            Coding(Privileged, FieldType.SingleChoice),
            Coding(PrivilegeBasis, FieldType.MultiChoice),
            Coding(Notes, FieldType.Text),
            new FieldDefinition { FieldId = Custodian, Name = "Custodian", Type = FieldType.Keyword, Storage = FieldStorage.Metadata },
            Coding(Hot, FieldType.Boolean),
        ],
        [
            new Choice { FieldId = Privileged, ChoiceId = Yes, Name = "Yes" },
            new Choice { FieldId = Privileged, ChoiceId = No, Name = "No" },
            new Choice { FieldId = PrivilegeBasis, ChoiceId = AttorneyClient, Name = "Attorney-Client" },
            new Choice { FieldId = PrivilegeBasis, ChoiceId = WorkProduct, Name = "Work Product" },
        ]);

    [Fact]
    public void Privilege_basis_is_required_only_when_privileged_is_yes()
    {
        var layout = PrivilegeLayout();
        CodingLayoutValidator.ValidateStructure(layout, Catalog).Should().BeEmpty();

        Submit(layout, (Privileged, JsonValue.Create(No))).Should().BeEmpty();
        Submit(layout, (Privileged, JsonValue.Create(Yes))).Should().ContainSingle()
            .Which.Should().Be(new FieldError("f1002", "required", "A value is required."));
        Submit(layout, (Privileged, JsonValue.Create(Yes)), (PrivilegeBasis, new JsonArray(AttorneyClient))).Should().BeEmpty();

        // Privileged itself is required regardless.
        Submit(layout).Select(e => e.Field).Should().Equal("f1001");
    }

    [Fact]
    public void Nested_conditions_hide_fields_whose_controlling_field_is_hidden()
    {
        var layout = PrivilegeLayout();
        layout.Sections[0].Fields.Add(new CodingLayoutField
        {
            FieldId = Notes,
            IsRequired = true,
            VisibleWhen = new VisibilityCondition(PrivilegeBasis, [WorkProduct]),
        });
        CodingLayoutValidator.ValidateStructure(layout, Catalog).Should().BeEmpty();

        // Basis is hidden (Privileged = No), so Notes is hidden too even though a stale basis value is present.
        Submit(layout, (Privileged, JsonValue.Create(No)), (PrivilegeBasis, new JsonArray(WorkProduct))).Should().BeEmpty();
        Submit(layout, (Privileged, JsonValue.Create(Yes)), (PrivilegeBasis, new JsonArray(WorkProduct)))
            .Select(e => e.Field).Should().Equal("f1003");
    }

    [Fact]
    public void Boolean_conditions_compare_with_the_value()
    {
        var layout = new CodingLayout
        {
            Name = "Hot docs",
            Sections =
            {
                new CodingLayoutSection
                {
                    SectionId = Guid.NewGuid(),
                    Title = "Main",
                    Fields =
                    {
                        new CodingLayoutField { FieldId = Hot },
                        new CodingLayoutField { FieldId = Notes, IsRequired = true, VisibleWhen = new VisibilityCondition(Hot, BooleanValue: true) },
                    },
                },
            },
        };
        CodingLayoutValidator.ValidateStructure(layout, Catalog).Should().BeEmpty();
        Submit(layout, (Hot, JsonValue.Create(false))).Should().BeEmpty();
        Submit(layout, (Hot, JsonValue.Create(true))).Should().ContainSingle();
    }

    [Fact]
    public void Invalid_layouts_are_rejected_with_field_level_errors()
    {
        var section = new CodingLayoutSection
        {
            SectionId = Guid.NewGuid(),
            Title = "Main",
            Fields =
            {
                new CodingLayoutField { FieldId = Custodian, IsRequired = true },
                new CodingLayoutField { FieldId = Notes, VisibleWhen = new VisibilityCondition(Custodian, [Yes]) },
                new CodingLayoutField { FieldId = Privileged, VisibleWhen = new VisibilityCondition(PrivilegeBasis, [AttorneyClient]) },
                new CodingLayoutField { FieldId = PrivilegeBasis, VisibleWhen = new VisibilityCondition(Privileged, [Yes]) },
                new CodingLayoutField { FieldId = Hot, VisibleWhen = new VisibilityCondition(Privileged, [AttorneyClient]) },
                new CodingLayoutField { FieldId = 4242 },
                new CodingLayoutField { FieldId = Notes },
            },
        };
        var layout = new CodingLayout { Name = "Broken", Sections = { section }, Roles = { "reviewer", "reviewer" } };

        var errors = CodingLayoutValidator.ValidateStructure(layout, Catalog).Select(e => (e.Field, e.Code)).ToList();

        errors.Should().Contain(("f1004", "not-editable"));
        errors.Should().Contain(("f1004", "required-not-editable"));
        errors.Should().Contain(("f1003", "invalid-condition-field"));
        errors.Should().Contain(("f1001", "condition-cycle"));
        errors.Should().Contain(("f1005", "invalid-condition-value"));
        errors.Should().Contain(("f4242", "unknown-field"));
        errors.Should().Contain(("f1003", "duplicate-field"));
        errors.Should().Contain(("roles", "invalid-role"));
    }

    [Fact]
    public void Submissions_may_only_write_editable_layout_fields()
    {
        var layout = PrivilegeLayout();
        layout.Sections[0].Fields.Add(new CodingLayoutField { FieldId = Custodian, IsReadOnly = true });
        var values = new Dictionary<int, JsonNode?> { [Privileged] = JsonValue.Create(No) };

        CodingLayoutValidator.ValidateSubmission(layout, values, [Privileged, Custodian, Hot])
            .Select(e => (e.Field, e.Code)).Should().Equal(("f1004", "not-in-layout"), ("f1005", "not-in-layout"));
    }

    private static IReadOnlyList<FieldError> Submit(CodingLayout layout, params (int FieldId, JsonNode? Value)[] values)
    {
        var map = values.ToDictionary(v => v.FieldId, v => v.Value);
        return CodingLayoutValidator.ValidateSubmission(layout, map, [.. map.Keys]);
    }

    private static CodingLayout PrivilegeLayout() => new()
    {
        Name = "Privilege review",
        Roles = { "privilege-reviewer" },
        Sections =
        {
            new CodingLayoutSection
            {
                SectionId = Guid.NewGuid(),
                Title = "Privilege",
                Fields =
                {
                    new CodingLayoutField { FieldId = Privileged, IsRequired = true },
                    new CodingLayoutField
                    {
                        FieldId = PrivilegeBasis,
                        IsRequired = true,
                        VisibleWhen = new VisibilityCondition(Privileged, [Yes]),
                    },
                },
            },
        },
    };

    private static FieldDefinition Coding(int id, FieldType type) => new()
    {
        FieldId = id,
        Name = "F" + id,
        Type = type,
        Storage = FieldStorage.Coding,
        IsMultiValue = type == FieldType.MultiChoice,
    };
}
