using System.Text;

using AwesomeAssertions;

using Opportunity.Application.ListExports;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Production.ListExports;

namespace Opportunity.UnitTests.Exports;

/// <summary>#181: the document list CSV (UTF-8 with BOM, formula-neutralized), its columns and its permission and snapshot rules.</summary>
public sealed class ListExportTests
{
    [Fact]
    public void The_file_starts_with_a_utf8_byte_order_mark_and_the_header_row()
    {
        var header = ListExportCsv.Header([new ListExportColumn(SystemFields.ControlNumber, "Control Number"), new ListExportColumn(1001, "Subject, short")]);

        header[..3].Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(header[3..]).Should().Be("Control Number,\"Subject, short\"\r\n");
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1 555 0100", "'+1 555 0100")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("\tlead", "'\tlead")]
    [InlineData("-12.5", "-12.5")]
    [InlineData("Zoë; Ångström", "Zoë; Ångström")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(null, "")]
    public void Cells_are_neutralized_and_quoted(string? value, string expected) => ListExportCsv.Cell(value).Should().Be(expected);

    [Fact]
    public void Rows_end_with_crlf()
    {
        var text = new StringBuilder();
        ListExportCsv.AppendRow(text, ["ABC0001", null, "=1+1"]);
        text.ToString().Should().Be("ABC0001,,'=1+1\r\n");
    }

    [Fact]
    public void Control_number_comes_first_then_the_view_columns_in_order_without_duplicates()
    {
        var columns = ListExportService.Columns([1001, SystemFields.FileName, 1001, SystemFields.ControlNumber], Catalog(), new HashSet<int>(), out var errors);

        errors.Should().BeEmpty();
        columns!.Select(c => c.FieldId).Should().Equal(SystemFields.ControlNumber, 1001, SystemFields.FileName);
        columns!.Select(c => c.Header).Should().Equal("Control Number", "Secret Notes", "File Name");
    }

    [Fact]
    public void Unknown_deleted_and_restricted_fields_are_refused_alike()
    {
        var restricted = new HashSet<int> { 1001 };

        ListExportService.Columns([1001, 999_999, 1002], Catalog(), restricted, out var errors).Should().BeNull();

        errors.Keys.Should().BeEquivalentTo("fieldIds[0]", "fieldIds[1]", "fieldIds[2]");
        errors.Values.Should().AllSatisfy(e => e.Should().Equal("No such field."));
    }

    [Fact]
    public void Columns_are_required_and_bounded()
    {
        ListExportService.Columns(null, Catalog(), new HashSet<int>(), out var missing).Should().BeNull();
        missing.Should().ContainKey("fieldIds");
        ListExportService.Columns([.. Enumerable.Repeat(1001, ListExportService.MaxColumns + 1)], Catalog(), new HashSet<int>(), out var many).Should().BeNull();
        many.Should().ContainKey("fieldIds");
        ListExportService.Columns([], Catalog(), new HashSet<int>(), out _)!.Select(c => c.FieldId).Should().Equal(SystemFields.ControlNumber);
    }

    [Fact]
    public void The_permission_follows_the_download_default_and_never_break_glass()
    {
        PermissionCatalog.Get(Permission.DocumentExportList).Name.Should().Be("Document.ExportList");
        ((int)Permission.DocumentExportList).Should().Be(37);
        WorkspaceRole.WorkspaceAdmin.Grants(Permission.DocumentExportList).Should().BeTrue();
        WorkspaceRole.ProductionManager.Grants(Permission.DocumentExportList).Should().BeTrue();
        WorkspaceRole.Reviewer.Grants(Permission.DocumentExportList).Should().BeFalse();
        WorkspaceRole.BreakGlass.Grants(Permission.DocumentExportList).Should().BeFalse();
    }

    [Fact]
    public void A_list_export_freezes_a_materialized_snapshot_with_its_own_permission()
    {
        SnapshotRules.RequiredPermission(SnapshotPurpose.ListExport).Should().Be(Permission.DocumentExportList);
        SnapshotRules.MemberPermission(SnapshotPurpose.ListExport).Should().Be(Permission.DocumentExportList);
        SnapshotRules.RetainedForMatter(SnapshotPurpose.ListExport).Should().BeFalse();
        SnapshotStrategyRules.AlwaysMaterialized(SetOperationKind.ListExport).Should().BeTrue();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.ListExport, SnapshotPurpose.ListExport).Should().BeTrue();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.Export, SnapshotPurpose.ListExport).Should().BeFalse();
        ChunkBounds.For(JobType.ListExport).MaxItems.Should().Be(1_000);
    }

    [Fact]
    public void The_file_name_names_the_list_export()
    {
        var id = Guid.Parse("0199c3a0-1a2b-7000-8000-000000000001");
        ListExportCsv.FileName(id, new DateTimeOffset(2026, 10, 11, 10, 42, 0, TimeSpan.Zero)).Should().Be("export-list-20261011-104200-0199c3a0.csv");
    }

    private static FieldCatalog Catalog()
    {
        var ws = Guid.CreateVersion7();
        return new FieldCatalog(
            [
                .. SystemFields.Create(ws),
                new FieldDefinition { WorkspaceId = ws, FieldId = 1001, Name = "Secret Notes", Type = FieldType.Text, Storage = FieldStorage.Coding },
                new FieldDefinition { WorkspaceId = ws, FieldId = 1002, Name = "Gone", Type = FieldType.Text, Storage = FieldStorage.Coding, IsDeleted = true },
            ],
            []);
    }
}
