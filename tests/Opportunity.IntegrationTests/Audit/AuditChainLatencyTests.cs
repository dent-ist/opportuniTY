using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Audit.Chain;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Data.Audit;
using Opportunity.Data.Coding;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Audit;

/// <summary>
/// E14-T03 acceptance: chain sealing adds at most 5 ms p95 to interactive coding. Concurrent reviewers save coding with
/// their in-transaction <c>Coding.Changed</c> event in alternating rounds without and with sealers running at the
/// dispatcher's cadence (two competing replicas, each every <see cref="AuditChainOptions.SealInterval"/>), and the p95
/// difference is the cost sealing adds. An A/A line (the sealer-free rounds split in two) shows the noise floor. Absolute latency is asserted only under <c>OPPORTUNITY_STRICT_LATENCY=1</c> (Q-44; test strategy §4),
/// so the test runs only there; the numbers go to the test output and <c>OPPORTUNITY_AUDIT_CHAIN_LATENCY_REPORT</c>.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuditChainLatencyTests(MigrationPostgresFixture postgres)
{
    private const int Reviewers = 20;
    private const int SavesPerRound = 20;
    private const int Rounds = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sealing_adds_at_most_5_ms_p95_to_interactive_coding()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OPPORTUNITY_STRICT_LATENCY") == "1",
            "Latency budget; runs only with OPPORTUNITY_STRICT_LATENCY=1 (Q-44).");

        await using var h = await AuditChainHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var responsive = (await h.Db.Fields.CreateFieldAsync(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding), Ct)).Value!.FieldId;
        var documents = new List<Guid>();
        for (var i = 0; i < Reviewers; i++)
        {
            documents.Add((await h.Db.InsertDocumentAsync(ws, $"DOC{i:D5}")).DocumentId);
        }

        await using var pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(h.Db.AppConnectionString) { MaxPoolSize = 40 }.ConnectionString);
        var coding = new CodingRepository(pool);
        for (var i = 0; i < 20; i++)
        {
            await coding.ApplyAsync(Save(ws, documents[i % Reviewers], responsive, i), Ct);
        }

        await h.Sealer.SealAsync(cancellationToken: Ct);
        var without = new ConcurrentBag<double>();
        var withoutA = new ConcurrentBag<double>();
        var withoutB = new ConcurrentBag<double>();
        var with = new ConcurrentBag<double>();
        long sealedEvents = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var sealing = round % 2 == 1;
            using var stop = new CancellationTokenSource();
            var sealers = sealing
                ? new[] { h.Sealer, h.NewSealer() }.Select(sealer => Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        Interlocked.Add(ref sealedEvents, (await sealer.SealAsync(cancellationToken: CancellationToken.None)).Sealed);
                        await Task.Delay(new AuditChainOptions().SealInterval, CancellationToken.None);
                    }
                }, CancellationToken.None)).ToArray()
                : [];

            var samples = sealing ? with : without;
            await Task.WhenAll(documents.Select((documentId, reviewer) => Task.Run(async () =>
            {
                for (var i = 0; i < SavesPerRound; i++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var result = await coding.ApplyAsync(Save(ws, documentId, responsive, i + reviewer, reviewer), Ct);
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    samples.Add(elapsed);
                    if (!sealing)
                    {
                        (round % 4 == 0 ? withoutA : withoutB).Add(elapsed);
                    }
                    result.Outcome.Should().Be(CodingWriteOutcome.Applied);
                }
            }, Ct)));

            await stop.CancelAsync();
            await Task.WhenAll(sealers);
            if (!sealing)
            {
                await h.Sealer.SealAsync(cancellationToken: Ct);
            }
        }

        var added = P(with, 95) - P(without, 95);
        var report = string.Create(CultureInfo.InvariantCulture,
            $"""
            Audit chain sealing overhead, {Reviewers} concurrent reviewers, {Rounds} alternating rounds x {SavesPerRound} saves, {Environment.ProcessorCount} CPUs:
              coding save (with Coding.Changed), no sealer      p50 {P(without, 50):F2} ms  p95 {P(without, 95):F2} ms
              coding save (with Coding.Changed), sealers active p50 {P(with, 50):F2} ms  p95 {P(with, 95):F2} ms
              added by sealing                                  p50 {P(with, 50) - P(without, 50):F2} ms  p95 {added:F2} ms (budget 5 ms)
              noise floor (A/A, sealer-free rounds split)       p95 {Math.Abs(P(withoutA, 95) - P(withoutB, 95)):F2} ms
              events sealed while measuring: {sealedEvents}
            """);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        TestContext.Current.SendDiagnosticMessage(report);
        TestContext.Current.AddAttachment("audit-chain-latency", report);
        if (Environment.GetEnvironmentVariable("OPPORTUNITY_AUDIT_CHAIN_LATENCY_REPORT") is { Length: > 0 } reportPath)
        {
            await File.WriteAllTextAsync(reportPath, report, Ct);
        }

        sealedEvents.Should().BeGreaterThan(0, "the sealers ran during the measured rounds");
        (await h.VerifyAsync(ws) is { } verification && verification.Intact).Should().BeTrue();
        added.Should().BeLessThanOrEqualTo(5, "E14-T03: chain sealing adds at most 5 ms p95 to interactive coding");
    }

    private static CodingWriteRequest Save(Guid ws, Guid documentId, int fieldId, int n, int reviewer = 0) => new()
    {
        WorkspaceId = ws,
        IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
        Actor = new CodingActor(Guid.CreateVersion7(), CodingActorType.Human),
        Documents = [new CodingTarget(documentId)],
        Operations = [CodingFieldOperation.Set(fieldId, JsonValue.Create(n % 2 == 0))],
        Audit = new AuditEvent
        {
            WorkspaceId = ws,
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditTaxonomy.Coding.Category,
            Action = AuditTaxonomy.Coding.Changed,
            ActorType = AuditActorType.User,
            ActorId = $"reviewer-{reviewer}",
            ActorDisplay = $"Reviewer {reviewer}",
            ClientIp = "192.0.2.10",
            ResourceType = "Document",
            ResourceId = documentId.ToString(),
            Outcome = AuditOutcome.Success,
            CorrelationId = Guid.CreateVersion7().ToString("N"),
            Details = new Dictionary<string, string?> { ["FieldIds"] = fieldId.ToString(CultureInfo.InvariantCulture) },
        },
    };

    private static double P(IEnumerable<double> samples, int percentile)
    {
        var sorted = samples.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
