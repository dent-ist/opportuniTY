using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Core.Jobs;

namespace Opportunity.UnitTests.Audit;

/// <summary>E14-T04: the audit log viewer's filters, the audit export's CSV and its time-window chunks.</summary>
public sealed class AuditLogTests
{
    [Fact]
    public void Filters_need_a_known_category_an_action_of_it_and_an_ordered_range()
    {
        AuditLogService.Validate(new AuditLogFilters()).Should().BeNull();
        AuditLogService.Validate(new AuditLogFilters { Category = "Document", Action = "Viewed" }).Should().BeNull();
        AuditLogService.Validate(new AuditLogFilters { Category = "Document" }).Should().BeNull();
        AuditLogService.Validate(new AuditLogFilters { Action = "Viewed" })!.Keys.Should().Equal("action");
        AuditLogService.Validate(new AuditLogFilters { Category = "Document", Action = "Changed" })!.Keys.Should().Equal("action");
        AuditLogService.Validate(new AuditLogFilters { Category = "Nope" })!.Keys.Should().Equal("category");
        var now = DateTimeOffset.UtcNow;
        AuditLogService.Validate(new AuditLogFilters { From = now, To = now })!.Keys.Should().Equal("to");
    }

    [Fact]
    public void Filters_round_trip_through_their_stored_form_and_name_a_document_as_the_resource()
    {
        var filters = new AuditLogFilters
        {
            From = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            Category = "Coding",
            Action = "Changed",
            ActorId = "u-1",
            DocumentId = Guid.CreateVersion7(),
            JobId = Guid.CreateVersion7(),
            Outcome = AuditOutcome.Denied,
        };

        AuditLogFilters.FromJson(filters.ToJson()).Should().Be(filters);
        var query = filters.ToQuery(Guid.CreateVersion7());
        query.ResourceType.Should().Be("Document");
        query.ResourceId.Should().Be(filters.DocumentId.ToString());
        query.JobId.Should().Be(filters.JobId);
        filters.ToDetails().Keys.Should().BeEquivalentTo(["From", "To", "Category", "Action", "ActorId", "DocumentId", "JobId", "Outcome"]);
    }

    [Fact]
    public void The_csv_has_a_bom_header_and_neutralizes_formulas()
    {
        var header = AuditExportCsv.Header(includeSearchText: true);
        header.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(header, 3, header.Length - 3).Should().StartWith("Occurred At (UTC),Event ID,").And.EndWith(",Search Text\r\n");

        var e = new AuditEvent
        {
            OccurredAt = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero).AddTicks(10),
            Category = "Search",
            Action = "Executed",
            ActorType = AuditActorType.User,
            ActorId = "u-1",
            ActorDisplay = "=HYPERLINK(\"x\")",
            Outcome = AuditOutcome.Success,
            CorrelationId = "c-1",
            Details = new Dictionary<string, string?> { ["Hits"] = "3" },
            RestrictedDetails = new Dictionary<string, string?> { ["query"] = "pricing, \"memo\"" },
        };
        var text = new StringBuilder();
        AuditExportCsv.AppendRow(text, new StoredAuditEvent(e, e.OccurredAt, 7, null) { DocumentControlNumber = "ABC-1" }, includeSearchText: true);

        var row = text.ToString();
        row.Should().StartWith("2026-10-10T09:00:00.000001Z," + e.EventId.ToString("D") + ",Search,Executed,Success,");
        row.Should().Contain(",\"'=HYPERLINK(\"\"x\"\")\",");
        row.Should().Contain(",ABC-1,");
        row.Should().EndWith(",7,\"{\"\"query\"\":\"\"pricing, \\\"\"memo\\\"\"\"\"}\"\r\n");
    }

    [Fact]
    public void A_time_window_chunk_is_a_range_of_microseconds()
    {
        var window = ChunkMembership.TimeRange(5, 5);
        window.Kind.Should().Be(ChunkMembershipKind.TimeRange);
        window.KnownCount.Should().BeNull();
        ChunkMembership.Restore(ChunkMembershipKind.TimeRange, null, null, 1, 9, null, null, null, null).Should().Be(ChunkMembership.TimeRange(1, 9));
        FluentActions.Invoking(() => ChunkMembership.TimeRange(9, 1)).Should().Throw<ArgumentOutOfRangeException>();

        var at = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero).AddTicks(30);
        AuditExportWindow.Time(AuditExportWindow.Micros(at)).Should().Be(at);
    }
}
