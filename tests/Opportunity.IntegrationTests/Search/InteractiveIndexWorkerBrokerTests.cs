using System.Diagnostics;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Opportunity.Application.Coding;
using Opportunity.Application.Messaging;
using Opportunity.Core.Coding;
using Opportunity.Core.SearchWork;
using Opportunity.Hosting.Workers;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Messaging;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.SearchWork;
using Opportunity.Messaging;
using Opportunity.Search.Projection;
using Opportunity.Search.Writing;
using Opportunity.Testing.OpenSearch;
using Opportunity.Testing.RabbitMq;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T03 end to end: coding commits → outbox dispatcher → RabbitMQ lanes → the indexing worker module exactly as the
/// worker host composes it (<see cref="IndexingWorkerModule"/>) → OpenSearch.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class InteractiveIndexWorkerBrokerTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Single coding → searchable, measured from before the coding transaction to the first search hit at the new
    /// version (refresh 1 s). The p95 &lt; 1 s target is asserted only under OPPORTUNITY_STRICT_LATENCY=1 (Q-44); elsewhere
    /// the samples are reported and each must merely become searchable. A security-lane coding takes the L0 queue.
    /// </summary>
    [Fact]
    public async Task Single_codings_on_both_lanes_become_searchable_and_latency_is_measured()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var messaging = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var w = await h.Db.WorkspaceAsync(documents: 21);
        await using var worker = await RunningWorker.StartAsync(h, openSearch, vhost.AmqpUri);
        await using var dispatcher = RunningDispatcher.Start(h.Db, messaging.Publisher, RunningDispatcher.FastOptions("dispatcher-latency"));

        // Warm-up: the first write places the workspace and creates its index.
        await InteractiveIndexWorkerTests.CodeAsync(h, w, w.Documents[0], true);
        await WaitSearchableAsync(h, w.Id, w.Documents[0], 2);

        var samples = new List<TimeSpan>();
        var appliedSamples = new List<TimeSpan>();
        foreach (var doc in w.Documents.Skip(1))
        {
            var clock = Stopwatch.StartNew();
            await InteractiveIndexWorkerTests.CodeAsync(h, w, doc, true);
            appliedSamples.Add(await WaitAppliedAsync(h, w.Id, doc, clock));
            await WaitSearchableAsync(h, w.Id, doc, 2);
            samples.Add(clock.Elapsed);
        }

        await h.Db.Core.Coding.ApplyAsync(SearchWorkDatabase.Interactive(w.Id, w.Documents[0],
            CodingFieldOperation.Set(w.Privilege, JsonValue.Create(w.Privileged))), Ct);
        await WaitSearchableAsync(h, w.Id, w.Documents[0], 3);
        var security = await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND lane = 1 AND status = 4", ("ws", w.Id));
        security.Should().Be(1, "the security-affecting coding went through the L0 lane and was applied");

        var p95 = Percentile(samples, 0.95);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"over {samples.Count} codings: coding → applied p50 {Percentile(appliedSamples, 0.5).TotalMilliseconds:F0} ms, " +
            $"p95 {Percentile(appliedSamples, 0.95).TotalMilliseconds:F0} ms; coding → searchable p50 {Percentile(samples, 0.5).TotalMilliseconds:F0} ms, " +
            $"p95 {p95.TotalMilliseconds:F0} ms, max {samples.Max().TotalMilliseconds:F0} ms");
        if (Environment.GetEnvironmentVariable("OPPORTUNITY_STRICT_LATENCY") == "1")
        {
            p95.Should().BeLessThan(TimeSpan.FromSeconds(1), "single coding → searchable p95 < 1 s with refresh 1 s (E07-T03)");
        }
    }

    /// <summary>
    /// Worker A crashes mid-batch: some handlers wrote to OpenSearch and stopped before marking their rows applied, others
    /// stopped before writing; none acked. Its deliveries return to the queue, worker B takes over and the workspace
    /// converges: every row Applied, the index equal to PostgreSQL, no newer version overwritten.
    /// </summary>
    [Fact]
    public async Task A_worker_crash_mid_batch_is_recovered_by_redelivery_without_stale_writes()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        await using var vhost = await rabbit.CreateVirtualHostAsync(Ct);
        await using var messaging = await MessagingHarness.StartAsync(vhost.AmqpUri, vhost.AmqpUri);
        var w = await h.Db.WorkspaceAsync(documents: 40);
        var crash = new CrashGate(passing: 5);
        var a = await RunningWorker.StartAsync(h, openSearch, vhost.AmqpUri, s => s.Replace(ServiceDescriptor.Singleton<IProjectionIndexWriter>(sp =>
            new CrashingWriter(ActivatorUtilities.CreateInstance<ProjectionIndexWriter>(sp), crash))));
        await using var dispatcher = RunningDispatcher.Start(h.Db, messaging.Publisher, RunningDispatcher.FastOptions("dispatcher-crash"));

        foreach (var doc in w.Documents)
        {
            await InteractiveIndexWorkerTests.CodeAsync(h, w, doc, true);
        }

        await crash.Ready.WaitAsync(Patience, Ct);
        await a.DisposeAsync();
        crash.WrittenUnmarked.Should().BeGreaterThan(0);
        crash.Unwritten.Should().BeGreaterThan(0);

        await using var b = await RunningWorker.StartAsync(h, openSearch, vhost.AmqpUri);
        var deadline = DateTime.UtcNow + Patience;
        while ((await h.Db.OutboxStatusesAsync(w.Id))[SearchOutboxStatus.Applied] < w.Documents.Count)
        {
            if (DateTime.UtcNow > deadline)
            {
                (await BrokerAndOutboxStateAsync(h, messaging, w.Id)).Should().BeEmpty("every row must be applied after redelivery");
            }

            await Task.Delay(100, Ct);
        }

        (await messaging.CountAsync(RabbitMqTopology.DeadLetterQueue(WorkQueues.IndexInteractive)))
            .Should().Be(0, "a worker stopping mid-batch dead-letters nothing");
        (await h.Db.OutboxStatusesAsync(w.Id)).Where(s => s.Key != SearchOutboxStatus.Applied).Sum(s => s.Value).Should().Be(0);
        await InteractiveIndexWorkerTests.AssertIndexMatchesPostgresAsync(h, w.Id, w.Documents);
    }

    /// <summary>Diagnostics on timeout: unapplied rows (id:status:attempts) and the index lanes' ready and dead-lettered counts.</summary>
    private static async Task<string> BrokerAndOutboxStateAsync(IndexWorkerHarness h, MessagingHarness messaging, Guid workspaceId)
    {
        var rows = await h.Db.Core.ScalarAsync<string?>(
            "SELECT string_agg(outbox_id || ':' || status || ':' || attempt_count, ' ' ORDER BY outbox_id) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status <> 4",
            ("ws", workspaceId));
        var queues = new List<string>();
        foreach (var queue in new[] { WorkQueues.IndexSecurity, WorkQueues.IndexInteractive })
        {
            queues.Add($"{queue.Name}={await messaging.CountAsync(queue.Name)} dlq={await messaging.CountAsync(RabbitMqTopology.DeadLetterQueue(queue))}");
        }

        return rows is null ? string.Empty : $"unapplied {rows}; {string.Join("; ", queues)}";
    }

    private static TimeSpan Percentile(List<TimeSpan> samples, double p)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * p) - 1)];
    }

    /// <summary>When the worker marked the document's outbox row Applied (written, before any refresh).</summary>
    private static async Task<TimeSpan> WaitAppliedAsync(IndexWorkerHarness h, Guid workspaceId, Guid doc, Stopwatch clock)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (await h.Db.Core.ScalarAsync<long>(
                   "SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc AND status = 4",
                   ("ws", workspaceId), ("doc", doc)) == 0)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "document {0} must be indexed", doc);
            await Task.Delay(5, Ct);
        }

        return clock.Elapsed;
    }

    private static async Task WaitSearchableAsync(IndexWorkerHarness h, Guid workspaceId, Guid doc, long version)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await h.SearchableAtAsync(workspaceId, doc, version))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "document {0} must become searchable at version {1}", doc, version);
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>The indexing worker module over the test's PostgreSQL, OpenSearch scope and RabbitMQ vhost.</summary>
    private sealed class RunningWorker : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IHostedService _host;

        private RunningWorker(ServiceProvider provider, IHostedService host)
        {
            _provider = provider;
            _host = host;
        }

        public static async Task<RunningWorker> StartAsync(
            IndexWorkerHarness h, OpenSearchFixture openSearch, Uri amqp, Action<IServiceCollection>? configure = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OpenSearch"] = openSearch.BaseAddress.ToString(),
                ["OpenSearch:IndexPrefix"] = h.Scope.Prefix,
                ["OpenSearch:Placement:CacheTtl"] = "00:00:00",
                ["ConnectionStrings:RabbitMq"] = amqp.ToString(),
                ["Messaging:RabbitMq:ClientName"] = "index-worker-test",
                ["Messaging:RabbitMq:RetryBaseDelay"] = "00:00:00.300",
                ["Messaging:RabbitMq:MaxTransportRetries"] = "3",
                ["Messaging:RabbitMq:DeliveryLimit"] = "3",
                ["Messaging:RabbitMq:NetworkRecoveryInterval"] = "00:00:00.500",
                ["Messaging:RabbitMq:MaxConnectRetryDelay"] = "00:00:01",
                ["ObjectStorage:Provider"] = "FileSystem",
            }).Build();
            var services = new ServiceCollection().AddLogging();
            IndexWorkerHarness.AddWorkerDependencies(services, h.Db, h.Meters);
            services.AddIndexingWorker(configuration);
            configure?.Invoke(services);
            var provider = services.BuildServiceProvider();
            var host = provider.GetServices<IHostedService>().Single();
            await host.StartAsync(Ct);
            return new RunningWorker(provider, host);
        }

        /// <summary>Stops like a crash of the consumer: in-flight deliveries are cancelled and return to the queue unacked.</summary>
        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            await _provider.DisposeAsync();
        }
    }

    /// <summary>
    /// The first <c>passing</c> writes go through; after that, alternately the write is made and the handler then hangs
    /// (written, not marked) or the handler hangs before writing. Hanging handlers end only by cancellation.
    /// </summary>
    private sealed class CrashGate(int passing)
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _writtenUnmarked;
        private int _unwritten;

        public Task Ready => _ready.Task;

        public int WrittenUnmarked => Volatile.Read(ref _writtenUnmarked);

        public int Unwritten => Volatile.Read(ref _unwritten);

        /// <summary>True: write, then hang. False: hang without writing. Null: pass through.</summary>
        public bool? Next()
        {
            var call = Interlocked.Increment(ref _calls);
            return call <= passing ? null : call % 2 == 0;
        }

        public void Hanging(bool written)
        {
            var total = written ? Interlocked.Increment(ref _writtenUnmarked) + Unwritten : Interlocked.Increment(ref _unwritten) + WrittenUnmarked;
            if (WrittenUnmarked >= 3 && Unwritten >= 3 && total >= 6)
            {
                _ready.TrySetResult();
            }
        }
    }

    private sealed class CrashingWriter(IProjectionIndexWriter inner, CrashGate gate) : IProjectionIndexWriter
    {
        public ProjectionWriterOptions Options => inner.Options;

        public Task<ProjectionWriteReport> WriteAsync(
            Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, ProjectionWriteScope scope, CancellationToken cancellationToken = default) =>
            WriteAsync(workspaceId, documents, cancellationToken);

        public async Task<ProjectionWriteReport> WriteAsync(
            Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, CancellationToken cancellationToken = default)
        {
            var mode = gate.Next();
            if (mode is null)
            {
                return await inner.WriteAsync(workspaceId, documents, cancellationToken);
            }

            if (mode == true)
            {
                (await inner.WriteAsync(workspaceId, documents, cancellationToken)).Documents.Should().OnlyContain(d => d.Succeeded);
            }

            gate.Hanging(written: mode == true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
