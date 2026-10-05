#if OPPORTUNITY_FAILPOINTS
using System.Diagnostics;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Coding;
using Opportunity.Application.Faults;
using Opportunity.Application.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Coding;
using Opportunity.Core.SearchWork;
using Opportunity.Correctness.ShadowLedger;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.IntegrationTests.SearchWork;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>
/// E17-T07 against real PostgreSQL and OpenSearch: the shadow-ledger oracle passes a correct run, counts the version
/// conflicts it saw, and — its own sensitivity check — catches a deliberately unversioned stale write in 10 of 10 seeded
/// trials. The race is the canonical one of ADR-001 §9: worker A reads a document at version v and stalls; the document
/// is edited to v+1, which worker B indexes; A then writes its v read. With external versioning that write is a 409
/// no-op; with the test-only <see cref="FaultFlags.UnversionedProjectionWrite"/> switch it overwrites the newer state.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class ShadowLedgerOracleTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const string StaleRejections = "opportunity.search.stale_version_rejections";
    private const int Trials = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_versioned_run_with_delayed_stale_writes_passes_and_counts_the_conflicts()
    {
        var race = new RaceInjector();
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres, configure: s => s.AddSingleton<IFaultInjector>(race));
        var (w, oracle) = await PrepareAsync(h, documents: 12, seed: 1);
        await using (oracle)
        {
            oracle.StartSampling();
            var raced = await RaceAsync(h, w, race, new Random(1), unversioned: false);

            var verdict = await oracle.ReconcileAsync(Ct);
            await LedgerSupport.KeepAsync(verdict, "ledger-control");

            verdict.Passed.Should().BeTrue(verdict.ToString());
            verdict.ShadowLedger.TouchedDocs.Should().Be(w.Documents.Count);
            verdict.ShadowLedger.VersionConflictRejections.Should().Be(raced.Count, "every delayed stale write was a 409 no-op");
            verdict.Sampling.Passes.Should().BeGreaterThan(0);
        }
    }

    /// <summary>The oracle's sensitivity (E17-T07, test-strategy §5.1): 10 seeded trials, each detected.</summary>
    [Fact]
    public async Task A_deliberately_unversioned_stale_write_is_detected_in_10_of_10_seeded_trials()
    {
        var detected = 0;
        for (var trial = 1; trial <= Trials; trial++)
        {
            var seed = 7_000 + trial;
            var race = new RaceInjector();
            await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres, configure: s => s.AddSingleton<IFaultInjector>(race));
            var (w, oracle) = await PrepareAsync(h, documents: 6, seed);
            await using (oracle)
            {
                oracle.StartSampling();
                var raced = await RaceAsync(h, w, race, new Random(seed), unversioned: true);

                var verdict = await oracle.ReconcileAsync(Ct);
                await LedgerSupport.KeepAsync(verdict, $"ledger-unversioned-{seed}");

                verdict.Passed.Should().BeFalse($"trial {trial} (seed {seed}): {verdict}");
                verdict.ShadowLedger.StaleOverwrites.Should().Be(raced.Count, $"trial {trial} (seed {seed}): every raced document was overwritten");
                verdict.Findings.Where(f => f.Kind == LedgerFindingKind.StaleVersion).Select(f => f.DocumentId)
                    .Should().BeEquivalentTo(raced, $"trial {trial} (seed {seed}) must name the stale documents");
                LedgerSupport.Json(verdict)["shadowLedger"]!["status"]!.GetValue<string>().Should().Be("failed");
                detected++;
            }
        }

        detected.Should().Be(Trials);
    }

    /// <summary>
    /// Q-70: the 1M-document ≤ 15 min target is hardware-bound, so the reconciliation is measured at CI scale and the
    /// rate recorded. It is streamed: keyset pages of <c>BatchSize</c> documents, one PostgreSQL snapshot and one
    /// <c>_mget</c> each, so memory stays bounded by the batch whatever the corpus size. Override the size with
    /// <c>OPPORTUNITY_LEDGER_RECONCILE_DOCS</c>.
    /// </summary>
    [Fact]
    public async Task Full_reconciliation_streams_in_batches_and_records_its_rate()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("OPPORTUNITY_LEDGER_RECONCILE_DOCS"), out var n) && n > 0 ? n : 20_000;
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: count);
        var load = Stopwatch.StartNew();
        foreach (var batch in w.Documents.Chunk(CodingWriteRequest.MaxDocuments))
        {
            await h.Db.Core.Coding.ApplyAsync(new CodingWriteRequest
            {
                WorkspaceId = w.Id,
                IdempotencyKey = "load-" + Guid.CreateVersion7().ToString("N"),
                Actor = new CodingActor(SearchWorkDatabase.Reviewer, CodingActorType.Human),
                Documents = [.. batch.Select(d => new CodingTarget(d))],
                Operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))],
            }, Ct);
        }

        foreach (var batch in w.Documents.Chunk(500))
        {
            var report = await h.Writer.WriteAsync(w.Id, await h.Projections.BuildAsync(w.Id, batch, Ct), Ct);
            report.Documents.Should().OnlyContain(d => d.Succeeded);
        }

        var loaded = load.Elapsed;
        await using var oracle = await LedgerSupport.StartAsync(
            h.Db.Core.DataSource, h.Http, w.Id, await LedgerSupport.TargetAsync(h.Indexes, w.Id), null, seed: null,
            since: DateTimeOffset.UnixEpoch);
        var verdict = await oracle.ReconcileAsync(Ct);
        await LedgerSupport.KeepAsync(verdict, "ledger-reconcile-rate");

        verdict.Passed.Should().BeTrue(verdict.ToString());
        verdict.ShadowLedger.TouchedDocs.Should().Be(count);
        verdict.Reconciliation!.Batches.Should().Be((count + 999) / 1_000);
        var perSecond = verdict.Reconciliation.DocumentsPerSecond;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"reconciled {count} documents in {verdict.Reconciliation.DurationMs:F0} ms ({perSecond:F0} docs/s, batch 1,000; loaded in {loaded.TotalSeconds:F1} s); " +
            $"at this rate 1M documents take {1_000_000 / Math.Max(perSecond, 1) / 60:F1} min (target <= 15 min on reference hardware, Q-70)");
    }

    /// <summary>A workspace whose documents are all coded and indexed once (so its placement exists), and a ledger.</summary>
    private static async Task<(TestWorkspace Workspace, LedgerOracle Oracle)> PrepareAsync(IndexWorkerHarness h, int documents, long seed)
    {
        var w = await h.Db.WorkspaceAsync(documents);
        foreach (var doc in w.Documents)
        {
            await InteractiveIndexWorkerTests.CodeAsync(h, w, doc, true);
        }

        foreach (var message in await h.DispatchAsync(w.Id))
        {
            await h.HandleAsync(message);
        }

        var oracle = await LedgerSupport.StartAsync(
            h.Db.Core.DataSource, h.Http, w.Id, await LedgerSupport.TargetAsync(h.Indexes, w.Id), () => h.Meters.Sum(StaleRejections), seed,
            since: DateTimeOffset.UnixEpoch);
        return (w, oracle);
    }

    /// <summary>
    /// Races 1–3 seeded documents as described on the class; each stalled worker reads version v, and 1–2 further edits
    /// are indexed before it writes. Returns the raced documents once every outbox row is Applied.
    /// </summary>
    private static async Task<List<Guid>> RaceAsync(IndexWorkerHarness h, TestWorkspace w, RaceInjector race, Random random, bool unversioned)
    {
        var raced = w.Documents.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToList();
        foreach (var doc in raced)
        {
            // Every edit flips Responsive (all documents start true), so each one commits a new DocumentVersion.
            var value = false;
            await InteractiveIndexWorkerTests.CodeAsync(h, w, doc, value);
            var stale = (await h.DispatchAsync(w.Id)).Single();
            var stalled = race.Hold(((SearchOutboxMessage)stale.Payload).OutboxId, unversioned);
            var handling = h.HandleAsync(stale);
            await stalled.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            for (var edits = random.Next(1, 3); edits > 0; edits--)
            {
                value = !value;
                await InteractiveIndexWorkerTests.CodeAsync(h, w, doc, value);
                foreach (var newer in await h.DispatchAsync(w.Id))
                {
                    await h.HandleAsync(newer);
                }
            }

            race.Release();
            await handling;
        }

        (await h.Db.OutboxStatusesAsync(w.Id)).Where(s => s.Key != SearchOutboxStatus.Applied).Sum(s => s.Value).Should().Be(0);
        return raced;
    }

    /// <summary>Stalls the worker handling one outbox row after its PostgreSQL read; optionally unversions only that write.</summary>
    private sealed class RaceInjector : IFaultInjector
    {
        private string? _subject;
        private bool _unversioned;
        private TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

        public Task Hold(long outboxId, bool unversioned)
        {
            _subject = outboxId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _unversioned = unversioned;
            _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _reached.Task;
        }

        public void Release() => _release.TrySetResult();

        public async ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken)
        {
            if (context.Subject != _subject)
            {
                return;
            }

            if (failpoint == Failpoints.OutboxBeforeBulk)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                _armed = _unversioned;
            }
            else if (failpoint == Failpoints.OutboxAfterBulk)
            {
                _armed = false;
                _subject = null;
            }
        }

        public bool IsArmed(string flag) => flag == FaultFlags.UnversionedProjectionWrite && _armed;
    }
}
#endif
