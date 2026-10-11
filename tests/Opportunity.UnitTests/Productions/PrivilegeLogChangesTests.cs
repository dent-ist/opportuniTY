using AwesomeAssertions;

using Opportunity.Application.PrivilegeLogs;
using Opportunity.Contracts.Api;
using Opportunity.Production.PrivilegeLogs;

namespace Opportunity.UnitTests.Productions;

/// <summary>
/// #182: what changed between two privilege log versions (added, removed and changed entries keyed by document, cells
/// compared by column header), and saved-search metadata that leaves the canonical form of older logs untouched.
/// </summary>
public class PrivilegeLogChangesTests
{
    private static readonly Guid A = Guid.Parse("01890000-0000-7000-8000-00000000000a");
    private static readonly Guid B = Guid.Parse("01890000-0000-7000-8000-00000000000b");
    private static readonly Guid C = Guid.Parse("01890000-0000-7000-8000-00000000000c");
    private static readonly Guid D = Guid.Parse("01890000-0000-7000-8000-00000000000d");

    private static PrivilegeLogEntryRow Row(int ordinal, Guid document, PrivilegeLogEntryTreatment treatment, params string[] cells) =>
        new(ordinal, document, treatment, cells);

    [Fact]
    public void Entries_are_matched_by_document_and_changed_cells_are_named_by_column()
    {
        string[] columns = ["Priv ID", "Privilege Basis", "Description", "Withheld/Redacted"];
        PrivilegeLogEntryRow[] before =
        [
            Row(1, A, PrivilegeLogEntryTreatment.Withheld, "PRIV0001", "Attorney-Client", "Advice", "Withheld"),
            Row(2, B, PrivilegeLogEntryTreatment.Withheld, "PRIV0002", "Work Product", "", "Withheld"),
            Row(3, C, PrivilegeLogEntryTreatment.Redacted, "ABC0001", "Attorney-Client", "", "Redacted"),
        ];
        PrivilegeLogEntryRow[] after =
        [
            Row(1, A, PrivilegeLogEntryTreatment.Withheld, "PRIV0001", "Attorney-Client", "Legal advice on the supply agreement", "Withheld"),
            Row(2, C, PrivilegeLogEntryTreatment.Withheld, "ABC0001", "Attorney-Client", "", "Withheld"),
            Row(3, D, PrivilegeLogEntryTreatment.Withheld, "PRIV0002", "Common Interest", "", "Withheld"),
        ];

        var result = PrivilegeLogChanges.Compare(columns, after, columns, before);

        (result.Added, result.Removed, result.Changed, result.Unchanged).Should().Be((1, 1, 2, 0));
        result.Differences.Select(d => (d.Current ?? d.Previous)!.DocumentId).Should().Equal([A, C, D, B], "log order, removed entries last");
        result.Differences[0].ChangedCells.Should().Equal([("Description", "Advice", "Legal advice on the supply agreement")]);
        result.Differences[1].ChangedCells.Should().Equal([("Withheld/Redacted", "Redacted", "Withheld")],
            "a treatment change shown by a column is not repeated");
        result.Differences[2].Previous.Should().BeNull();
        result.Differences[3].Current.Should().BeNull();
        result.ColumnsAdded.Should().BeEmpty();
        result.ColumnsRemoved.Should().BeEmpty();
    }

    [Fact]
    public void Columns_only_one_version_has_are_reported_and_a_hidden_treatment_change_still_counts()
    {
        string[] before = ["Priv ID", "Description"];
        string[] after = ["Priv ID", "Date"];
        var result = PrivilegeLogChanges.Compare(
            after, [Row(1, A, PrivilegeLogEntryTreatment.Redacted, "PRIV0001", "2025-06-01"), Row(2, B, PrivilegeLogEntryTreatment.Withheld, "PRIV0002", "")],
            before, [Row(1, A, PrivilegeLogEntryTreatment.Withheld, "PRIV0001", "Advice"), Row(2, B, PrivilegeLogEntryTreatment.Withheld, "PRIV0002", "x")]);

        result.ColumnsAdded.Should().Equal("Date");
        result.ColumnsRemoved.Should().Equal("Description");
        (result.Changed, result.Unchanged).Should().Be((1, 1), "only common columns and the treatment are compared");
        result.Differences.Single().ChangedCells.Should().Equal([(PrivilegeLogChanges.TreatmentColumn, "Withheld", "Redacted")]);
    }

    [Fact]
    public void Identical_versions_have_no_differences()
    {
        string[] columns = ["Priv ID"];
        PrivilegeLogEntryRow[] rows = [Row(1, A, PrivilegeLogEntryTreatment.Withheld, "PRIV0001")];
        var result = PrivilegeLogChanges.Compare(columns, rows, columns, rows);
        result.Differences.Should().BeEmpty();
        result.Unchanged.Should().Be(1);
    }

    [Fact]
    public void Saved_search_metadata_is_written_only_for_saved_search_logs()
    {
        var metadata = new PrivilegeLogMetadata(1, PrivilegeLogSourceKind.Production, Guid.Parse("01890000-0000-7000-8000-000000000001"), "Production 1", 1,
            Guid.Parse("01890000-0000-7000-8000-000000000002"), null, null, "Document-by-document", ["Priv ID"], false, [], 1, 1, 0, 0, 0);
        var json = PrivilegeLogFiles.SerializeMetadata(metadata);
        json.Should().NotContain("savedSearch", "the canonical metadata (and content SHA-256) of production and frozen-set logs is unchanged");
        PrivilegeLogFiles.DeserializeMetadata(json).Should().BeEquivalentTo(metadata);

        var saved = metadata with
        {
            Source = PrivilegeLogSourceKind.SavedSearch,
            ProductionId = null,
            ProductionName = null,
            ProductionVersion = null,
            SavedSearchId = Guid.Parse("01890000-0000-7000-8000-000000000003"),
            SavedSearchName = "Privileged emails",
        };
        PrivilegeLogFiles.DeserializeMetadata(PrivilegeLogFiles.SerializeMetadata(saved)).Should().BeEquivalentTo(saved);
        PrivilegeLogFiles.Information(saved).Should().Contain(("Privilege Log", "Saved search: Privileged emails"))
            .And.Contain(("Saved search ID", "01890000-0000-7000-8000-000000000003"));
    }
}
