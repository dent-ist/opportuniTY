using AwesomeAssertions;

using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Production.Productions;

namespace Opportunity.UnitTests.Productions;

/// <summary>
/// E12-T04: designations in the production specification (field, levels with legends, family rule), the family rule
/// itself, the endorsements of every produced page and the designation QC check.
/// </summary>
public class DesignationTests
{
    private const int Field = 1001;
    private const int None = 11;
    private const int Confidential = 12;
    private const int Aeo = 13;

    private static readonly FieldCatalog Catalog = new(
    [
        new FieldDefinition { FieldId = SystemFields.ControlNumber, Name = "Control Number", Type = FieldType.Text, Storage = FieldStorage.Metadata },
        new FieldDefinition
        {
            FieldId = Field, Name = "Confidentiality Designation", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding,
            IsSecurityAffecting = true, SecurityClass = SecurityClass.ConfidentialityDesignation,
        },
        new FieldDefinition { FieldId = 1002, Name = "Responsiveness", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding },
    ],
    [
        new Choice { FieldId = Field, ChoiceId = None, Name = "None", SortOrder = 0 },
        new Choice { FieldId = Field, ChoiceId = Confidential, Name = "CONFIDENTIAL", SortOrder = 1 },
        new Choice { FieldId = Field, ChoiceId = Aeo, Name = "HIGHLY CONFIDENTIAL – AEO", SortOrder = 2 },
        new Choice { FieldId = 1002, ChoiceId = 21, Name = "Responsive", SortOrder = 0 },
    ]);

    private static readonly HashSet<int> Unrestricted = [];

    private static readonly IReadOnlyList<DesignationLevel> Levels =
        [new(None, 0, string.Empty), new(Confidential, 1, "CONFIDENTIAL"), new(Aeo, 2, "HIGHLY CONFIDENTIAL – AEO")];

    [Fact]
    public void The_specification_states_the_designation_field_its_levels_and_the_family_rule()
    {
        var normalized = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC")), Catalog, Unrestricted,
            out var errors)!;

        errors.Should().BeEmpty();
        normalized.Specification.Designations.Should().BeEquivalentTo(new ProductionDesignationSettings(Field, DesignationFamilyRuleResource.HighestInFamily,
        [
            new ProductionDesignationLevel(None, string.Empty),
            new ProductionDesignationLevel(Confidential, "CONFIDENTIAL"),
            new ProductionDesignationLevel(Aeo, "HIGHLY CONFIDENTIAL – AEO"),
        ]), o => o.WithStrictOrdering());
        normalized.Specification.Endorsements.Should().BeEquivalentTo(new { FontSize = 10, ExpandCanvas = true, Margin = 18 });
        normalized.Json.Should().Contain("\"designations\":{\"fieldId\":1001,\"familyRule\":\"highestInFamily\"");
        ProductionSpecificationRules.Serialize(ProductionSpecificationRules.Deserialize(normalized.Json)).Should().Be(normalized.Json);
        ProductionSpecificationRules.StampsDesignation(normalized.Specification).Should().BeTrue("the default endorsements stamp the designation");

        var perDocument = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"),
            Designations: new ProductionDesignationSettings(FamilyRule: DesignationFamilyRuleResource.Document)), Catalog, Unrestricted, out _)!;
        perDocument.Sha256.Should().NotEqual(normalized.Sha256, "the family rule is part of the frozen specification");
        ProductionSpecificationRules.RuleOf(perDocument.Specification).Should().Be(DesignationFamilyRule.Document);
    }

    [Fact]
    public void Levels_can_be_reordered_and_given_legends_but_must_list_every_choice_once()
    {
        var custom = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"),
            Designations: new ProductionDesignationSettings(Field, null,
            [
                new(None), new(Confidential, " CONFIDENTIAL – SUBJECT TO PROTECTIVE ORDER "), new(Aeo, "ATTORNEYS' EYES ONLY"),
            ])), Catalog, Unrestricted, out var errors)!;
        errors.Should().BeEmpty();
        ProductionSpecificationRules.LevelsOf(custom.Specification).Select(l => (l.ChoiceId, l.Rank, l.Legend)).Should().Equal(
            (None, 0, string.Empty), (Confidential, 1, "CONFIDENTIAL – SUBJECT TO PROTECTIVE ORDER"), (Aeo, 2, "ATTORNEYS' EYES ONLY"));

        Refused(new ProductionDesignationSettings(Field, null, [new(None), new(Confidential)]), "specification.designations.levels");
        Refused(new ProductionDesignationSettings(Field, null, [new(None), new(Confidential), new(Aeo), new(Aeo)]), "specification.designations.levels[3]");
        Refused(new ProductionDesignationSettings(Field, null, [new(None), new(Confidential), new(21)]), "specification.designations.levels[2]");
        Refused(new ProductionDesignationSettings(Field, null, [new(None), new(Confidential), new(Aeo, new string('x', 101))]), "specification.designations.levels[2]");
        Refused(new ProductionDesignationSettings(1002), "specification.designations.fieldId");
        Refused(new ProductionDesignationSettings(FamilyRule: (DesignationFamilyRuleResource)7), "specification.designations.familyRule");
        var margin = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"),
            Endorsements: new ProductionEndorsementSettings(Margin: 73)), Catalog, Unrestricted, out var marginErrors);
        margin.Should().BeNull();
        marginErrors.Should().ContainKey("specification.endorsements.margin");
    }

    [Fact]
    public void A_hidden_or_missing_designation_field_leaves_documents_undesignated()
    {
        var hidden = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC")), Catalog, new HashSet<int> { Field },
            out _)!;
        hidden.Specification.Designations.Should().BeEquivalentTo(new ProductionDesignationSettings(null, DesignationFamilyRuleResource.HighestInFamily, []));

        var explicitHidden = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"),
            Designations: new ProductionDesignationSettings(Field)), Catalog, new HashSet<int> { Field }, out var errors);
        explicitHidden.Should().BeNull();
        errors.Should().ContainKey("specification.designations.fieldId");
    }

    [Fact]
    public void The_family_inherits_its_highest_designation_unless_the_rule_says_per_document_or_an_override_says_otherwise()
    {
        var family = Guid.NewGuid();
        var alone = Guid.NewGuid();
        DesignationMember[] members =
        [
            new(1, family, Confidential),
            new(2, family, Aeo),
            new(3, family, null),
            new(4, family, None),
            new(5, alone, None),
            new(6, alone, null),
        ];

        var inherited = DesignationResolver.Resolve(members, Levels, DesignationFamilyRule.HighestInFamily);
        inherited.Select(r => (r.ChoiceId, r.Legend, r.Source)).Should().Equal(
            (Aeo, "HIGHLY CONFIDENTIAL – AEO", DesignationSource.Family),
            (Aeo, "HIGHLY CONFIDENTIAL – AEO", DesignationSource.Document),
            (Aeo, "HIGHLY CONFIDENTIAL – AEO", DesignationSource.Family),
            (Aeo, "HIGHLY CONFIDENTIAL – AEO", DesignationSource.Family),
            (None, string.Empty, DesignationSource.None),
            (None, string.Empty, DesignationSource.None));

        var own = DesignationResolver.Resolve(members, Levels, DesignationFamilyRule.Document);
        own.Select(r => (r.ChoiceId, r.Source)).Should().Equal(
            (Confidential, DesignationSource.Document), (Aeo, DesignationSource.Document), ((int?)null, DesignationSource.None),
            (None, DesignationSource.None), (None, DesignationSource.None), ((int?)null, DesignationSource.None));

        members[0] = members[0] with { Override = new DesignationOverride(Confidential) };
        members[2] = members[2] with { Override = new DesignationOverride(null) };
        var overridden = DesignationResolver.Resolve(members, Levels, DesignationFamilyRule.HighestInFamily);
        overridden[0].Should().Be(new ResolvedDesignation(1, Confidential, "CONFIDENTIAL", DesignationSource.Override, false));
        overridden[2].Should().Be(new ResolvedDesignation(3, null, string.Empty, DesignationSource.Override, false));
        overridden[3].Source.Should().Be(DesignationSource.Family, "an override of one member does not change the family's highest designation");

        var unlisted = DesignationResolver.Resolve([new(1, alone, 99)], Levels, DesignationFamilyRule.HighestInFamily);
        unlisted[0].Unlisted.Should().BeTrue();
    }

    [Fact]
    public void Every_produced_page_gets_the_endorsements_with_its_own_Bates_label_and_the_frozen_designation()
    {
        var spec = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC", 5, 4),
            Endorsements: new ProductionEndorsementSettings(
            [
                new(EndorsementPositionResource.BottomLeft, "{confidentiality}"),
                new(EndorsementPositionResource.BottomRight, "{bates}"),
                new(EndorsementPositionResource.TopCenter, "{production}"),
            ])), Catalog, Unrestricted, out _)!.Specification;
        var member = Member(begin: 5, end: 7, designation: "CONFIDENTIAL");

        var pages = EndorsementPlanner.ForMember(spec, "Vol 1", member);

        pages.Select(p => p.BatesLabel).Should().Equal("ABC0005", "ABC0006", "ABC0007");
        pages.Should().AllSatisfy(p => p.Stamps.Select(s => s.Text).Should().Contain(["CONFIDENTIAL", "Vol 1"]));
        pages[2].Stamps.Should().ContainEquivalentOf(new PageStamp(EndorsementPosition.BottomRight, "ABC0007"));

        EndorsementPlanner.ForPage(spec, "Vol 1", "ABC0001", string.Empty).Select(s => s.Position).Should().Equal(
            [EndorsementPosition.TopCenter, EndorsementPosition.BottomRight], "an empty legend stamps nothing");

        var documentLevel = ProductionSpecificationRules.Normalize(new ProductionSpecification(
            new ProductionBatesSettings("ABC", Level: BatesLevelResource.Document)), Catalog, Unrestricted, out _)!.Specification;
        EndorsementPlanner.PageLabels(ProductionSpecificationRules.FormatOf(documentLevel), Member(3, 3, null) with { PageCount = 2 })
            .Should().Equal("ABC0000003.0001", "ABC0000003.0002");
        EndorsementPlanner.PageLabels(ProductionSpecificationRules.FormatOf(spec), Member(9, 9, null) with { Output = ProductionOutputKind.Native })
            .Should().Equal(["ABC0009"], "a native's slip sheet is one page");
    }

    [Fact]
    public void The_designation_QC_check_requires_the_legend_on_every_page_and_the_same_value_in_the_load_file()
    {
        var spec = ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC", 5, 4)), Catalog, Unrestricted,
            out _)!.Specification;
        var member = Member(5, 7, "HIGHLY CONFIDENTIAL – AEO");
        var stamped = EndorsementPlanner.ForMember(spec, "P", member).Select(p => p.Stamps).ToList();

        DesignationQc.CheckMember(spec, member, stamped, "HIGHLY CONFIDENTIAL – AEO").Should().BeEmpty();
        DesignationQc.CheckMember(spec, member, stamped, "CONFIDENTIAL").Should().ContainSingle().Which.Should().Contain("load file");
        DesignationQc.CheckMember(spec, member, [stamped[0], stamped[1], [new PageStamp(EndorsementPosition.BottomRight, "ABC0007")]],
            "HIGHLY CONFIDENTIAL – AEO").Should().ContainSingle().Which.Should().Contain("page 3");
        DesignationQc.CheckMember(spec, member, stamped.Take(2).ToList(), "HIGHLY CONFIDENTIAL – AEO").Should().ContainSingle().Which.Should().Contain("2 pages");
        DesignationQc.CheckMember(spec, member with { DesignationSource = null }, stamped, null).Should().ContainSingle().Which.Should().Contain("not frozen");
        var undesignated = Member(5, 5, string.Empty);
        DesignationQc.CheckMember(spec, undesignated, EndorsementPlanner.ForMember(spec, "P", undesignated).Select(p => p.Stamps).ToList(), string.Empty)
            .Should().BeEmpty();
    }

    private static void Refused(ProductionDesignationSettings designations, string key)
    {
        ProductionSpecificationRules.Normalize(new ProductionSpecification(new ProductionBatesSettings("ABC"), Designations: designations), Catalog,
            Unrestricted, out var errors).Should().BeNull();
        errors.Should().ContainKey(key);
    }

    private static ProductionDocumentRow Member(long begin, long end, string? designation) => new(
        1, Guid.NewGuid(), Guid.NewGuid(), 0, 1, ProductionOutputKind.Image, (int)(end - begin + 1), begin, end, "B", "E", "B", "E", "CN",
        PageCount: (int)(end - begin + 1), Designation: designation, DesignationChoiceId: null,
        DesignationSource: designation is null ? null : designation.Length == 0 ? DesignationSource.None : DesignationSource.Document);
}
