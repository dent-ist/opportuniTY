using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs.Dispatch;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// E19-T05: the dispatcher's pipeline gauges (<c>opportunity.index.chunk_tasks</c>, <c>opportunity.index.chunk_task.oldest_age</c>,
/// <c>opportunity.job.chunk.backlog</c>, <c>opportunity.job.chunk.oldest_age</c>, <c>opportunity.jobs.active</c>) read
/// from PostgreSQL under RLS. An induced stuck chunk and stuck index task cross the 15-minute alert threshold, and every
/// measurement carries only the catalog's low-cardinality attributes.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class PipelineBacklogMetricsTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stuck_job_chunks_and_index_tasks_are_reported_with_their_age_and_only_catalog_attributes()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 6);
        var quiet = await db.WorkspaceAsync();
        var snapshot = Guid.CreateVersion7();
        ChunkPlan Plan(long from) => new(ChunkMembership.SnapshotRange(snapshot, from, from + 1), 2);

        // Chunk 1 commits and leaves a bulk-lane IndexChunkTask; chunk 2 is claimed (Running); chunk 3 stays Pending.
        var first = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [Plan(1), Plan(3), Plan(5)], snapshotId: snapshot);
        var applied = await db.Core.Coding.ApplyChunkAsync(first, new CodingWriteRequest
        {
            WorkspaceId = w.Id,
            IdempotencyKey = first.IdempotencyKey,
            Actor = new CodingActor(SearchWorkDatabase.Reviewer, CodingActorType.BulkHuman),
            JobId = first.Lease.JobId,
            Documents = [.. w.Documents.Take(2).Select(d => new CodingTarget(d, 1))],
            Operations = [CodingFieldOperation.Set(w.Responsive, System.Text.Json.Nodes.JsonValue.Create(true))],
        }, Ct);
        applied.Committed.Should().BeTrue();
        (await db.Chunks.ClaimNextAsync(w.Id, first.Lease.JobId, "worker-2", TimeSpan.FromMinutes(30), Ct)).Claimed.Should().BeTrue();
        var task = applied.IndexTaskId!.Value;
        (await db.Tasks.LeaseAsync(w.Id, task, "indexer-1", TimeSpan.FromMinutes(30), Ct)).Outcome.Should().Be(IndexTaskLeaseOutcome.Leased);

        // Induced failure: both have been running for longer than the ADR-010 maximum of 15 minutes.
        await db.Core.ExecuteAsync(
            "UPDATE opportunity.job_chunk SET claimed_at = now() - interval '16 minutes' WHERE workspace_id = @ws AND status = 3", ("ws", w.Id));
        await db.Core.ExecuteAsync(
            "UPDATE opportunity.index_chunk_task SET started_at = now() - interval '20 minutes' WHERE workspace_id = @ws", ("ws", w.Id));

        var backlog = await db.Maintenance.GetPipelineBacklogAsync(w.Id, Ct);
        backlog.IndexTasks.Should().ContainSingle().Which.Should().Match<IndexTaskBacklog>(t =>
            t.Status == IndexChunkTaskStatus.Running && t.Lane == MessageLane.Bulk && t.Count == 1);
        backlog.JobChunks.Select(c => (c.JobType, c.Status, c.Count)).Should().BeEquivalentTo(
            [(JobType.BulkCoding, JobChunkStatus.Running, 1L), (JobType.BulkCoding, JobChunkStatus.Pending, 1L)]);
        backlog.ActiveJobs.Should().Equal(new ActiveJobCount(JobType.BulkCoding, 1));
        (await db.Maintenance.GetPipelineBacklogAsync(quiet.Id, Ct)).Should().BeEquivalentTo(PipelineBacklog.Empty, "RLS: one workspace's backlog only");

        using var meters = new GaugeCapture();
        var monitor = new OutboxBacklogMonitor(
            db.Maintenance, new OutboxDispatcherOptions(), TimeProvider.System, NullLogger<OutboxBacklogMonitor>.Instance, meters.Metrics);
        await monitor.SampleAsync([w.Id, quiet.Id], Ct);
        meters.Observe();

        meters.Value(OpportunityMetricCatalog.IndexChunkTasks, ("opportunity.status", "Running"), ("opportunity.lane", "bulk")).Should().Be(1);
        meters.Value(OpportunityMetricCatalog.IndexChunkTaskOldestAge, ("opportunity.status", "Running"), ("opportunity.lane", "bulk"))
            .Should().BeGreaterThanOrEqualTo(900, "IndexChunkTaskRunningTooLong fires at 15 minutes");
        meters.Value(OpportunityMetricCatalog.JobChunkOldestAge, ("opportunity.job.type", "BulkCoding"), ("opportunity.status", "Running"))
            .Should().BeGreaterThanOrEqualTo(900, "JobChunkRunningTooLong fires at 15 minutes");
        meters.Value(OpportunityMetricCatalog.JobChunkBacklog, ("opportunity.job.type", "BulkCoding"), ("opportunity.status", "Pending")).Should().Be(1);
        meters.Value(OpportunityMetricCatalog.JobsActive, ("opportunity.job.type", "BulkCoding")).Should().Be(1);
        meters.Value(OpportunityMetricCatalog.JobsActive, ("opportunity.job.type", "Import")).Should().Be(0, "every job type reports, zero included");
        meters.Value(OpportunityMetricCatalog.IndexChunkTasks, ("opportunity.status", "Failed"), ("opportunity.lane", "security-bulk")).Should().Be(0);

        foreach (var definition in new[]
        {
            OpportunityMetricCatalog.IndexChunkTasks, OpportunityMetricCatalog.IndexChunkTaskOldestAge, OpportunityMetricCatalog.JobChunkBacklog,
            OpportunityMetricCatalog.JobChunkOldestAge, OpportunityMetricCatalog.JobsActive, OpportunityMetricCatalog.OutboxPending,
        })
        {
            var series = meters.Series(definition.Name);
            series.Should().NotBeEmpty(definition.Name);
            series.Should().OnlyContain(tags => tags.Keys.All(definition.Attributes.Contains), "{0} carries only catalog attributes", definition.Name);
            series.SelectMany(tags => tags.Values).Should().NotContain(v => v == w.Id.ToString() || v == task.ToString(), "no ids as metric labels");
        }
    }

    /// <summary>Records the last observation of each gauge series of one <see cref="OpportunityMetrics"/> instance.</summary>
    private sealed class GaugeCapture : IDisposable
    {
        private readonly ServiceProvider _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<(string Name, string Tags), (IReadOnlyDictionary<string, string> Tags, double Value)> _values = new();

        public GaugeCapture()
        {
            var factory = _provider.GetRequiredService<IMeterFactory>();
            Metrics = new OpportunityMetrics(factory);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Scope == factory)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
            _listener.Start();
        }

        public OpportunityMetrics Metrics { get; }

        public void Observe() => _listener.RecordObservableInstruments();

        public double Value(MetricDefinition definition, params (string Key, string Value)[] tags)
        {
            var key = (definition.Name, Key(tags.ToDictionary(t => t.Key, t => t.Value)));
            _values.TryGetValue(key, out var value).Should().BeTrue("{0} has a series {1}", definition.Name, key.Item2);
            return value.Value;
        }

        public IReadOnlyList<IReadOnlyDictionary<string, string>> Series(string name) =>
            [.. _values.Where(v => v.Key.Name == name).Select(v => v.Value.Tags)];

        public void Dispose()
        {
            _listener.Dispose();
            _provider.Dispose();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            }

            _values[(instrument.Name, Key(map))] = (map, value);
        }

        private static string Key(IReadOnlyDictionary<string, string> tags) =>
            string.Join(",", tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"));
    }
}
