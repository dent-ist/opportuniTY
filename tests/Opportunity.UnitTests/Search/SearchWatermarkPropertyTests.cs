using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Search;
using Opportunity.Search.Freshness;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// E07-T08 property test (ADR-001 §7.3): the visible watermark never passes a generation whose work is unapplied, or
/// applied but not yet made searchable by a refresh, under concurrent transactions that commit out of their start
/// order, out-of-order and failing workers, background refreshes and failing refreshes. Each seed is one random
/// schedule; the model's steps run inside every port call of the ticker (before and after its PostgreSQL snapshot,
/// around the refresh, before the advance), so the world moves while a tick is in flight. The model is the oracle:
/// an item is visible only if its write was acknowledged before a refresh point.
/// </summary>
public sealed class SearchWatermarkPropertyTests
{
    private const int Seeds = 1500;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_watermark_never_passes_an_unapplied_or_unrefreshed_generation()
    {
        var advances = 0;
        for (var seed = 0; seed < Seeds; seed++)
        {
            var world = new World(new Random(seed), honestRefresh: true);
            await world.RunAsync(ticks: 40, Ct);
            world.Violations.Should().BeEmpty($"seed {seed}");
            advances += world.Advances;

            // Liveness: once all work is applied and a refresh succeeds, every workspace is current.
            await world.DrainAsync(Ct);
            world.Violations.Should().BeEmpty($"seed {seed} (drain)");
            world.Workspaces.Should().OnlyContain(w => w.Indexed == w.Latest, $"seed {seed}: a drained workspace is current");
        }

        advances.Should().BeGreaterThan(Seeds * 3, "the schedules must advance the watermark often, not only at the drain");
    }

    [Fact]
    public async Task The_oracle_catches_a_ticker_that_trusts_acknowledgements()
    {
        // Negative control: a "refresh" that reports success without making writes visible must be caught by the oracle
        // in some schedule, otherwise the property test above would prove nothing about refresh-awareness.
        var caught = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var world = new World(new Random(seed), honestRefresh: false);
            await world.RunAsync(ticks: 40, Ct);
            caught += world.Violations.Count > 0 ? 1 : 0;
        }

        caught.Should().BePositive();
    }

    [Fact]
    public void Freshness_levels_follow_the_lag()
    {
        var now = DateTimeOffset.Parse("2026-10-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var current = new SearchFreshnessReading(7, 7, 7, null, now);
        current.Level.Should().Be(SearchFreshnessLevel.Current);
        current.Lag.Should().Be(TimeSpan.Zero);
        current.PendingChanges.Should().Be(0);

        var updating = new SearchFreshnessReading(9, 7, 8, now.AddSeconds(-3), now);
        updating.Level.Should().Be(SearchFreshnessLevel.Updating);
        updating.Lag.Should().Be(TimeSpan.FromSeconds(3));
        updating.PendingChanges.Should().Be(2);

        (updating with { OldestUnreflectedCommittedAt = now.AddMinutes(-2) }).Level.Should().Be(SearchFreshnessLevel.Updating);
        (updating with { OldestUnreflectedCommittedAt = now.AddMinutes(-2).AddSeconds(-1) }).Level.Should().Be(SearchFreshnessLevel.Delayed);
        SearchFreshnessReading.Empty(now).IsCurrent.Should().BeTrue();
    }

    /// <summary>The model: workspaces on two indexes, their work records, an OpenSearch write buffer and refresh points.</summary>
    private sealed class World : ISearchWatermarkStore, ISearchIndexRefresher
    {
        private readonly Random _rng;
        private readonly bool _honestRefresh;
        private readonly SearchWatermarkAdvancer _advancer;

        public World(Random rng, bool honestRefresh)
        {
            _rng = rng;
            _honestRefresh = honestRefresh;
            Workspaces = [.. Enumerable.Range(0, 3).Select(i => new Workspace(Guid.CreateVersion7(), i < 2 ? 0 : 1))];
            _advancer = new SearchWatermarkAdvancer(this, this, NullLogger<SearchWatermarkAdvancer>.Instance);
        }

        public List<Workspace> Workspaces { get; }

        public List<string> Violations { get; } = [];

        public int Advances { get; private set; }

        /// <summary>When set, refreshes always succeed and no new work starts (drain phase).</summary>
        private bool Draining { get; set; }

        public async Task RunAsync(int ticks, CancellationToken cancellationToken)
        {
            for (var i = 0; i < ticks; i++)
            {
                Steps(_rng.Next(0, 6));
                await _advancer.TickAsync([.. Workspaces.Select(w => w.Id)], cancellationToken);
                CheckAll("after tick");
            }
        }

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            Draining = true;
            foreach (var w in Workspaces)
            {
                while (w.InFlight.Count > 0)
                {
                    Commit(w, w.InFlight[0]);
                }

                foreach (var item in w.Items)
                {
                    if (item.State is ItemState.Committed or ItemState.Failed)
                    {
                        item.State = ItemState.Committed;
                        Ack(item);
                    }

                    if (item.State == ItemState.Acked)
                    {
                        item.State = ItemState.Applied;
                    }
                }
            }

            await _advancer.TickAsync([.. Workspaces.Select(w => w.Id)], cancellationToken);
            CheckAll("after drain");
        }

        public Task<SearchFreshnessReading> ReadAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        {
            Steps(_rng.Next(0, 3));
            var reading = Snapshot(Find(workspaceId));
            Steps(_rng.Next(0, 3));
            return Task.FromResult(reading);
        }

        public Task<SearchFreshnessReading> AdvanceAsync(Guid workspaceId, long indexedThroughGeneration, CancellationToken cancellationToken = default)
        {
            Steps(_rng.Next(0, 3));
            var w = Find(workspaceId);
            var before = w.Indexed;
            w.Indexed = Math.Max(w.Indexed, Math.Min(indexedThroughGeneration, w.Latest));
            Advances += w.Indexed > before ? 1 : 0;
            Check(w, "on advance");
            return Task.FromResult(Snapshot(w));
        }

        public Task<IReadOnlySet<Guid>> RefreshAsync(IReadOnlyCollection<Guid> workspaceIds, CancellationToken cancellationToken)
        {
            Steps(_rng.Next(0, 3));
            var ok = new HashSet<Guid>();
            foreach (var index in workspaceIds.Select(id => Find(id).Index).Distinct().ToList())
            {
                // A refresh with a failed shard copy reports failure; some copies may still have refreshed.
                var failed = !Draining && _rng.Next(5) == 0;
                if (_honestRefresh && (!failed || _rng.Next(2) == 0))
                {
                    RefreshPoint(index);
                }

                if (!failed)
                {
                    ok.UnionWith(workspaceIds.Where(id => Find(id).Index == index));
                }

                Steps(_rng.Next(0, 2));
            }

            return Task.FromResult<IReadOnlySet<Guid>>(ok);
        }

        private void Steps(int count)
        {
            for (var i = 0; i < count && !Draining; i++)
            {
                var w = Workspaces[_rng.Next(Workspaces.Count)];
                switch (_rng.Next(9))
                {
                    case 0:
                        w.InFlight.Add(_rng.Next(1, 4));
                        break;
                    case 1 or 2 when w.InFlight.Count > 0:
                        // Transactions commit in any order relative to their start; the late-lock counter numbers commits.
                        Commit(w, w.InFlight[_rng.Next(w.InFlight.Count)]);
                        break;
                    case 3 when w.InFlight.Count > 0:
                        w.InFlight.RemoveAt(_rng.Next(w.InFlight.Count)); // rollback: no generation is consumed
                        break;
                    case 4 or 5 when Pick(w, ItemState.Committed) is { } committed:
                        Ack(committed); // OpenSearch acknowledged the write; PostgreSQL does not know yet
                        break;
                    case 6 when Pick(w, ItemState.Acked) is { } acked:
                        acked.State = ItemState.Applied;
                        break;
                    case 7 when Pick(w, ItemState.Committed) is { } failing:
                        failing.State = _rng.Next(2) == 0 ? ItemState.Failed : failing.State;
                        break;
                    case 7 when Pick(w, ItemState.Failed) is { } failed:
                        failed.State = ItemState.Committed; // replay
                        break;
                    case 8 when _honestRefresh:
                        RefreshPoint(_rng.Next(2)); // the index's own refresh_interval
                        break;
                }
            }
        }

        private static void Commit(Workspace w, int items)
        {
            w.InFlight.Remove(items);
            w.Latest++;
            for (var i = 0; i < items; i++)
            {
                w.Items.Add(new Item(w.Latest));
            }
        }

        private static void Ack(Item item)
        {
            item.State = ItemState.Acked;
            item.Acknowledged = true;
        }

        private void RefreshPoint(int index)
        {
            foreach (var item in Workspaces.Where(w => w.Index == index).SelectMany(w => w.Items))
            {
                // Every write acknowledged before this point becomes searchable.
                item.Visible |= item.Acknowledged;
            }
        }

        private Item? Pick(Workspace w, ItemState state)
        {
            var candidates = w.Items.Where(i => i.State == state).ToList();
            return candidates.Count == 0 ? null : candidates[_rng.Next(candidates.Count)];
        }

        private static SearchFreshnessReading Snapshot(Workspace w)
        {
            var unapplied = w.Items.Where(i => i.State != ItemState.Applied).Select(i => i.Generation).DefaultIfEmpty(long.MaxValue).Min();
            var applied = unapplied == long.MaxValue ? w.Latest : Math.Min(w.Latest, unapplied - 1);
            return new SearchFreshnessReading(w.Latest, w.Indexed, applied, null, DateTimeOffset.UnixEpoch);
        }

        private void CheckAll(string when)
        {
            foreach (var w in Workspaces)
            {
                Check(w, when);
            }
        }

        private void Check(Workspace w, string when)
        {
            // Highest N with every item of every generation <= N visible (generations are gap-free).
            var visibleThrough = w.Items.Where(i => !i.Visible).Select(i => i.Generation - 1).DefaultIfEmpty(w.Latest).Min();
            if (w.Indexed > visibleThrough)
            {
                Violations.Add($"{when}: workspace {w.Index} watermark {w.Indexed} passes searchable generation {visibleThrough}");
            }

            if (w.Indexed < w.LastSeen)
            {
                Violations.Add($"{when}: watermark moved backwards from {w.LastSeen} to {w.Indexed}");
            }

            w.LastSeen = w.Indexed;
        }

        private Workspace Find(Guid id) => Workspaces.Single(w => w.Id == id);
    }

    private sealed class Workspace(Guid id, int index)
    {
        public Guid Id { get; } = id;

        public int Index { get; } = index;

        public long Latest { get; set; }

        public long Indexed { get; set; }

        public long LastSeen { get; set; }

        public List<int> InFlight { get; } = [];

        public List<Item> Items { get; } = [];
    }

    private enum ItemState
    {
        Committed,
        Acked,
        Applied,
        Failed,
    }

    private sealed class Item(long generation)
    {
        public long Generation { get; } = generation;

        public ItemState State { get; set; }

        /// <summary>OpenSearch acknowledged the item's write (it becomes visible at the next refresh point).</summary>
        public bool Acknowledged { get; set; }

        public bool Visible { get; set; }
    }
}
