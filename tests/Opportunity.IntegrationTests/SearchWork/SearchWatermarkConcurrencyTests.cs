using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Search;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Core.SearchWork;
using Opportunity.Data;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Freshness;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// E07-T08 against real PostgreSQL (the V0011 late-lock counter and the V0030 visible watermark): concurrent
/// transactions that start in one order, commit in another or roll back, workers that apply and fail rows in random
/// order, and the watermark ticker running all the while. The refresh port is a model of OpenSearch: a refresh makes
/// exactly the rows that were Applied when it started searchable. The watermark must never cover a row outside that set
/// and must reach the counter once everything is applied. The ticker's per-workspace samples feed the lag gauges.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class SearchWatermarkConcurrencyTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(70)]
    [InlineData(71)]
    [InlineData(72)]
    public async Task The_watermark_never_passes_unapplied_or_unrefreshed_work_under_concurrent_out_of_order_commits(int seed)
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await db.Core.ExecuteAsync("SELECT opportunity.search_work_ensure_partitions(now() + interval '1 day')");
        var ws = await db.Core.CreateWorkspaceAsync();
        var store = new SearchWatermarkStore(db.Core.AppDataSource);
        var index = new ModelIndex(db);
        var advancer = new SearchWatermarkAdvancer(store, index, NullLogger<SearchWatermarkAdvancer>.Instance);

        var committers = Enumerable.Range(0, 6).Select(i => CommitAsync(db, ws, new Random(seed * 100 + i))).ToArray();
        var allCommitted = Task.WhenAll(committers);
        var workers = Enumerable.Range(0, 3).Select(i => WorkAsync(db, ws, new Random(seed * 1000 + i), allCommitted)).ToArray();
        var allApplied = Task.WhenAll(workers);

        var violations = new List<string>();
        long last = 0, advanced = 0, behind = 0;
        while (!allApplied.IsCompleted)
        {
            var reading = (await advancer.TickAsync([ws], Ct))[ws];
            violations.AddRange(await CheckAsync(db, ws, reading.IndexedThroughGeneration, index));
            reading.IndexedThroughGeneration.Should().BeGreaterThanOrEqualTo(last, "the watermark never moves backwards");
            advanced += reading.IndexedThroughGeneration > last ? 1 : 0;
            behind += reading.IsCurrent ? 0 : 1;
            last = reading.IndexedThroughGeneration;
            await Task.Delay(TimeSpan.FromMilliseconds(seed % 3 * 5), Ct);
        }

        await allApplied;
        violations.Should().BeEmpty();
        behind.Should().BePositive("ticks ran while committed work was not yet searchable");
        TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}: {advanced} advances, {behind} ticks behind");

        var final = (await advancer.TickAsync([ws], Ct))[ws];
        final.LatestGeneration.Should().BeGreaterThan(0);
        final.IsCurrent.Should().BeTrue("everything is applied and the last refresh covered it");
        final.Level.Should().Be(SearchFreshnessLevel.Current);
        final.Lag.Should().Be(TimeSpan.Zero);
        (await CheckAsync(db, ws, final.IndexedThroughGeneration, index)).Should().BeEmpty();
        (await db.Core.ScalarAsync<long>("SELECT count(DISTINCT search_generation) FROM opportunity.search_outbox WHERE workspace_id = @ws", ("ws", ws)))
            .Should().Be(final.LatestGeneration, "rolled-back transactions consume no generation (gap-free)");
    }

    [Fact]
    public async Task Unrefreshed_work_holds_the_watermark_and_the_lag_is_measured_from_its_commit()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await db.Core.ExecuteAsync("SELECT opportunity.search_work_ensure_partitions(now() + interval '1 day')");
        var ws = await db.Core.CreateWorkspaceAsync();
        var store = new SearchWatermarkStore(db.Core.AppDataSource);
        var index = new ModelIndex(db) { Fail = true };
        var advancer = new SearchWatermarkAdvancer(store, index, NullLogger<SearchWatermarkAdvancer>.Instance);

        (await store.ReadAsync(ws, Ct)).Should().Match<SearchFreshnessReading>(r => r.IsCurrent && r.LatestGeneration == 0 && r.Lag == TimeSpan.Zero);
        var first = await db.AddOutboxRowsAsync(ws, 2, SearchChangeMask.Coding);
        await db.AddOutboxRowsAsync(ws, 1, SearchChangeMask.Coding);
        await db.Core.ExecuteAsync(
            "UPDATE opportunity.search_outbox SET committed_at = committed_at - interval '3 minutes' WHERE workspace_id = @ws AND search_generation = @g",
            ("ws", ws), ("g", first));
        await db.Core.ExecuteAsync("UPDATE opportunity.search_outbox SET status = 4, applied_at = now() WHERE workspace_id = @ws", ("ws", ws));

        // Applied but the refresh fails: nothing is searchable yet, the oldest unreflected commit is 3 minutes old.
        var held = (await advancer.TickAsync([ws], Ct))[ws];
        held.AppliedGeneration.Should().Be(2);
        held.IndexedThroughGeneration.Should().Be(0);
        held.PendingChanges.Should().Be(2);
        held.Lag.Should().BeGreaterThan(TimeSpan.FromMinutes(3));
        held.Level.Should().Be(SearchFreshnessLevel.Delayed);
        SearchFreshnessMapping.ToStatus(held, held.ReadAt).State.Should().Be(Contracts.Search.SearchFreshnessState.Delayed);

        index.Fail = false;
        var current = (await advancer.TickAsync([ws], Ct))[ws];
        current.IndexedThroughGeneration.Should().Be(2);
        current.Level.Should().Be(SearchFreshnessLevel.Current);
        (await store.AdvanceAsync(ws, 1, Ct)).IndexedThroughGeneration.Should().Be(2, "the watermark never moves backwards");
        (await store.AdvanceAsync(ws, 99, Ct)).IndexedThroughGeneration.Should().Be(2, "never past the counter");
    }

    [Fact]
    public async Task The_dispatcher_ticker_samples_every_workspace_and_exports_the_lag_gauges()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await db.Core.ExecuteAsync("SELECT opportunity.search_work_ensure_partitions(now() + interval '1 day')");
        var ws = await db.Core.CreateWorkspaceAsync();
        var quiet = await db.Core.CreateWorkspaceAsync();
        using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = meters.GetRequiredService<IMeterFactory>();
        var gauges = new ConcurrentDictionary<(string Name, string? Workspace), double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OpportunityTelemetry.MeterName && instrument.Meter.Scope == factory)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => gauges[(i.Name, Workspace(tags))] = v);
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => gauges[(i.Name, Workspace(tags))] = v);
        listener.Start();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(new OpportunityMetrics(factory));
        services.AddSingleton<ISearchWatermarkStore>(new SearchWatermarkStore(db.Core.AppDataSource));
        services.AddSingleton<ISearchWorkMaintenance>(db.Maintenance);
        services.AddSingleton<ISearchIndexRefresher>(new ModelIndex(db));
        services.AddSingleton(new SearchWatermarkOptions { Interval = TimeSpan.FromMilliseconds(100), WorkspaceRefreshInterval = TimeSpan.FromMilliseconds(100) });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new SearchWatermarkAdvancer(
            sp.GetRequiredService<ISearchWatermarkStore>(), sp.GetRequiredService<ISearchIndexRefresher>(),
            NullLogger<SearchWatermarkAdvancer>.Instance, sp.GetRequiredService<OpportunityMetrics>()));
        services.AddSingleton<SearchWatermarkService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<SearchWatermarkService>();
        await service.StartAsync(Ct);
        try
        {
            var generation = await db.AddOutboxRowsAsync(ws, 3, SearchChangeMask.Coding);
            await Eventually(() =>
            {
                listener.RecordObservableInstruments();
                return gauges.GetValueOrDefault((OpportunityMetricCatalog.SearchGenerationLag.Name, ws.ToString())) == 1
                    && gauges.ContainsKey((OpportunityMetricCatalog.SearchIndexLag.Name, ws.ToString()))
                    && gauges.GetValueOrDefault((OpportunityMetricCatalog.SearchGenerationCommitted.Name, ws.ToString())) == generation;
            });
            gauges[(OpportunityMetricCatalog.SearchGenerationIndexed.Name, ws.ToString())].Should().Be(0);
            gauges[(OpportunityMetricCatalog.SearchGenerationLag.Name, quiet.ToString())].Should().Be(0, "every workspace is sampled");

            await db.Core.ExecuteAsync("UPDATE opportunity.search_outbox SET status = 4, applied_at = now() WHERE workspace_id = @ws", ("ws", ws));
            await Eventually(() =>
            {
                listener.RecordObservableInstruments();
                return gauges.GetValueOrDefault((OpportunityMetricCatalog.SearchGenerationIndexed.Name, ws.ToString())) == generation;
            });
            gauges[(OpportunityMetricCatalog.SearchGenerationLag.Name, ws.ToString())].Should().Be(0);
            gauges[(OpportunityMetricCatalog.SearchIndexLag.Name, ws.ToString())].Should().Be(0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Transactions that do their work, then take the generation as their last statement; 30% roll back.</summary>
    private static async Task CommitAsync(SearchWorkDatabase db, Guid ws, Random rng)
    {
        for (var i = 0; i < 12; i++)
        {
            await using var tx = await WorkspaceTransaction.BeginAsync(db.Core.AppDataSource, ws, Ct);
            await Task.Delay(rng.Next(0, 15), Ct); // the transaction's own work: commits overtake each other here
            await SearchWorkSql.AddOutboxRowsAsync(tx, [.. Enumerable.Range(0, rng.Next(1, 4)).Select(_ => (Guid.CreateVersion7(), 1L))],
                SearchChangeMask.Coding, Ct);
            if (rng.Next(10) < 3)
            {
                await tx.Transaction.RollbackAsync(Ct);
            }
            else
            {
                await tx.CommitAsync(Ct);
            }
        }
    }

    /// <summary>Applies pending rows in random order; some fail first and are replayed later.</summary>
    private static async Task WorkAsync(SearchWorkDatabase db, Guid ws, Random rng, Task committed)
    {
        while (true)
        {
            var done = committed.IsCompleted;
            var row = await db.Core.ScalarAsync<long?>(
                "SELECT outbox_id FROM opportunity.search_outbox WHERE workspace_id = @ws AND status IN (1, 5) ORDER BY random() LIMIT 1", ("ws", ws));
            if (row is not { } id)
            {
                if (done)
                {
                    return;
                }

                await Task.Delay(2, Ct);
                continue;
            }

            var (status, applied) = rng.Next(10) == 0 ? ((short)5, "NULL") : ((short)4, "now()");
            await db.Core.ExecuteAsync(
                $"UPDATE opportunity.search_outbox SET status = @s, applied_at = {applied} WHERE workspace_id = @ws AND outbox_id = @id AND status <> 4",
                ("ws", ws), ("id", id), ("s", status));
            await Task.Delay(rng.Next(0, 4), Ct);
        }
    }

    /// <summary>Rows at or below the watermark that no successful refresh made searchable.</summary>
    private static async Task<List<string>> CheckAsync(SearchWorkDatabase db, Guid ws, long watermark, ModelIndex index)
    {
        var covered = await db.Core.ScalarAsync<long[]>(
            "SELECT coalesce(array_agg(outbox_id), '{}') FROM opportunity.search_outbox WHERE workspace_id = @ws AND search_generation <= @w",
            ("ws", ws), ("w", watermark));
        return [.. covered.Where(id => !index.Visible.ContainsKey(id)).Select(id => $"outbox row {id} is under watermark {watermark} but not searchable")];
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the ticker samples every 100 ms in this test");
            await Task.Delay(50, Ct);
        }
    }

    private static string? Workspace(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == TelemetryAttributes.WorkspaceId)
            {
                return tag.Value as string;
            }
        }

        return null;
    }

    /// <summary>OpenSearch as the watermark sees it: a successful refresh makes every row Applied at its start searchable.</summary>
    private sealed class ModelIndex(SearchWorkDatabase db) : ISearchIndexRefresher
    {
        public ConcurrentDictionary<long, bool> Visible { get; } = new();

        public bool Fail { get; set; }

        public async Task<IReadOnlySet<Guid>> RefreshAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                return new HashSet<Guid>();
            }

            foreach (var ws in workspaceIds)
            {
                var applied = await db.Core.ScalarAsync<long[]>(
                    "SELECT coalesce(array_agg(outbox_id), '{}') FROM opportunity.search_outbox WHERE workspace_id = @ws AND status = 4", ("ws", ws));
                foreach (var id in applied)
                {
                    Visible[id] = true;
                }
            }

            return workspaceIds.ToHashSet();
        }
    }
}
