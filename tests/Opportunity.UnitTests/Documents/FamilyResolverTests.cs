using AwesomeAssertions;

using Opportunity.Core.Documents;

namespace Opportunity.UnitTests.Documents;

/// <summary>ADR-009 §2 golden cases: one per family mode and per R10 conflict row, plus load-order independence.</summary>
public class FamilyResolverTests
{
    [Fact]
    public void Mode_A_range_members_join_the_parent_named_by_BegAttach()
    {
        var docs = Docs(
            Doc("ABC0001", beg: "ABC0001", end: "ABC0004"),
            Doc("ABC0002", beg: "ABC0001", end: "ABC0004"),
            Doc("ABC0003", beg: "ABC0001", end: "ABC0004"),
            Doc("ABC0004", beg: "ABC0001", end: "ABC0004"),
            Doc("ABC0005", beg: "ABC0005", end: "ABC0005"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "ABC0001").Should().Equal("ABC0001", "ABC0002", "ABC0003", "ABC0004");
        Get(result, docs, "ABC0003").Should().Be(new FamilyAssignment(Id(docs, "ABC0001"), Id(docs, "ABC0001"), 2, FamilyStatus.Resolved, null));
        Get(result, docs, "ABC0005").Should().Be(new FamilyAssignment(Id(docs, "ABC0005"), null, 0, FamilyStatus.Resolved, null));
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Mode_A_uses_natural_order_and_the_non_numeric_prefix()
    {
        var docs = Docs(
            Doc("ABC9", beg: "ABC9", end: "ABC11"),
            Doc("ABC10", beg: "ABC9", end: "ABC11"),
            Doc("ABC11", beg: "ABC9", end: "ABC11"),
            Doc("ABC10.1"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "ABC9").Should().Equal("ABC9", "ABC10", "ABC11");
        Get(result, docs, "ABC10.1").FamilyId.Should().Be(Id(docs, "ABC10.1"), "its prefix ABC10. differs from ABC");
    }

    [Fact]
    public void Mode_B_pointers_flatten_nested_attachments_and_keep_the_immediate_parent()
    {
        var docs = Docs(
            Doc("D1"),
            Doc("D2", parent: "D1"),
            Doc("D3", parent: "D2"),
            Doc("D4", parent: "D1"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "D1").Should().Equal("D1", "D2", "D3", "D4");
        Get(result, docs, "D3").ParentDocumentId.Should().Be(Id(docs, "D2"));
        Get(result, docs, "D4").ParentDocumentId.Should().Be(Id(docs, "D1"));
        result.Assignments.Values.Should().OnlyContain(a => a.Status == FamilyStatus.Resolved);
    }

    [Fact]
    public void Mode_C_group_parent_is_the_only_member_without_ParentID_else_the_lowest()
    {
        var docs = Docs(
            Doc("G1", group: "FAM-A", parent: "G3"),
            Doc("G2", group: "FAM-A", parent: "G3"),
            Doc("G3", group: "FAM-A"),
            Doc("H1", group: "FAM-B"),
            Doc("H2", group: "FAM-B"));
        var result = FamilyResolver.Resolve(docs.Values);

        Get(result, docs, "G1").FamilyId.Should().Be(Id(docs, "G3"));
        Family(result, docs, "G3").Should().Equal("G3", "G1", "G2");
        Family(result, docs, "H1").Should().Equal("H1", "H2");
        Get(result, docs, "H2").ParentDocumentId.Should().Be(Id(docs, "H1"));
    }

    [Fact]
    public void Pointer_wins_over_group_and_range_r9()
    {
        var docs = Docs(
            Doc("P1", group: "X", beg: "P1", end: "P3"),
            Doc("P2", group: "X", beg: "P1", end: "P3", parent: "Q1"),
            Doc("P3", group: "X", beg: "P1", end: "P3"),
            Doc("Q1"));
        var result = FamilyResolver.Resolve(docs.Values);

        Get(result, docs, "P2").FamilyId.Should().Be(Id(docs, "Q1"));
        Family(result, docs, "P1").Should().Equal("P1", "P3");
    }

    [Fact]
    public void Range_without_its_parent_forms_a_provisional_family_under_the_lowest_member()
    {
        var docs = Docs(
            Doc("R0002", beg: "R0001", end: "R0003"),
            Doc("R0003", beg: "R0001", end: "R0003"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "R0002").Should().Equal("R0002", "R0003");
        result.Assignments.Values.Should().OnlyContain(a => a.Status == FamilyStatus.ParentMissing);
        result.Issues.Should().HaveCount(2).And.OnlyContain(i => i.Kind == FamilyIssueKind.ParentMissing && i.Related.Contains("R0001"));
    }

    [Fact]
    public void Range_spanning_missing_control_numbers_is_a_gap_with_the_missing_count()
    {
        var docs = Docs(
            Doc("S0001", beg: "S0001", end: "S0006"),
            Doc("S0002", beg: "S0001", end: "S0006"),
            Doc("S0005", beg: "S0001", end: "S0006", endBates: "S0006"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "S0001").Should().Equal("S0001", "S0002", "S0005");
        result.Assignments.Values.Should().OnlyContain(a => a.Status == FamilyStatus.Gap);
        result.Issues.Should().ContainSingle().Which.Should().Match<FamilyIssue>(i =>
            i.Kind == FamilyIssueKind.RangeGap && i.MissingCount == 2 && i.DocumentId == Id(docs, "S0001"));
    }

    [Fact]
    public void Range_with_different_prefixes_is_ignored_and_the_document_stands_alone()
    {
        var docs = Docs(
            Doc("T0001", beg: "T0001", end: "T0002"),
            Doc("T0002", beg: "T0001", end: "U0002"));
        var result = FamilyResolver.Resolve(docs.Values);

        Get(result, docs, "T0002").Should().Be(new FamilyAssignment(Id(docs, "T0001"), Id(docs, "T0001"), 1, FamilyStatus.InvalidRange, null),
            "its own range is ignored but T0001's valid range still covers it");
        result.Issues.Should().ContainSingle(i => i.Kind == FamilyIssueKind.InvalidRange);

        var alone = Docs(Doc("V0001", beg: "V0001", end: "W0003"));
        Get(FamilyResolver.Resolve(alone.Values), alone, "V0001").Status.Should().Be(FamilyStatus.InvalidRange);
    }

    [Fact]
    public void Overlapping_ranges_join_the_lowest_parent_and_are_flagged_with_both_claims()
    {
        var docs = Docs(
            Doc("K0001", beg: "K0001", end: "K0005"),
            Doc("K0002", beg: "K0001", end: "K0005"),
            Doc("K0004", beg: "K0004", end: "K0006"),
            Doc("K0005", beg: "K0001", end: "K0005"),
            Doc("K0006", beg: "K0004", end: "K0006"));
        var result = FamilyResolver.Resolve(docs.Values);

        Get(result, docs, "K0005").Status.Should().Be(FamilyStatus.Conflict);
        Get(result, docs, "K0005").FamilyId.Should().Be(Id(docs, "K0001"));
        result.Issues.Where(i => i.Kind == FamilyIssueKind.ClaimedByTwoFamilies).Select(i => i.DocumentId)
            .Should().BeEquivalentTo([Id(docs, "K0004"), Id(docs, "K0005")]);
        result.Issues.Single(i => i.DocumentId == Id(docs, "K0005") && i.Kind == FamilyIssueKind.ClaimedByTwoFamilies)
            .Related.Should().Equal("K0001", "K0004");
    }

    [Fact]
    public void Nested_ranges_nest_without_a_conflict()
    {
        var docs = Docs(
            Doc("N0001", beg: "N0001", end: "N0005"),
            Doc("N0002", beg: "N0001", end: "N0005"),
            Doc("N0003", beg: "N0003", end: "N0004"),
            Doc("N0004", beg: "N0003", end: "N0004"),
            Doc("N0005", beg: "N0001", end: "N0005"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "N0001").Should().Equal("N0001", "N0002", "N0003", "N0004", "N0005");
        result.Assignments.Values.Should().OnlyContain(a => a.Status == FamilyStatus.Resolved);
        Get(result, docs, "N0004").ParentDocumentId.Should().Be(Id(docs, "N0001"), "R8: without ParentID the top-level parent");
    }

    [Fact]
    public void Pointer_cycle_is_broken_at_the_lowest_control_number()
    {
        var docs = Docs(Doc("C2", parent: "C1"), Doc("C1", parent: "C3"), Doc("C3", parent: "C2"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "C1").Should().Equal("C1", "C2", "C3");
        result.Assignments.Values.Should().OnlyContain(a => a.Status == FamilyStatus.Conflict);
        result.Issues.Should().HaveCount(3).And.OnlyContain(i => i.Kind == FamilyIssueKind.Cycle);
    }

    [Fact]
    public void Pointer_to_an_unknown_control_number_leaves_the_document_standalone()
    {
        var docs = Docs(Doc("O1", parent: "MISSING"));
        var result = FamilyResolver.Resolve(docs.Values);

        Get(result, docs, "O1").Should().Be(new FamilyAssignment(Id(docs, "O1"), null, 0, FamilyStatus.ParentMissing, null));
        result.Issues.Should().ContainSingle(i => i.Kind == FamilyIssueKind.ParentMissing && i.Related.Contains("MISSING"));
    }

    [Fact]
    public void Attachment_ids_are_cross_validated_only()
    {
        var docs = Docs(
            Doc("A1", attachments: ["A2", "B1", "ZZ"]),
            Doc("A2", parent: "A1"),
            Doc("A3", parent: "A1"),
            Doc("B1"));
        var result = FamilyResolver.Resolve(docs.Values);

        Family(result, docs, "A1").Should().Equal("A1", "A2", "A3");
        result.Assignments.Values.Should().OnlyContain(a => a.Status == FamilyStatus.Resolved);
        result.Issues.Where(i => i.Kind == FamilyIssueKind.AttachmentListMismatch).Select(i => i.Related.Single())
            .Should().BeEquivalentTo(["B1", "ZZ", "A1"]);
    }

    [Fact]
    public void Family_date_is_the_parents_upstream_family_date_else_its_document_date()
    {
        var sent = new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var upstream = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var docs = Docs(
            Doc("F1") with { DocumentDate = sent },
            Doc("F2", parent: "F1") with { DocumentDate = sent.AddYears(-5) },
            Doc("E1") with { DocumentDate = sent, UpstreamFamilyDate = upstream },
            Doc("E2", parent: "E1"));
        var result = FamilyResolver.Resolve(docs.Values);

        Get(result, docs, "F2").FamilyDate.Should().Be(sent);
        Get(result, docs, "E2").FamilyDate.Should().Be(upstream);
    }

    [Fact]
    public void Resolution_does_not_depend_on_input_order()
    {
        var random = new Random(84);
        var docs = new List<FamilySource>();
        for (var family = 0; family < 60; family++)
        {
            var parent = $"Z{family * 10 + 1:D5}";
            var size = random.Next(1, 7);
            var end = $"Z{family * 10 + size:D5}";
            for (var member = 1; member <= size; member++)
            {
                var cn = $"Z{family * 10 + member:D5}";
                docs.Add((family % 3) switch
                {
                    0 => Doc(cn, beg: parent, end: end),
                    1 => Doc(cn, parent: member == 1 ? null : member > 2 && random.Next(2) == 0 ? $"Z{family * 10 + 2:D5}" : parent),
                    _ => Doc(cn, group: "grp-" + family),
                });
            }
        }

        docs.Add(Doc("Z09991", beg: "Z00001", end: "Z00015"));
        docs.Add(Doc("Z09992", parent: "Z09993"));
        docs.Add(Doc("Z09993", parent: "Z09992"));
        var expected = FamilyResolver.Resolve(docs);
        for (var i = 0; i < 20; i++)
        {
            var shuffled = docs.OrderBy(_ => random.Next()).ToList();
            var actual = FamilyResolver.Resolve(shuffled);
            actual.Assignments.Should().BeEquivalentTo(expected.Assignments);
            actual.Issues.Should().Equal(expected.Issues, (a, b) =>
                a.DocumentId == b.DocumentId && a.Kind == b.Kind && a.Message == b.Message && a.Related.SequenceEqual(b.Related) && a.MissingCount == b.MissingCount);
        }
    }

    [Theory]
    [InlineData("ABC-0012", "ABC-", 12L)]
    [InlineData("ABC", "ABC", null)]
    [InlineData("0042", "", 42L)]
    [InlineData("X1234567890123456789", "X", null)]
    public void Prefix_and_numeric_tail(string norm, string prefix, long? tail)
    {
        FamilyResolver.Prefix(norm).Should().Be(prefix);
        (FamilyResolver.TryNumericTail(norm, out var value) ? value : (long?)null).Should().Be(tail);
    }

    private static FamilySource Doc(
        string cn, string? beg = null, string? end = null, string? parent = null, string? group = null, string? endBates = null,
        string[]? attachments = null) => new()
        {
            DocumentId = Guid.CreateVersion7(),
            ControlNumber = cn,
            ControlNumberNorm = cn,
            BegAttachNorm = beg,
            EndAttachNorm = end,
            ParentIdNorm = parent,
            GroupIdentifier = group,
            EndBates = endBates,
            AttachmentIdsNorm = attachments ?? [],
        };

    private static Dictionary<string, FamilySource> Docs(params FamilySource[] docs) => docs.ToDictionary(d => d.ControlNumberNorm);

    private static Guid Id(Dictionary<string, FamilySource> docs, string cn) => docs[cn].DocumentId;

    private static FamilyAssignment Get(FamilyResolution result, Dictionary<string, FamilySource> docs, string cn) => result.Assignments[Id(docs, cn)];

    /// <summary>The family of <paramref name="member"/> as control numbers in FamilySequence order.</summary>
    private static List<string> Family(FamilyResolution result, Dictionary<string, FamilySource> docs, string member)
    {
        var family = Get(result, docs, member).FamilyId;
        return [.. docs.Values.Where(d => result.Assignments[d.DocumentId].FamilyId == family)
            .OrderBy(d => result.Assignments[d.DocumentId].FamilySequence).Select(d => d.ControlNumber)];
    }
}
