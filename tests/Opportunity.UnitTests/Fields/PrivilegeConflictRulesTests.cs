using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Fields;

/// <summary>E13-T02 rules that need no database: conflict reasons, grouped propagation job parameters and the CSV form.</summary>
public class PrivilegeConflictRulesTests
{
    private const int NotPrivileged = 11;
    private const int Withhold = 12;
    private const int Redact = 13;
    private const int AttorneyClient = 21;
    private const int Responsive = 31;
    private const int NotResponsive = 32;
    private const int ResponsivenessField = 1001;

    private static readonly FieldCatalog Catalog = new(
        [
            new FieldDefinition { FieldId = PrivilegeFields.Status, Name = "Privilege Status", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding, IsSystem = true },
            new FieldDefinition
            {
                FieldId = PrivilegeFields.Basis, Name = "Privilege Basis", Type = FieldType.MultiChoice, Storage = FieldStorage.Coding, IsSystem = true, IsMultiValue = true,
            },
            new FieldDefinition { FieldId = ResponsivenessField, Name = "Responsiveness", Type = FieldType.SingleChoice, Storage = FieldStorage.Coding },
        ],
        [
            new Choice { FieldId = PrivilegeFields.Status, ChoiceId = NotPrivileged, Name = "Not Privileged", SortOrder = 1, SystemKey = PrivilegeFields.Keys.NotPrivileged },
            new Choice { FieldId = PrivilegeFields.Status, ChoiceId = Withhold, Name = "Withhold", SortOrder = 2, SystemKey = PrivilegeFields.Keys.Withhold },
            new Choice { FieldId = PrivilegeFields.Status, ChoiceId = Redact, Name = "Redact", SortOrder = 3, SystemKey = PrivilegeFields.Keys.Redact },
            new Choice { FieldId = PrivilegeFields.Basis, ChoiceId = AttorneyClient, Name = "Attorney-Client", SortOrder = 1 },
            new Choice { FieldId = ResponsivenessField, ChoiceId = Responsive, Name = "Responsive", SortOrder = 1 },
            new Choice { FieldId = ResponsivenessField, ChoiceId = NotResponsive, Name = "=Not Responsive", SortOrder = 2 },
        ]);

    [Theory]
    [InlineData(new[] { Withhold, 0 }, true)]
    [InlineData(new[] { Withhold, NotPrivileged }, true)]
    [InlineData(new[] { Withhold, Redact }, true)]
    [InlineData(new[] { Withhold, Withhold }, false)]
    [InlineData(new[] { Redact, NotPrivileged }, false)]
    [InlineData(new[] { Withhold }, false)]
    public void A_family_conflicts_when_a_member_is_withheld_and_another_is_not(int[] statuses, bool conflict) =>
        PrivilegeConflictRules.Evaluate(PrivilegeConflictKind.Family, Members(statuses), Withhold, NotPrivileged, responsiveness: false)
            .HasFlag(PrivilegeConflictReasons.WithheldMember).Should().Be(conflict);

    [Theory]
    [InlineData(new[] { Withhold, 0 }, true)]
    [InlineData(new[] { Withhold, NotPrivileged }, true)]
    [InlineData(new[] { Withhold, Redact }, true)]
    [InlineData(new[] { Redact, NotPrivileged, NotPrivileged }, true)]
    [InlineData(new[] { NotPrivileged, 0 }, false)]
    [InlineData(new[] { NotPrivileged, NotPrivileged }, false)]
    [InlineData(new[] { Withhold, Withhold }, false)]
    [InlineData(new[] { Redact }, false)]
    public void Duplicates_conflict_when_their_calls_differ_and_one_is_a_privilege_call(int[] statuses, bool conflict) =>
        PrivilegeConflictRules.Evaluate(PrivilegeConflictKind.Duplicates, Members(statuses), Withhold, NotPrivileged, responsiveness: false)
            .Should().Be(conflict ? PrivilegeConflictReasons.PrivilegeCallsDiffer : PrivilegeConflictReasons.None);

    [Fact]
    public void Differing_responsiveness_calls_in_a_family_count_only_when_asked_for()
    {
        var members = new[]
        {
            Member(0, responsiveness: Responsive),
            Member(0, responsiveness: NotResponsive),
            Member(0),
        };
        PrivilegeConflictRules.Evaluate(PrivilegeConflictKind.Family, members, Withhold, NotPrivileged, responsiveness: true)
            .Should().Be(PrivilegeConflictReasons.ResponsivenessDiffers);
        PrivilegeConflictRules.Evaluate(PrivilegeConflictKind.Family, members, Withhold, NotPrivileged, responsiveness: false)
            .Should().Be(PrivilegeConflictReasons.None);
        PrivilegeConflictRules.Evaluate(PrivilegeConflictKind.Family, [Member(0, responsiveness: Responsive), Member(0)], Withhold, NotPrivileged, true)
            .Should().Be(PrivilegeConflictReasons.None, "an uncoded member has no call to differ from");
    }

    [Fact]
    public void A_grouped_propagation_round_trips_through_the_job_parameters()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var origin = Guid.NewGuid();
        GroupPropagationEntry[] groups =
        [
            new(first, Guid.NewGuid(),
                [CodingFieldOperation.Set(PrivilegeFields.Status, JsonValue.Create(Withhold)), CodingFieldOperation.Set(PrivilegeFields.Basis, new JsonArray(AttorneyClient))],
                new Dictionary<int, Guid> { [PrivilegeFields.Status] = origin }),
            new(second, Guid.NewGuid(), [CodingFieldOperation.Clear(PrivilegeFields.Status), CodingFieldOperation.Clear(PrivilegeFields.Basis)],
                new Dictionary<int, Guid>()),
        ];

        var json = BulkCodingParameters.ToJson(groups, securityAffecting: true);
        var parsed = BulkCodingParameters.Groups(json)!;

        parsed.Select(g => g.DuplicateGroupId).Should().Equal(second, first);
        parsed[1].SourceDocumentId.Should().Be(groups[0].SourceDocumentId);
        parsed[1].OriginEventIds.Should().Equal(new Dictionary<int, Guid> { [PrivilegeFields.Status] = origin });
        parsed[1].Operations.Select(o => (o.FieldId, o.Value?.ToJsonString())).Should().Equal(
            (PrivilegeFields.Status, Withhold.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (PrivilegeFields.Basis, $"[{AttorneyClient}]"));
        parsed[0].Operations.Should().OnlyContain(o => o.Value == null);
        BulkCodingParameters.SecurityAffecting(json).Should().BeTrue();
        BulkCodingParameters.Propagation(json).Should().BeNull();
        BulkCodingParameters.Groups(BulkCodingParameters.ToJson([CodingFieldOperation.Clear(PrivilegeFields.Status)], true)).Should().BeNull();

        json["groupPropagation"]!["groups"]!.AsArray().Add(json["groupPropagation"]!["groups"]![0]!.DeepClone());
        FluentActions.Invoking(() => BulkCodingParameters.Groups(json)).Should().Throw<FormatException>("a group appears once");
    }

    [Fact]
    public void The_csv_lists_every_member_with_values_and_reviewers_and_neutralizes_formulas()
    {
        var reviewer = Guid.NewGuid();
        var family = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero);
        var report = new PrivilegeConflictReport(
            at, Catalog, ResponsivenessField, null,
            [
                new PrivilegeConflictGroup(PrivilegeConflictKind.Family, family,
                    PrivilegeConflictReasons.WithheldMember | PrivilegeConflictReasons.ResponsivenessDiffers,
                    [
                        Member(Withhold, by: reviewer, at: at, basis: AttorneyClient, responsiveness: Responsive, controlNumber: "ACME0001"),
                        Member(0, responsiveness: NotResponsive, controlNumber: "-ACME0002", sequence: 1),
                    ]),
            ],
            new Dictionary<Guid, string> { [reviewer] = "Pat Reviewer, Esq." },
            Truncated: false);

        var bytes = PrivilegeConflictReportCsv.Build(report);
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("Conflict,Group ID,Reasons,Document ID,Control Number,Family Sequence,Duplicate Primary,Privilege Status,Status Set By")
            .And.EndWith("Responsiveness,Responsiveness Set By,Responsiveness Set At (UTC)");
        lines[1].Should().StartWith($"Family,{family:D},Withheld member in a family that is not withheld as a whole; Responsiveness calls differ within the family,")
            .And.Contain(",ACME0001,0,No,Withhold,\"Pat Reviewer, Esq.\",2026-10-08 09:30:00,Attorney-Client,");
        lines[2].Should().Contain(",'-ACME0002,1,No,,,,,,,'=Not Responsive,", "formula-leading cells are neutralized");
        PrivilegeConflictReportCsv.FileName(report).Should().Be("privilege-conflicts-20261008-093000.csv");
    }

    private static PrivilegeConflictMember[] Members(int[] statuses) => [.. statuses.Select(s => Member(s))];

    private static PrivilegeConflictMember Member(
        int status, Guid? by = null, DateTimeOffset? at = null, int basis = 0, int responsiveness = 0, string controlNumber = "DOC", int sequence = 0) =>
        new(Guid.NewGuid(), controlNumber, sequence, false, false,
            status == 0 ? PrivilegeCodedValue.Empty : new PrivilegeCodedValue([status], by, at),
            basis == 0 ? PrivilegeCodedValue.Empty : new PrivilegeCodedValue([basis], by, at),
            responsiveness == 0 ? PrivilegeCodedValue.Empty : new PrivilegeCodedValue([responsiveness], by, at));
}
