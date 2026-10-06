using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

/// <summary>E09-T05 / Q-48 rules that need no database: layout pre-ticking, job parameters and value text.</summary>
public class CodingPropagationRulesTests
{
    private const int Responsive = 2001;
    private const int Confidentiality = 2002;
    private const int Custodian = 2003;
    private const int Issues = 2004;

    private static readonly FieldCatalog Catalog = new(
        [
            new FieldDefinition { FieldId = Responsive, Name = "Responsive", Type = FieldType.Boolean, Storage = FieldStorage.Coding },
            new FieldDefinition
            {
                FieldId = Confidentiality, Name = "Confidentiality", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding,
                IsSecurityAffecting = true, SecurityClass = SecurityClass.ConfidentialityDesignation,
            },
            new FieldDefinition { FieldId = Custodian, Name = "Custodian", Type = FieldType.Keyword, Storage = FieldStorage.Metadata },
            new FieldDefinition { FieldId = Issues, Name = "Issues", Type = FieldType.MultiChoice, Storage = FieldStorage.Coding, IsMultiValue = true },
        ],
        [
            new Choice { FieldId = Issues, ChoiceId = 31, Name = "Pricing", SortOrder = 2 },
            new Choice { FieldId = Issues, ChoiceId = 32, Name = "Supply", SortOrder = 1 },
        ]);

    [Fact]
    public void Only_editable_non_security_coding_fields_apply_to_the_family_by_default()
    {
        var layout = new CodingLayout
        {
            Name = "Review",
            Sections =
            {
                new CodingLayoutSection
                {
                    SectionId = Guid.NewGuid(),
                    Title = "Main",
                    Fields =
                    {
                        new CodingLayoutField { FieldId = Responsive, ApplyToFamilyByDefault = true },
                        new CodingLayoutField { FieldId = Confidentiality, ApplyToFamilyByDefault = true },
                        new CodingLayoutField { FieldId = Custodian, IsReadOnly = true, ApplyToFamilyByDefault = true },
                    },
                },
            },
        };

        CodingLayoutValidator.ValidateStructure(layout, Catalog)
            .Where(e => e.Code == "apply-to-family-not-allowed")
            .Select(e => e.Field)
            .Should().Equal(FieldKey.For(Confidentiality), FieldKey.For(Custodian));
    }

    [Fact]
    public void Propagation_job_parameters_round_trip_and_mass_edit_jobs_have_none()
    {
        var origin = Guid.CreateVersion7();
        var propagation = new PropagationJobParameters(Guid.CreateVersion7(), Guid.CreateVersion7(), new Dictionary<int, Guid> { [Responsive] = origin });
        var json = BulkCodingParameters.ToJson([CodingFieldOperation.Set(Responsive, JsonValue.Create(true))], false, propagation);

        var parsed = BulkCodingParameters.Propagation(json)!;
        parsed.PreviewId.Should().Be(propagation.PreviewId);
        parsed.SourceDocumentId.Should().Be(propagation.SourceDocumentId);
        parsed.OriginEventIds.Should().Equal(new Dictionary<int, Guid> { [Responsive] = origin });
        BulkCodingParameters.Parse(json).Single().Kind.Should().Be(CodingOperationKind.Set);

        BulkCodingParameters.Propagation(BulkCodingParameters.ToJson([CodingFieldOperation.Clear(Responsive)], false)).Should().BeNull();
        var broken = json.DeepClone().AsObject();
        broken["propagation"]!["originEventIds"]![Responsive.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "nope";
        FluentActions.Invoking(() => BulkCodingParameters.Propagation(broken)).Should().Throw<FormatException>();
    }

    [Fact]
    public void Values_read_as_reviewers_see_them()
    {
        CodingValueText.Format(Catalog.Find(Responsive)!, JsonValue.Create(true), []).Should().Equal("Yes");
        CodingValueText.Format(Catalog.Find(Issues)!, new JsonArray(31, 32), Catalog.ChoicesOf(Issues)).Should().Equal("Supply", "Pricing");
        CodingValueText.Format(Catalog.Find(Responsive)!, null, []).Should().BeEmpty();
    }
}
