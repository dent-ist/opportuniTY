using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

/// <summary>E13-T01: the privilege system fields and the basis rule (AC 1), independent of the database.</summary>
public class PrivilegeFieldsTests
{
    private static readonly Guid Ws = Guid.NewGuid();

    private static FieldCatalog Catalog(bool provisioned = true)
    {
        var fields = PrivilegeFields.Definitions
            .Where(d => provisioned || d.FieldId != PrivilegeFields.Basis)
            .Select(d => new FieldDefinition
            {
                WorkspaceId = Ws,
                FieldId = d.FieldId,
                Name = d.Name,
                Type = d.Type,
                IsMultiValue = d.IsMultiValue,
                Storage = FieldStorage.Coding,
                IsSystem = true,
                IsSecurityAffecting = true,
                SecurityClass = SecurityClass.PrivilegeStatus,
            });
        var choices = PrivilegeFields.BuiltInChoices.Select((c, i) => new Choice
        {
            WorkspaceId = Ws,
            FieldId = c.FieldId,
            ChoiceId = 100 + i,
            Name = c.Name,
            SortOrder = i,
            SystemKey = c.Key,
        });
        return new FieldCatalog(fields, choices);
    }

    private static JsonValue Status(FieldCatalog catalog, string key) => JsonValue.Create(PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, key)!.Value);

    [Theory]
    [InlineData(PrivilegeFields.Keys.Withhold, true)]
    [InlineData(PrivilegeFields.Keys.Redact, true)]
    [InlineData(PrivilegeFields.Keys.NotPrivileged, false)]
    [InlineData(PrivilegeFields.Keys.NeedsSecondLevelReview, false)]
    public void Withhold_and_Redact_need_a_basis(string key, bool needsBasis)
    {
        var catalog = Catalog();
        var basis = new JsonArray(JsonValue.Create(PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Basis, PrivilegeFields.Keys.WorkProduct)!.Value));

        var error = PrivilegeFields.ValidateBasis(catalog, Status(catalog, key), null);
        (error is not null).Should().Be(needsBasis);
        PrivilegeFields.ValidateBasis(catalog, Status(catalog, key), basis).Should().BeNull();
        if (error is not null)
        {
            error.Field.Should().Be("f38");
            error.Code.Should().Be(PrivilegeFields.BasisRequiredCode);
            error.Message.Should().Be("Privilege Basis is required when Privilege Status is Withhold or Redact.");
        }
    }

    [Fact]
    public void Built_in_choices_are_found_by_key_whatever_their_id_or_name()
    {
        var catalog = Catalog();
        PrivilegeFields.BasisRequiredStatuses(catalog).Should().BeEquivalentTo(
            [PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.Withhold)!.Value,
             PrivilegeFields.ChoiceId(catalog, PrivilegeFields.Status, PrivilegeFields.Keys.Redact)!.Value]);
        PrivilegeFields.ValidateBasis(catalog, null, null).Should().BeNull("no privilege call yet");
        PrivilegeFields.ValidateBasis(Catalog(provisioned: false), Status(catalog, PrivilegeFields.Keys.Withhold), null)
            .Should().BeNull("a workspace without the system basis field has nothing to require");
    }

    [Fact]
    public void The_privilege_fields_follow_the_system_field_rules()
    {
        PrivilegeFields.Definitions.Select(d => d.FieldId).Should().Equal(37, 38, 39, 40, 41);
        PrivilegeFields.Definitions.Select(d => d.FieldId).Should().NotIntersectWith(SystemFields.Create(Ws).Select(f => f.FieldId));
        PrivilegeFields.ReservedSlots.Keys.Should().BeEquivalentTo(PrivilegeFields.Definitions.Select(d => d.FieldId));
        PrivilegeFields.ReservedSlots.Values.Should().OnlyHaveUniqueItems();
        foreach (var (fieldId, slot) in PrivilegeFields.ReservedSlots)
        {
            var definition = PrivilegeFields.Definitions.Single(d => d.FieldId == fieldId);
            var kind = FieldRules.SlotKind(definition.Type, definition.Type == FieldType.Text ? TextAnalysis.Prose : null);
            slot.Should().StartWith(kind + ".");
            int.Parse(slot[(kind.Length + 2)..], System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeLessThanOrEqualTo(FieldRules.CodingSlotBudgets[kind]);
        }

        foreach (var definition in Catalog().Fields)
        {
            FieldRules.ValidateDefinition(definition).Should().BeEmpty(definition.Name);
        }
    }
}
