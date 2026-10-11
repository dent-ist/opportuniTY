using System.Text;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Opportunity.Application.Defensibility;
using Opportunity.Application.Search.TermReports;
using Opportunity.Core.SearchTermReports;
using Opportunity.Production.Defensibility;

namespace Opportunity.UnitTests.Audit;

/// <summary>E14-T05: the as-of point, the fingerprint of the as-of content and the CSV forms of the defensibility reports.</summary>
public sealed class DefensibilityReportTests
{
    private static readonly Guid Workspace = Guid.Parse("0190a000-0000-7000-8000-000000000001");
    private static readonly Guid Reviewer = Guid.Parse("0190a000-0000-7000-8000-000000000002");
    private static readonly DateTimeOffset AsOf = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_point_defaults_to_now_is_truncated_to_microseconds_and_never_lies_in_the_future()
    {
        var time = new FakeTimeProvider(AsOf.AddTicks(12_345));
        var service = new DefensibilityReportService(null!, null!, null!, null!, time);

        var now = service.Point(null)!;
        now.IsNow.Should().BeTrue();
        now.AsOf.Should().Be(AsOf.AddTicks(12_340));
        var past = service.Point(AsOf.AddHours(-1).ToOffset(TimeSpan.FromHours(2)))!;
        past.IsNow.Should().BeFalse();
        past.AsOf.Offset.Should().Be(TimeSpan.Zero);
        past.AsOf.Should().Be(AsOf.AddHours(-1));
        service.Point(AsOf.AddMinutes(1)).Should().BeNull();
    }

    [Fact]
    public void The_fingerprint_covers_as_of_facts_but_not_display_names()
    {
        var data = Summary("Rae Reviewer");
        var fingerprint = DefensibilityFingerprint.ReviewSummary(Workspace, AsOf, null, data);

        DefensibilityFingerprint.ReviewSummary(Workspace, AsOf, null, Summary("Rae R. (renamed)")).Should().Be(fingerprint);
        DefensibilityFingerprint.ReviewSummary(Workspace, AsOf.AddSeconds(1), null, data).Should().NotBe(fingerprint);
        DefensibilityFingerprint.ReviewSummary(Workspace, AsOf, null, data with { Population = data.Population with { Reviewed = 2 } })
            .Should().NotBe(fingerprint);
        fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void The_review_summary_csv_states_the_point_and_neutralizes_formulas()
    {
        var report = new ReviewSummaryReport(Statement(DefensibilityReportKind.ReviewSummary), null, Summary("=HYPERLINK(\"x\")"));

        var csv = Encoding.UTF8.GetString(DefensibilityCsv.ReviewSummary(report));

        csv.Should().StartWith("﻿Report,Review population and coding summary\r\n");
        csv.Should().Contain("As of (UTC),2026-09-30 12:00:00.000000,2026-09-30T12:00:00.0000000Z\r\n");
        csv.Should().Contain("Fingerprint (SHA-256),abc\r\n");
        csv.Should().Contain("Reviewed (coded by a person),1\r\n");
        csv.Should().Contain($"{Reviewer:D},\"'=HYPERLINK(\"\"x\"\")\",1,2,2,0,0,");
        DefensibilityCsv.FileName(report.Statement).Should().Be("review-summary-asof-20260930-120000.csv");
    }

    [Fact]
    public void Hit_reports_note_the_index_lag_at_execution()
    {
        var record = new SearchTermReportRecord
        {
            WorkspaceId = Workspace,
            ReportId = Guid.CreateVersion7(),
            Name = "Terms",
            ScopeKind = SearchTermReportScopeKind.Workspace,
            Status = SearchTermReportStatus.Completed,
            JobId = Guid.CreateVersion7(),
            SearchGeneration = 7,
            IndexCurrent = false,
            IndexPendingChanges = 3,
            IndexLag = TimeSpan.FromMilliseconds(4500),
            TermCount = 1,
            CreatedBy = Reviewer,
            CreatedByDisplay = "Rae",
            CreatedAt = AsOf,
            ExecutedBy = Reviewer,
            ExecutedByDisplay = "Rae",
        };
        var report = new SearchTermHitsReport(Statement(DefensibilityReportKind.SearchTermHits),
            [new TermHitReportEntry(record, [new SearchTermRecord(1, Guid.CreateVersion7(), "Merger", "merger", null, 2, 2, 1, 1)])], false);

        var csv = Encoding.UTF8.GetString(DefensibilityCsv.SearchTermHits(report));

        csv.Should().Contain(",7,No,3,4.5,Rae,");
        SearchTermReportExport.IndexLag(record).Should().Be("3 change(s) not yet searchable; the oldest was 4.5 s old.");
        SearchTermReportExport.IndexLag(record with { IndexPendingChanges = 0, IndexLag = TimeSpan.Zero })
            .Should().Be("None: every committed change was searchable.");
        SearchTermReportExport.IndexLag(record with { IndexPendingChanges = null, IndexLag = null }).Should().BeNull();
    }

    private static ReviewSummaryData Summary(string name) => new(
        new ReviewPopulation(3, 1, 2, 0, 0),
        [new ReviewerCoding(Reviewer, name, 1, 2, 2, 0, 0, AsOf.AddHours(-2), AsOf.AddHours(-2))],
        [new ReviewerDayCoding(DateOnly.FromDateTime(AsOf.UtcDateTime), Reviewer, name, 1, 2, 2, 0, 0)],
        false);

    private static DefensibilityStatement Statement(DefensibilityReportKind kind) =>
        new(kind, Workspace, AsOf, null, null, AsOf, "Ada Admin", "abc", Guid.CreateVersion7());
}
