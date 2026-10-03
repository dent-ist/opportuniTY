using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Data.Audit;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>E14-T01: the PostgreSQL writer and reader behind <see cref="IAuditEventWriter"/> / <see cref="IAuditEventReader"/>.</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditWriterReaderTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task System_chain_events_round_trip_and_a_retried_write_is_stored_once()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var writer = new PostgresAuditEventWriter(db.AppDataSource);
        var signIn = AuditSamples.Event(null);

        await writer.WriteAsync(signIn, Ct);
        await writer.WriteAsync(signIn, Ct);

        var stored = (await AuditSamples.ReadAllAsync(db, null)).Should().ContainSingle().Subject;
        stored.Event.Should().BeEquivalentTo(signIn, o => o.Excluding(e => e.SessionIdHash));
        stored.RecordedAt.Should().BeOnOrAfter(signIn.OccurredAt);
    }

    [Fact]
    public async Task Invalid_events_are_rejected_before_they_reach_the_store()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var writer = new PostgresAuditEventWriter(db.AppDataSource);

        foreach (var invalid in new[]
        {
            AuditSamples.Event(null) with { Outcome = AuditOutcome.Denied, ReasonCode = null },
            AuditSamples.Event(null) with { RestrictedDetails = new Dictionary<string, string?> { ["Query"] = "x" } },
            AuditSamples.Event(null) with { SessionIdHash = new byte[] { 1, 2, 3 } },
            AuditSamples.Event(Guid.Empty),
        })
        {
            var act = () => writer.WriteAsync(invalid, Ct).AsTask();
            await act.Should().ThrowAsync<ArgumentException>();
        }

        (await db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_event")).Should().Be(0);
    }

    [Fact]
    public async Task Search_text_is_stored_in_full_and_returned_only_when_the_reader_may_see_it()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var writer = new PostgresAuditEventWriter(db.AppDataSource);
        var reader = new PostgresAuditEventReader(db.AppDataSource);
        var query = "(\"price fixing\" OR collusion) AND custodian:smith W/5 " + new string('x', 20_000);
        await writer.WriteAsync(AuditSamples.Event(ws) with
        {
            Category = "Search",
            Action = "Executed",
            ResourceType = null,
            ResourceId = null,
            RestrictedDetails = new Dictionary<string, string?> { ["QueryText"] = query, ["AstVersion"] = "1" },
        }, Ct);
        var auditor = AuditSamples.Event(ws) with { ActorId = "auditor-1", ActorDisplay = "Audi Tor" };

        var envelopes = await reader.QueryAsync(new AuditQuery(ws) { Category = "Search" }, auditor, Ct);
        envelopes.Events.Should().ContainSingle().Which.Event.RestrictedDetails.Should().BeNull("Audit.Read alone excludes search text");

        var withText = await reader.QueryAsync(new AuditQuery(ws) { Category = "Search", IncludeRestrictedDetails = true }, auditor, Ct);
        withText.Events.Single().Event.RestrictedDetails!["QueryText"].Should().Be(query, "Q-16: the full executed text, not a hash");

        // Who read the audit is itself audited, in the same chain.
        var queried = (await AuditSamples.ReadAllAsync(db, ws)).Where(e => e.Event.Action == AuditTaxonomy.Audit.Queried).ToList();
        queried.Should().HaveCount(2).And.OnlyContain(e => e.Event.ActorId == "auditor-1" && e.Event.Category == AuditTaxonomy.Audit.Category);
        queried[1].Event.Details.Should().Contain("IncludeRestrictedDetails", "true").And.Contain("Category", "Search");
    }

    [Fact]
    public async Task Queries_page_in_occurrence_order_and_filter_by_envelope_fields()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        var writer = new PostgresAuditEventWriter(db.AppDataSource);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds());
        for (var i = 0; i < 7; i++)
        {
            await writer.WriteAsync(AuditSamples.Event(ws) with
            {
                OccurredAt = start.AddSeconds(i),
                ResourceType = "Document",
                ResourceId = i % 2 == 0 ? "doc-even" : "doc-odd",
            }, Ct);
        }

        var reader = new PostgresAuditEventReader(db.AppDataSource);
        var auditor = AuditSamples.Event(ws);
        var seen = new List<StoredAuditEvent>();
        var query = new AuditQuery(ws) { ResourceType = "Document", ResourceId = "doc-even", Limit = 3 };
        AuditEventPage page;
        do
        {
            page = await reader.QueryAsync(query, auditor, Ct);
            seen.AddRange(page.Events);
            query = query with { After = page.Next };
        }
        while (page.Next is not null);

        seen.Select(e => e.Event.OccurredAt).Should().Equal(start, start.AddSeconds(2), start.AddSeconds(4), start.AddSeconds(6));
    }
}
