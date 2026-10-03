using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Telemetry;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T01 acceptance: audit write overhead recorded at 100 concurrent reviewers (ADR-013 §2.5 budget: ≤ 2 ms p95 per
/// insert). Each simulated reviewer codes its own document, alternating saves with and without the in-transaction
/// audit event, and writes standalone access events through <see cref="PostgresAuditEventWriter"/>. The numbers go to
/// the test output, a test attachment, the file named by <c>OPPORTUNITY_AUDIT_OVERHEAD_REPORT</c> (if set) and the
/// <c>opportunity.audit.write.duration</c> histogram. The assertions are loose because CI hardware varies; the
/// reference measurement belongs to the benchmark environment (E18).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditWriteOverheadTests(MigrationPostgresFixture postgres)
{
    private const int Reviewers = 100;
    private const int SavesPerReviewer = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Audit_write_overhead_is_recorded_at_100_concurrent_reviewers()
    {
        await using var db = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await db.CreateWorkspaceAsync();
        await db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var responsive = (await db.Fields.CreateFieldAsync(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding), Ct)).Value!.FieldId;
        var documents = new List<Guid>();
        for (var i = 0; i < Reviewers; i++)
        {
            documents.Add((await db.InsertDocumentAsync(ws, $"DOC{i:D5}")).DocumentId);
        }

        // One application pool shared by all reviewers, as in an API replica; below the container's max_connections.
        await using var pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 40 }.ConnectionString);
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new OpportunityMetrics(services.GetRequiredService<IMeterFactory>());
        var recorded = new ConcurrentBag<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == OpportunityMetricCatalog.AuditWriteDuration.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => recorded.Add(value));
        listener.Start();

        var coding = new CodingRepository(pool);
        var writer = new PostgresAuditEventWriter(pool, metrics);
        var withAudit = new ConcurrentBag<double>();
        var withoutAudit = new ConcurrentBag<double>();
        var standalone = new ConcurrentBag<double>();

        // Warm the pool and plans so the first saves do not measure connection setup.
        await coding.ApplyAsync(Save(ws, documents[0], responsive, false, null), Ct);

        await Task.WhenAll(documents.Select((documentId, reviewer) => Task.Run(async () =>
        {
            for (var i = 0; i < SavesPerReviewer; i++)
            {
                var audited = i % 2 == 0;
                var started = Stopwatch.GetTimestamp();
                var result = await coding.ApplyAsync(
                    Save(ws, documentId, responsive, audited, audited ? Event(ws, reviewer, "Coding", "Changed") : null), Ct);
                (audited ? withAudit : withoutAudit).Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                result.Outcome.Should().Be(CodingWriteOutcome.Applied);

                started = Stopwatch.GetTimestamp();
                await writer.WriteAsync(Event(ws, reviewer, "Document", "Retrieved") with { ResourceType = "Document", ResourceId = documentId.ToString() }, Ct);
                standalone.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }, Ct)));

        var report = string.Create(CultureInfo.InvariantCulture,
            $"""
            Audit write overhead, {Reviewers} concurrent reviewers x {SavesPerReviewer} saves (pool 40):
              coding save without audit  p50 {P(withoutAudit, 50):F2} ms  p95 {P(withoutAudit, 95):F2} ms
              coding save with audit     p50 {P(withAudit, 50):F2} ms  p95 {P(withAudit, 95):F2} ms
              in-transaction overhead    p50 {P(withAudit, 50) - P(withoutAudit, 50):F2} ms  p95 {P(withAudit, 95) - P(withoutAudit, 95):F2} ms
              standalone audit write     p50 {P(standalone, 50):F2} ms  p95 {P(standalone, 95):F2} ms (insert + commit, incl. pool wait)
            """);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        TestContext.Current.SendDiagnosticMessage(report);
        TestContext.Current.AddAttachment("audit-write-overhead", report);
        if (Environment.GetEnvironmentVariable("OPPORTUNITY_AUDIT_OVERHEAD_REPORT") is { Length: > 0 } reportPath)
        {
            await File.WriteAllTextAsync(reportPath, report, Ct);
        }

        (await db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_event WHERE category = 'Coding'"))
            .Should().Be(Reviewers * SavesPerReviewer / 2, "every audited save committed exactly one event");
        (await db.ScalarAsync<long>("SELECT count(*) FROM audit.audit_event WHERE category = 'Document'")).Should().Be(Reviewers * SavesPerReviewer);
        recorded.Should().HaveCount(Reviewers * SavesPerReviewer, "the writer records every standalone write in the histogram");
        P(standalone, 95).Should().BeLessThan(2_000, "a gross regression guard only; the 2 ms budget is verified on reference hardware");
    }

    private static CodingWriteRequest Save(Guid ws, Guid documentId, int fieldId, bool value, AuditEvent? audit) => new()
    {
        WorkspaceId = ws,
        IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
        Actor = new CodingActor(Guid.CreateVersion7(), CodingActorType.Human),
        Documents = [new CodingTarget(documentId)],
        Operations = [CodingFieldOperation.Set(fieldId, JsonValue.Create(value))],
        Audit = audit,
    };

    private static AuditEvent Event(Guid ws, int reviewer, string category, string action) => new()
    {
        WorkspaceId = ws,
        OccurredAt = DateTimeOffset.UtcNow,
        Category = category,
        Action = action,
        ActorType = AuditActorType.User,
        ActorId = $"reviewer-{reviewer}",
        ActorDisplay = $"Reviewer {reviewer}",
        ClientIp = "192.0.2.10",
        Outcome = AuditOutcome.Success,
        CorrelationId = Guid.CreateVersion7().ToString("N"),
        Details = new Dictionary<string, string?> { ["Purpose"] = "Display" },
    };

    private static double P(IEnumerable<double> samples, int percentile)
    {
        var sorted = samples.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
