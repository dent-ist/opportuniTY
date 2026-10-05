using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using AwesomeAssertions;

using Opportunity.Application.Search.TermReports;
using Opportunity.Core.Documents;
using Opportunity.Core.SearchTermReports;

namespace Opportunity.UnitTests.Search;

/// <summary>E07-T10: family hit counting, the pasted CSV term list and the CSV/XLSX exports.</summary>
public sealed class SearchTermReportRulesTests
{
    private static readonly Guid F1 = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid F2 = Guid.Parse("00000000-0000-0000-0000-0000000000f2");

    [Fact]
    public void Family_counts_follow_the_documented_definitions()
    {
        // Family F1: parent hits 1, attachment hits 2, second attachment hits nothing. Family F2: parent hits 1 only,
        // attachment hits nothing. Standalone S1 hits 2 only; S2 hits 1 and 2; S3 hits nothing.
        var p1 = Doc(F1, 1);
        var a1 = Doc(F1, 2);
        var a2 = Doc(F1);
        var p2 = Doc(F2, 1);
        var a3 = Doc(F2);
        var s1 = Doc(null, 2);
        var s2 = Doc(null, 1, 2);
        var s3 = Doc(null);
        var (terms, totals) = FamilyHitCounting.Count([p1, a1, a2, p2, a3, s1, s2, s3], [1, 2, 3]);

        terms.Should().Equal(
            new TermHitCounts(1, WithHits: 3, WithHitsIncludingFamily: 6, Unique: 2, UniqueIncludingFamily: 2),
            new TermHitCounts(2, WithHits: 3, WithHitsIncludingFamily: 5, Unique: 2, UniqueIncludingFamily: 1),
            new TermHitCounts(3, 0, 0, 0, 0));
        totals.Should().Be(new ScopeHitTotals(InScope: 8, WithHits: 5, WithHitsIncludingFamily: 7, WithoutHits: 3));
    }

    [Fact]
    public void Csv_term_lists_parse_with_quotes_header_and_single_values()
    {
        var terms = SearchTermReportRules.ParseCsv("Name,Expression\r\nContract,\"contract OR agreement\"\n\n\"Quote \"\"x\"\"\",\"a, b\"\nmemo\n", out var error);

        error.Should().BeNull();
        terms.Should().Equal(
            new SearchTermInput("Contract", "contract OR agreement"),
            new SearchTermInput("Quote \"x\"", "a, b"),
            new SearchTermInput("memo", "memo"));
        SearchTermReportRules.ParseCsv("a,b,c", out error).Should().BeNull();
        error.Should().Contain("Row 1");
        SearchTermReportRules.ParseCsv("a,\"b", out error).Should().BeNull();
        error.Should().Contain("not closed");
    }

    [Fact]
    public void Exports_hold_provenance_terms_and_totals_and_are_reproducible()
    {
        var report = Report();
        IReadOnlyList<SearchTermRecord> terms =
        [
            new(1, Guid.NewGuid(), "Contract", "contract", null, 3, 5, 1, 2),
            new(2, Guid.NewGuid(), "Formula", "=cmd", new SearchTermError("UNEXPECTED_TOKEN", "Unexpected '='.", 0), null, null, null, null),
        ];

        var csv = Encoding.UTF8.GetString(SearchTermReportExport.Csv(report, terms));
        csv.Should().StartWith("﻿Search Terms Report,Report A");
        csv.Should().Contain("Index current at execution,No: changes were still being made searchable");
        csv.Should().Contain("Contract,contract,3,5,1,2,\r\n");
        csv.Should().Contain("Formula,'=cmd,,,,,UNEXPECTED_TOKEN: Unexpected '='.");
        csv.Should().Contain("Total documents in scope,,10\r\n");

        var xlsx = SearchTermReportExport.Xlsx(report, terms);
        xlsx.Should().Equal(SearchTermReportExport.Xlsx(report, terms), "the same run exports byte-identical files");
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        zip.Entries.Select(e => e.FullName).Should().Contain(["[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml"]);
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(sheet);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cells = xml.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!, c => c.Value);
        cells["A1"].Should().Be("Search Terms Report");
        cells.Values.Should().Contain("=cmd", "inline strings are never formulas");
        xml.Descendants(ns + "f").Should().BeEmpty();
        cells.Should().ContainValue("Contract");
    }

    private static FamilyScopedDocument Doc(Guid? family, params int[] terms)
    {
        var id = Guid.NewGuid();
        return new FamilyScopedDocument(id, family ?? id, new HashSet<int>(terms));
    }

    private static SearchTermReportRecord Report() => new()
    {
        WorkspaceId = Guid.NewGuid(),
        ReportId = Guid.NewGuid(),
        Name = "Report A",
        ScopeKind = SearchTermReportScopeKind.Workspace,
        Status = SearchTermReportStatus.Completed,
        JobId = Guid.NewGuid(),
        SnapshotId = Guid.NewGuid(),
        SearchGeneration = 42,
        IndexCurrent = false,
        DocumentsInScope = 10,
        DocumentsWithHits = 3,
        DocumentsWithHitsIncludingFamily = 5,
        DocumentsWithoutHits = 7,
        TermCount = 2,
        CreatedBy = Guid.NewGuid(),
        CreatedByDisplay = "Ada",
        CreatedAt = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero),
        ExecutedBy = Guid.NewGuid(),
        ExecutedByDisplay = "Ada",
        ExecutedAt = new DateTimeOffset(2026, 10, 5, 9, 0, 1, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 10, 5, 9, 0, 2, TimeSpan.Zero),
    };
}
