using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Audit;
using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.IntegrationTests.Jobs;
using Opportunity.IntegrationTests.Messaging;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs.Dispatch;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// E06-T04: the outbox dispatcher against PostgreSQL and the real RabbitMQ transport — LISTEN/NOTIFY latency, several
/// instances, crash and restart, broker outage with the outbox-age alert, backlog drain with per-workspace fairness and
/// the security lane first, graceful shutdown, and job chunk dispatch end to end with the job chunk consumer.
/// </summary>
[Collection(SearchWorkBrokerGroup.Name)]
public sealed class OutboxDispatcherTests(MigrationPostgresFixture postgres, RabbitMqFixture rabbit)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_committed_outbox_row_is_published_within_100_ms_p95_woken_by_notify_not_by_polling()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var ws = await db.Core.CreateWorkspaceAsync();
        var publisher = new RecordingPublisher(harness.Publisher);
        using var meters = new MeterCapture();

        // Polling every 30 s: only the NOTIFY wake-up can explain sub-second publishes.
        var options = Slow(RunningDispatcher.FastOptions("dispatcher-latency"));
        await using var dispatcher = RunningDispatcher.Start(db, publisher, options, meters.Metrics);
        await DispatcherTestData.EventuallyAsync(() => Task.FromResult(dispatcher.Dispatcher.Listening), because: "LISTEN subscribed");
        await db.AddOutboxRowsAsync(ws, 1, SearchChangeMask.Coding);
        await DispatcherTestData.EventuallyAsync(() => Task.FromResult(!publisher.Confirmed.IsEmpty), because: "warm-up row published");

        const int Samples = 40;
        var latencies = new List<double>();
        var commits = new List<double>();
        var raw = new List<double>();
        for (var i = 0; i < Samples; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await harness.Publisher.PublishAsync(MessagingHarness.ChunkTask(WorkQueues.IndexBulk), Ct);
            raw.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        TestContext.Current.SendDiagnosticMessage($"raw publish: p50 {raw.Order().ElementAt(Samples / 2):F1} ms, p95 {raw.Order().ElementAt(37):F1} ms");
        for (var i = 0; i < Samples; i++)
        {
            var before = publisher.Confirmed.Count;
            var started = Stopwatch.GetTimestamp();
            await db.AddOutboxRowsAsync(ws, 1, i % 4 == 0 ? SearchChangeMask.Security : SearchChangeMask.Coding);
            commits.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await DispatcherTestData.EventuallyAsync(
                () => Task.FromResult(publisher.Confirmed.Count > before), TimeSpan.FromSeconds(10), "row published by NOTIFY");
            latencies.Add(Stopwatch.GetElapsedTime(started, publisher.Confirmed.Last().ConfirmedAt).TotalMilliseconds);
            await Task.Delay(20, Ct);
        }

        var p95 = latencies.Order().ElementAt((int)Math.Ceiling(0.95 * Samples) - 1);
        TestContext.Current.SendDiagnosticMessage($"commit: p50 {commits.Order().ElementAt(Samples / 2):F1} ms, p95 {commits.Order().ElementAt(37):F1} ms");
        TestContext.Current.SendDiagnosticMessage($"insert → confirmed publish: p50 {latencies.Order().ElementAt(Samples / 2):F1} ms, p95 {p95:F1} ms");
        // The absolute 100 ms gate holds on an idle machine only (Q-44: absolute latency gates are comparative on shared
        // hardware), so it is enforced where OPPORTUNITY_STRICT_LATENCY=1 (benchmark tiers). Everywhere else the test
        // still proves the wake-up is NOTIFY-driven: polling is 30 s, so p95 must stay far below it.
        var strict = Environment.GetEnvironmentVariable("OPPORTUNITY_STRICT_LATENCY") == "1";
        p95.Should().BeLessThan(strict ? 100 : 5_000, "E06-T04: outbox insert → message published p95 < 100 ms at idle (includes the commit); commit p50 {0:F1} p95 {1:F1}, raw publish p50 {2:F1} p95 {3:F1}, e2e p50 {4:F1}", commits.Order().ElementAt(Samples / 2), commits.Order().ElementAt(37), raw.Order().ElementAt(Samples / 2), raw.Order().ElementAt(37), latencies.Order().ElementAt(Samples / 2));
        (await db.OutboxStatusesAsync(ws))[SearchOutboxStatus.Dispatched].Should().Be(Samples + 1);
        meters.Values(OpportunityMetricCatalog.OutboxPublishLatency.Name).Should().HaveCount(Samples + 1, "every confirm records commit → confirm latency");
        meters.Values(OpportunityMetricCatalog.DispatcherPublished.Name).Sum().Should().Be(Samples + 1);
    }

    [Fact]
    public async Task Three_dispatchers_publish_every_row_task_and_chunk_once_and_the_job_completes_end_to_end()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var workspaces = new[] { await db.Core.CreateWorkspaceAsync(), await db.Core.CreateWorkspaceAsync(), await db.Core.CreateWorkspaceAsync() };
        var jobs = new Dictionary<Guid, Guid>();
        var indexed = new Dictionary<Guid, Guid>();
        foreach (var ws in workspaces)
        {
            jobs[ws] = await db.RunningJobAsync(ws, chunks: 6);

            // The index tasks belong to an earlier job, paused so that its own chunks are not dispatched.
            indexed[ws] = await db.RunningJobAsync(ws, chunks: 1);
            await db.Jobs.PauseAsync(ws, indexed[ws], "test", Ct);
        }

        // The bulk-coding worker: the real job chunk consumer behind the real transport.
        var executor = new StateBasedExecutor();
        var consumer = ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter());
        for (var i = 0; i < 3; i++)
        {
            await harness.SubscribeAsync(WorkQueues.BulkCoding, (message, ct) => consumer.HandleAsync((JobChunkMessage)message.Payload, message, ct));
        }

        var publisher = new RecordingPublisher(harness.Publisher);
        var dispatchers = new List<RunningDispatcher>();
        for (var i = 0; i < 3; i++)
        {
            // Small batches and several passes at once per instance, so the instances contend for the same rows.
            dispatchers.Add(RunningDispatcher.Start(
                db, publisher, RunningDispatcher.FastOptions($"dispatcher-{i}") with { BatchSize = 7, MaxConcurrency = 3 }));
        }

        try
        {
            await Task.WhenAll(workspaces.Select(async ws =>
            {
                for (var i = 0; i < 40; i++)
                {
                    await db.AddOutboxRowsAsync(ws, 1 + (i % 3), i % 5 == 0 ? SearchChangeMask.Security : SearchChangeMask.Coding);
                }

                await db.AddIndexTasksAsync(ws, indexed[ws], 10, SearchChangeMask.Coding);
                await db.AddIndexTasksAsync(ws, indexed[ws], 3, SearchChangeMask.Security);
            }));

            foreach (var ws in workspaces)
            {
                await DispatcherTestData.EventuallyAsync(async () =>
                {
                    var statuses = await db.OutboxStatusesAsync(ws);
                    return statuses[SearchOutboxStatus.Dispatched] == statuses.Values.Sum()
                        && await db.Core.ScalarAsync<long>(
                            "SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status <> 2", ("ws", ws)) == 0
                        && (await db.Jobs.GetAsync(ws, jobs[ws], Ct))!.Status == JobStatus.Completed;
                }, TimeSpan.FromSeconds(60), $"workspace {ws} fully dispatched and its job completed");
            }
        }
        finally
        {
            foreach (var dispatcher in dispatchers)
            {
                await dispatcher.DisposeAsync();
            }
        }

        foreach (var ws in workspaces)
        {
            publisher.OutboxIds(ws).Should().BeEquivalentTo(await db.OutboxIdsAsync(ws), "no row lost, none published twice");
        }

        var tasks = publisher.Confirmed.Select(p => p.Payload).OfType<IndexChunkTaskMessage>().Select(t => t.TaskId).ToList();
        tasks.Should().HaveCount(3 * 13).And.OnlyHaveUniqueItems();
        publisher.Confirmed.Count(p => p.Queue == WorkQueues.IndexSecurityBulk).Should().Be(3 * 3, "security-affecting tasks go to lane L2");
        executor.Executions.Should().HaveCount(3 * 6).And.OnlyContain(e => e.Value == 1, "every chunk ran exactly once");
        publisher.Confirmed.Count(p => p.Queue == WorkQueues.BulkCoding).Should().Be(3 * 6);
        var indexMessages = await harness.CountAsync(WorkQueues.IndexInteractive.Name) + await harness.CountAsync(WorkQueues.IndexSecurity.Name);
        indexMessages.Should().Be((uint)publisher.Confirmed.Count(p => p.OutboxId is not null));
    }

    [Fact]
    public async Task A_dispatcher_crashing_between_confirm_and_mark_is_taken_over_and_each_row_is_published_at_most_twice()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var ws = await db.Core.CreateWorkspaceAsync();
        var job = await db.RunningJobAsync(ws, chunks: 2);
        await db.AddOutboxRowsAsync(ws, 25, SearchChangeMask.Coding);
        var tasks = await db.AddIndexTasksAsync(ws, job, 5);

        // A publishes (the broker confirms) and then hangs as if the process froze before marking anything Dispatched.
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisherA = new RecordingPublisher(harness.Publisher)
        {
            After = async ct =>
            {
                crashed.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        var a = RunningDispatcher.Start(db, publisherA, Crashable("dispatcher-a"));
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(500, Ct);
        await a.DisposeAsync();
        a.Stopped.Should().BeTrue();
        (await db.OutboxStatusesAsync(ws))[SearchOutboxStatus.Dispatched].Should().Be(0, "A never marked a row");
        (await db.OutboxStatusesAsync(ws))[SearchOutboxStatus.Claimed].Should().BeGreaterThan(0, "A's claims are left behind");

        // B starts afterwards (a restart, or another replica) and takes over once A's claims expire.
        var publisherB = new RecordingPublisher(harness.Publisher);
        await using var b = RunningDispatcher.Start(db, publisherB, RunningDispatcher.FastOptions("dispatcher-b"));
        await DispatcherTestData.EventuallyAsync(async () =>
            (await db.OutboxStatusesAsync(ws))[SearchOutboxStatus.Dispatched] == 25
            && await db.Core.ScalarAsync<long>(
                "SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status = 2", ("ws", ws)) == 5,
            TimeSpan.FromSeconds(30), "B dispatched everything");

        var all = publisherA.OutboxIds(ws).Concat(publisherB.OutboxIds(ws)).ToList();
        all.Distinct().Should().BeEquivalentTo(await db.OutboxIdsAsync(ws), "nothing is lost");
        all.GroupBy(id => id).Should().OnlyContain(g => g.Count() <= 2, "a crash costs at most one duplicate per row");
        publisherB.OutboxIds(ws).Should().OnlyHaveUniqueItems();
        publisherA.Confirmed.Concat(publisherB.Confirmed).Select(p => p.Payload).OfType<IndexChunkTaskMessage>().Select(t => t.TaskId)
            .Distinct().Should().BeEquivalentTo(tasks);
        (await harness.CountAsync(WorkQueues.IndexInteractive.Name)).Should().Be((uint)all.Count, "every confirmed publish is in the queue");
    }

    [Fact]
    public async Task During_a_broker_outage_rows_accumulate_the_outbox_age_alert_fires_and_everything_drains_after_recovery()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var proxy = await rabbit.CreateFaultProxyAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUriVia(proxy), vhost.AmqpUri);
        var ws = await db.Core.CreateWorkspaceAsync();
        using var meters = new MeterCapture();
        var options = RunningDispatcher.FastOptions("dispatcher-outage") with { OutboxAgeAlertThreshold = TimeSpan.FromSeconds(2) };
        await using var dispatcher = RunningDispatcher.Start(db, new RecordingPublisher(harness.Publisher), options, meters.Metrics);
        await db.AddOutboxRowsAsync(ws, 1, SearchChangeMask.Coding);
        await DispatcherTestData.EventuallyAsync(async () => (await db.OutboxStatusesAsync(ws))[SearchOutboxStatus.Dispatched] == 1);

        await proxy.CutAsync(Ct);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                await db.AddOutboxRowsAsync(ws, 3, i % 2 == 0 ? SearchChangeMask.Security : SearchChangeMask.Coding);
            }

            await DispatcherTestData.EventuallyAsync(
                () => Task.FromResult(dispatcher.Dispatcher.Backlog.Alerting.Count == 2), TimeSpan.FromSeconds(30), "both lanes alert");
            var during = await db.OutboxStatusesAsync(ws);
            during[SearchOutboxStatus.Dispatched].Should().Be(1, "nothing is marked Dispatched without a broker confirm");
            (during[SearchOutboxStatus.Pending] + during[SearchOutboxStatus.Claimed]).Should().Be(30, "rows accumulate");
            during[SearchOutboxStatus.Failed].Should().Be(0);
            meters.ObserveGauges();
            meters.Values(OpportunityMetricCatalog.OutboxOldestAge.Name).Max().Should().BeGreaterThan(2, "outbox_oldest_age_seconds crosses the alert threshold");
            meters.Values(OpportunityMetricCatalog.OutboxPending.Name).Sum().Should().Be(31, "every row not yet Applied, the dispatched one included");
            meters.Values(OpportunityMetricCatalog.DispatcherPublished.Name, DispatchMetrics.UnconfirmedOutcome).Sum().Should().BeGreaterThan(0);
        }
        finally
        {
            await proxy.RestoreAsync(Ct);
        }

        await DispatcherTestData.EventuallyAsync(
            async () => (await db.OutboxStatusesAsync(ws))[SearchOutboxStatus.Dispatched] == 31, TimeSpan.FromSeconds(60), "drained after recovery");
        await DispatcherTestData.EventuallyAsync(
            () => Task.FromResult(dispatcher.Dispatcher.Backlog.Alerting.Count == 0), TimeSpan.FromSeconds(10), "the alert clears");
        (await harness.CountAsync(WorkQueues.IndexInteractive.Name) + await harness.CountAsync(WorkQueues.IndexSecurity.Name))
            .Should().BeGreaterThanOrEqualTo(31);
    }

    [Fact]
    public async Task A_backlog_drains_in_round_robin_batches_so_other_workspaces_and_the_security_lane_are_not_starved()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var busy = await db.Core.CreateWorkspaceAsync();
        var quiet = await db.Core.CreateWorkspaceAsync();
        var secure = await db.Core.CreateWorkspaceAsync();
        const int Backlog = 2_000;
        const int Batch = 100;
        await db.AddOutboxRowsAsync(busy, Backlog, SearchChangeMask.Coding);

        // One pass at a time, each publish slowed a little, so the backlog takes a while to drain.
        var publisher = new RecordingPublisher(harness.Publisher) { Before = _ => Task.Delay(25, CancellationToken.None) };
        await using var dispatcher = RunningDispatcher.Start(
            db, publisher, RunningDispatcher.FastOptions("dispatcher-fair") with { BatchSize = Batch, MaxConcurrency = 1 });
        await DispatcherTestData.EventuallyAsync(() => Task.FromResult(publisher.Confirmed.Count >= Batch), because: "the drain started");

        var publishedBefore = publisher.Confirmed.Count;
        await db.AddOutboxRowsAsync(quiet, 1, SearchChangeMask.Coding);
        await db.AddOutboxRowsAsync(secure, 1, SearchChangeMask.Security | SearchChangeMask.Coding);
        await DispatcherTestData.EventuallyAsync(
            async () => (await db.OutboxStatusesAsync(busy))[SearchOutboxStatus.Dispatched] == Backlog, TimeSpan.FromSeconds(60), "backlog drained");
        await DispatcherTestData.EventuallyAsync(async () =>
            (await db.OutboxStatusesAsync(quiet))[SearchOutboxStatus.Dispatched] == 1
            && (await db.OutboxStatusesAsync(secure))[SearchOutboxStatus.Dispatched] == 1);

        var order = publisher.Confirmed.ToList();
        var quietAt = order.FindIndex(p => p.WorkspaceId == quiet);
        var secureAt = order.FindIndex(p => p.WorkspaceId == secure);
        TestContext.Current.SendDiagnosticMessage($"committed after {publishedBefore} publishes; security row at {secureAt}, quiet row at {quietAt}");
        (secureAt - publishedBefore).Should().BeLessThanOrEqualTo(2 * Batch, "a security wake-up is taken before any other queued work");
        (quietAt - publishedBefore).Should().BeLessThanOrEqualTo(3 * Batch, "another workspace waits for at most a round of batches, not the backlog");
        quietAt.Should().BeLessThan(Backlog - Batch, "the quiet workspace did not wait for the backlog to drain");
        publisher.OutboxIds(busy).Should().HaveCount(Backlog).And.OnlyHaveUniqueItems();
        (await harness.CountAsync(WorkQueues.IndexInteractive.Name)).Should().Be(Backlog + 1);
        (await harness.CountAsync(WorkQueues.IndexSecurity.Name)).Should().Be(1);
    }

    [Fact]
    public async Task A_lost_listen_session_is_re_established_and_polling_covers_the_gap()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var ws = await db.Core.CreateWorkspaceAsync();
        var publisher = new RecordingPublisher(harness.Publisher);
        await using var dispatcher = RunningDispatcher.Start(db, publisher, Slow(RunningDispatcher.FastOptions("dispatcher-relisten")));
        await DispatcherTestData.EventuallyAsync(() => Task.FromResult(dispatcher.Dispatcher.Listening), because: "LISTEN subscribed");

        var terminated = await db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE datname = current_database() AND pid <> pg_backend_pid() AND query LIKE 'LISTEN%') t");
        terminated.Should().Be(1, "the dispatcher holds one LISTEN session");

        await db.AddOutboxRowsAsync(ws, 2, SearchChangeMask.Coding);
        await DispatcherTestData.EventuallyAsync(
            async () => (await db.OutboxStatusesAsync(ws))[SearchOutboxStatus.Dispatched] == 2, TimeSpan.FromSeconds(10),
            "published without waiting for the 30 s poll");
        await DispatcherTestData.EventuallyAsync(() => Task.FromResult(dispatcher.Dispatcher.Listening), because: "LISTEN re-established");

        var before = publisher.Confirmed.Count;
        await db.AddOutboxRowsAsync(ws, 1, SearchChangeMask.Coding);
        await DispatcherTestData.EventuallyAsync(
            () => Task.FromResult(publisher.Confirmed.Count > before), TimeSpan.FromSeconds(5), "the new session wakes the dispatcher again");
    }

    [Fact]
    public async Task Shutdown_lets_a_running_pass_finish_and_mark_its_rows_instead_of_abandoning_its_claims()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var ws = await db.Core.CreateWorkspaceAsync();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher(harness.Publisher)
        {
            Before = async _ =>
            {
                inFlight.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            },
        };
        var dispatcher = RunningDispatcher.Start(db, publisher, RunningDispatcher.FastOptions("dispatcher-stop"));
        await db.AddOutboxRowsAsync(ws, 5, SearchChangeMask.Coding);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await dispatcher.StopAsync();
        await dispatcher.DisposeAsync();

        var statuses = await db.OutboxStatusesAsync(ws);
        statuses[SearchOutboxStatus.Dispatched].Should().Be(5, "the pass that was publishing finished within the shutdown grace");
        statuses[SearchOutboxStatus.Claimed].Should().Be(0);
    }

    [Fact]
    public async Task Two_dispatchers_keep_a_job_within_its_in_flight_limit_and_a_retried_chunk_is_dispatched_again()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var harness = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var ws = await db.Core.CreateWorkspaceAsync();
        var job = await db.RunningJobAsync(ws, chunks: 12);

        var running = 0;
        var maxRunning = 0;
        var failedOnce = new ConcurrentDictionary<int, bool>();
        var executor = new StateBasedExecutor
        {
            Before = async (context, ct) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                try
                {
                    await Task.Delay(150, ct);
                    if (context.Chunk.Sequence == 3 && failedOnce.TryAdd(3, true))
                    {
                        throw new TimeoutException("Transient failure of the first attempt.");
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            },
        };
        var consumer = ChunkMessages.Consumer(db.Chunks, executor, new InMemoryAuditEventWriter(),
            new Opportunity.Jobs.JobLeaseOptions { LeaseDuration = TimeSpan.FromSeconds(30) });
        for (var i = 0; i < 8; i++)
        {
            await harness.SubscribeAsync(WorkQueues.BulkCoding, (message, ct) => consumer.HandleAsync((JobChunkMessage)message.Payload, message, ct));
        }

        var publisher = new RecordingPublisher(harness.Publisher);
        await using var a = RunningDispatcher.Start(db, publisher, RunningDispatcher.FastOptions("dispatcher-job-a"));
        await using var b = RunningDispatcher.Start(db, publisher, RunningDispatcher.FastOptions("dispatcher-job-b"));

        // The transient failure puts chunk 3 in RetryWait; its backoff (5 s) is shortened instead of waited for. The new
        // available_at lies in the past: the status read above it may be stale, and a chunk re-dispatched meanwhile must
        // stay due for the worker whose claim transaction began before this update (else the claim says NotDue, the
        // message is dropped and the chunk waits for the 60 s re-dispatch).
        var maxInFlight = 0L;
        try
        {
            await DispatcherTestData.EventuallyAsync(async () =>
            {
                await db.Core.ExecuteAsync(
                    "UPDATE opportunity.job_chunk SET available_at = now() - interval '1 minute' " +
                    "WHERE job_id = @job AND status = @retry AND available_at > now() - interval '1 minute'",
                    ("job", job), ("retry", (short)JobChunkStatus.RetryWait));
                maxInFlight = Math.Max(maxInFlight, await db.Core.ScalarAsync<long>(
                    "SELECT count(*) FROM opportunity.job_chunk WHERE job_id = @job " +
                    "AND (status IN (2, 3) OR (claim_owner IS NOT NULL AND claim_expires_at > now()))", ("job", job)));
                return (await db.Jobs.GetAsync(ws, job, Ct))!.Status == JobStatus.Completed;
            }, TimeSpan.FromSeconds(60), "the job completed");
        }
        catch (TimeoutException ex)
        {
            var chunks = await db.Core.ScalarAsync<string>(
                "SELECT string_agg(format('seq %s status %s attempts %s available %s dispatched %s claim %s until %s lease until %s', " +
                "chunk_sequence, status, attempt_count, available_at, dispatched_at, claim_owner, claim_expires_at, lease_expires_at), " +
                "E'\\n' ORDER BY chunk_sequence) FROM opportunity.job_chunk WHERE job_id = @job", ("job", job));
            throw new TimeoutException($"{ex.Message} Chunks at {DateTimeOffset.UtcNow:O}:\n{chunks}", ex);
        }

        maxInFlight.Should().BeLessThanOrEqualTo(4, "ADR-010 §6: in PostgreSQL too, at most 4 chunks of a job are in flight");
        maxRunning.Should().BeLessThanOrEqualTo(4, "ADR-010 §6: at most 4 chunks of a job in flight, across dispatchers");
        maxRunning.Should().BeGreaterThan(1, "chunks of a job do run in parallel");
        executor.Executions.Should().HaveCount(12);
        executor.Executions.Where(e => e.Value != 1).Should().ContainSingle("only the failed chunk ran twice");
        var published = publisher.Confirmed.Select(p => p.Payload).OfType<JobChunkMessage>().GroupBy(m => m.Sequence).ToDictionary(g => g.Key, g => g.Count());
        published.Should().HaveCount(12);
        published.Where(p => p.Value != 1).Select(p => p.Key).Should().Equal([3], "the RetryWait chunk is re-dispatched once, the rest once each");
        (await db.Jobs.GetAsync(ws, job, Ct))!.Counters.ChunksCommitted.Should().Be(12);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>Polling every 30 s, so a prompt publish proves the wake-up path.</summary>
    private static OutboxDispatcherOptions Slow(OutboxDispatcherOptions o) => o with
    {
        OutboxPollInterval = TimeSpan.FromSeconds(30),
        IndexTaskPollInterval = TimeSpan.FromSeconds(30),
        JobChunkPollInterval = TimeSpan.FromSeconds(30),
    };

    /// <summary>Short claims and no shutdown grace: stopping it is a crash, and its claims expire quickly.</summary>
    private static OutboxDispatcherOptions Crashable(string owner) =>
        RunningDispatcher.FastOptions(owner) with { ClaimDuration = TimeSpan.FromSeconds(2), ShutdownGrace = TimeSpan.Zero };

    /// <summary>Collects measurements of the application meter of one <see cref="OpportunityMetrics"/> instance.</summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly ServiceProvider _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Name, double Value, string? Outcome)> _values = new();

        public MeterCapture()
        {
            Metrics = new OpportunityMetrics(_provider.GetRequiredService<IMeterFactory>());
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpportunityTelemetry.MeterName && instrument.Meter.Scope == _provider.GetRequiredService<IMeterFactory>())
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => _values.Enqueue((i.Name, v, Outcome(tags))));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => _values.Enqueue((i.Name, v, Outcome(tags))));
            _listener.Start();
        }

        public OpportunityMetrics Metrics { get; }

        public void ObserveGauges() => _listener.RecordObservableInstruments();

        public IReadOnlyList<double> Values(string name, string? outcome = null) =>
            [.. _values.Where(v => v.Name == name && (outcome is null || v.Outcome == outcome)).Select(v => v.Value)];

        public void Dispose()
        {
            _listener.Dispose();
            _provider.Dispose();
        }

        private static string? Outcome(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == TelemetryAttributes.Outcome)
                {
                    return tag.Value as string;
                }
            }

            return null;
        }
    }
}
