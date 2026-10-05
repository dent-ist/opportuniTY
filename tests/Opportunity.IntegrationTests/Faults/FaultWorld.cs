#if OPPORTUNITY_FAILPOINTS
using System.Globalization;
using System.Net;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Bootstrap;
using Opportunity.Application.Faults;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Search;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Data.Fields;
using Opportunity.Data.Jobs;
using Opportunity.Data.Migrations;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Hosting.Workers;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Messaging;
using Opportunity.IntegrationTests.Search;
using Opportunity.Jobs;
using Opportunity.Jobs.Dispatch;
using Opportunity.Messaging;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Workers;
using Opportunity.Security.Authorization;
using Opportunity.Testing.OpenSearch;
using Opportunity.Testing.Postgres;
using Opportunity.Testing.RabbitMq;
using Opportunity.Testing.Toxiproxy;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>
/// The fault matrix runs against its own containers (PostgreSQL, OpenSearch and RabbitMQ, each behind Toxiproxy), so it
/// may pause, partition and restart them without touching other suites. Its tests run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class FaultCollectionDefinition
    : ICollectionFixture<FaultPostgresFixture>, ICollectionFixture<OpenSearchFixture>, ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "Faults";
}

/// <summary>PostgreSQL whose template database is migrated once; every world clones it.</summary>
public sealed class FaultPostgresFixture : PostgresFixture
{
    protected override async Task SeedTemplateAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var template = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = TemplateDatabase, Pooling = false };
        await using var source = NpgsqlDataSource.Create(template.ConnectionString);
        await new PostgresMigrator(source).MigrateAsync(cancellationToken);
    }
}

/// <summary>Where the work of a failpoint runs.</summary>
internal enum FaultHost
{
    Worker,
    Dispatcher,
}

/// <summary>
/// The system under fault (E18-T01), shared by the matrix's trials: one migrated database, an OpenSearch index prefix and
/// a RabbitMQ virtual host, the real dispatcher host (outbox/task/chunk relays, lease sweeper, search work recovery)
/// and the real worker host (bulk coding, chunk index and interactive index modules) — composed exactly like
/// <c>Opportunity.Worker.All</c> with test timings — plus an "API side" (coding store, snapshots, PDP and search
/// service) on a link that faults never touch. Workers and the dispatcher reach every store through Toxiproxy, and
/// their OpenSearch client through <see cref="OpenSearchFaultHandler"/>; <see cref="MatrixFaultInjector"/> is
/// registered in both hosts. Each trial uses a fresh workspace.
/// </summary>
public sealed class FaultWorldFixture(FaultPostgresFixture postgres, OpenSearchFixture openSearch, RabbitMqFixture rabbit) : IAsyncLifetime, IFaultActions
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _hosts = new(1, 1);
    private readonly List<Task> _background = [];
    private readonly Lock _backgroundGate = new();
    private PostgresDatabase? _database;
    private FaultProxy? _pgAdmin;
    private FaultProxy? _pgApp;
    private FaultProxy? _osProxy;
    private FaultProxy? _rabbitProxy;
    private RabbitMqVirtualHost? _vhost;
    private OpenSearchIndexScope? _scope;
    private ServiceProvider? _api;
    private RunningHost? _worker;
    private RunningHost? _dispatcher;
    private string _workerApp = string.Empty;
    private int _kills;

    internal CoreSchemaDatabase Core { get; private set; } = null!;

    internal AuthorizationDatabase Authz { get; private set; } = null!;

    internal MeterCapture Meters { get; } = new();

    internal OpenSearchFaults OpenSearchFaults { get; } = new();

    internal MatrixFaultInjector Injector { get; private set; } = null!;

    /// <summary>Direct client of the OpenSearch container (oracle and assertions; never faulted except by a pause).</summary>
    internal HttpClient OpenSearchHttp { get; private set; } = null!;

    internal IServiceProvider Api => _api!;

    internal string IndexPrefix => _scope!.Prefix;

    /// <summary>Hosts killed by crash faults so far.</summary>
    internal int Kills => Volatile.Read(ref _kills);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Injector = new MatrixFaultInjector(this);
        _database = await postgres.CreateDatabaseAsync();
        _pgAdmin = await postgres.CreateFaultProxyAsync();
        _pgApp = await postgres.CreateFaultProxyAsync();
        _osProxy = await openSearch.CreateFaultProxyAsync();
        _rabbitProxy = await rabbit.CreateFaultProxyAsync();
        _vhost = await rabbit.CreateVirtualHostAsync();
        _scope = openSearch.CreateIndexScope();
        OpenSearchHttp = new HttpClient { BaseAddress = openSearch.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

        var admin = _database.ConnectionStringVia(_pgAdmin);
        var apiApp = await CoreSchemaDatabase.CreateLoginAsync(admin, "opportunity_app");
        Core = CoreSchemaDatabase.Over(admin, apiApp);
        Authz = AuthorizationDatabase.Over(Core);
        _workerApp = new NpgsqlConnectionStringBuilder(apiApp) { Host = _pgApp.Host, Port = _pgApp.Port, Timeout = 5, CommandTimeout = 15 }.ConnectionString;

        // Topology once, directly (the transport's own declaration would race the first consumers).
        await using (var _ = await MessagingHarness.StartAsync(_vhost.AmqpUri, _vhost.AmqpUri))
        {
        }

        _api = BuildApi();
        await _api.GetServices<IInfrastructureBootstrapStep>().Single().RunAsync(Ct);
        _dispatcher = await StartDispatcherAsync();
        _worker = await StartWorkerAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Injector?.Disarm();
        await AwaitBackgroundAsync(TimeSpan.FromSeconds(60));
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_dispatcher is not null)
        {
            await _dispatcher.DisposeAsync();
        }

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_scope is not null)
        {
            using (var _ = await OpenSearchHttp.DeleteAsync($"_index_template/{_scope.Prefix}-projection-g2", CancellationToken.None))
            {
            }

            await _scope.DisposeAsync();
        }

        OpenSearchHttp?.Dispose();
        if (Core is not null)
        {
            await Core.DisposeAsync();
        }

        foreach (var proxy in new[] { _pgApp, _pgAdmin, _osProxy, _rabbitProxy })
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }
        }

        if (_vhost is not null)
        {
            await _vhost.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        Meters.Dispose();
        _hosts.Dispose();
    }

    /// <summary>Waits for every background fault (partition window, pause, restart) to finish and restore.</summary>
    internal async Task AwaitBackgroundAsync(TimeSpan timeout)
    {
        Task[] running;
        lock (_backgroundGate)
        {
            running = [.. _background];
            _background.Clear();
        }

        await Task.WhenAll(running).WaitAsync(timeout);
    }

    /// <summary>Lifts every fault a failed trial may have left: proxies enabled, OpenSearch unpaused, rejections cleared.</summary>
    internal async Task RestoreAllAsync()
    {
        OpenSearchFaults.Clear();
        foreach (var proxy in new[] { _pgApp!, _osProxy!, _rabbitProxy! })
        {
            await proxy.RestoreAsync(CancellationToken.None);
        }

        try
        {
            await openSearch.UnpauseAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Not paused: nothing to lift.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        // The test's own pools may hold connections a PostgreSQL restart terminated (the workers recover on their own).
        Core.DataSource.Clear();
        Core.AppDataSource.Clear();
    }

    void IFaultActions.Kill(string failpoint)
    {
        Interlocked.Increment(ref _kills);
        var host = failpoint == Failpoints.RelayAfterPublish ? FaultHost.Dispatcher : FaultHost.Worker;
        Background(Task.Run(() => RestartAsync(host), CancellationToken.None));
    }

    async Task IFaultActions.DeliverAsync(ReceivedMessage message, bool redelivered, CancellationToken cancellationToken)
    {
        var worker = _worker ?? throw new InvalidOperationException("No worker host.");
        var copy = message with { Redelivered = redelivered };
        try
        {
            await using var scope = worker.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            switch (message.Payload)
            {
                case JobChunkMessage chunk:
                    await services.GetRequiredService<JobChunkConsumer>().HandleAsync(chunk, copy, cancellationToken);
                    break;
                case IndexChunkTaskMessage task:
                    await services.GetRequiredService<ChunkIndexTaskConsumer>().HandleAsync(task, copy, cancellationToken);
                    break;
                case SearchOutboxMessage row:
                    await services.GetRequiredService<InteractiveIndexWorker>().HandleAsync(row, copy, cancellationToken);
                    break;
            }
        }
#pragma warning disable CA1031 // A failed copy is retried by the transport in production; here the original carries on.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"duplicate delivery ended with {ex.GetType().Name}: {ex.Message}");
        }
    }

    async Task IFaultActions.ExpireLeaseAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken)
    {
        var ws = context.WorkspaceId!.Value;
        switch (context.Message?.Payload)
        {
            case JobChunkMessage chunk:
                await AdminAsync(
                    "UPDATE opportunity.job_chunk SET lease_expires_at = now() - interval '1 hour' WHERE workspace_id = @ws AND chunk_id = @id AND status = 3",
                    ("ws", ws), ("id", chunk.ChunkId));
                break;
            case IndexChunkTaskMessage task:
                await AdminAsync(
                    "UPDATE opportunity.index_chunk_task SET lease_expires_at = now() - interval '1 hour' WHERE workspace_id = @ws AND task_id = @id AND status = 3",
                    ("ws", ws), ("id", task.TaskId));
                break;
        }
    }

    async Task<bool> IFaultActions.OthersDrainedAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken)
    {
        var ws = context.WorkspaceId!.Value;
        var outbox = context.Message?.Payload is SearchOutboxMessage o ? o.OutboxId : -1L;
        var task = context.Message?.Payload is IndexChunkTaskMessage t ? t.TaskId : Guid.Empty;
        var chunk = context.Message?.Payload is JobChunkMessage c ? c.ChunkId : Guid.Empty;
        try
        {
            return await Core.ScalarAsync<long>(
                """
                SELECT (SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws AND status NOT IN (4, 5) AND outbox_id <> @outbox)
                     + (SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws AND status NOT IN (5, 6) AND task_id <> @task)
                     + (SELECT count(*) FROM opportunity.job_chunk WHERE workspace_id = @ws AND status NOT IN (5, 6, 7) AND chunk_id <> @chunk)
                """,
                ("ws", ws), ("outbox", outbox), ("task", task), ("chunk", chunk)) == 0;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    void IFaultActions.StartInfrastructureFault(FaultKind fault, Random random)
    {
        var window = TimeSpan.FromMilliseconds(1_000 + random.Next(1_500));
        switch (fault)
        {
            case FaultKind.OpenSearch429:
                OpenSearchFaults.RejectNext(HttpStatusCode.TooManyRequests, 2 + random.Next(3));
                break;
            case FaultKind.OpenSearch503:
                OpenSearchFaults.RejectNext(HttpStatusCode.ServiceUnavailable, 2 + random.Next(3));
                break;
            case FaultKind.OpenSearchPartialBulk:
                OpenSearchFaults.PartiallyFailNext(new Random(random.Next()));
                break;
            case FaultKind.OpenSearchPartition:
                Background(CutAsync(_osProxy!, window));
                break;
            case FaultKind.OpenSearchPause:
                Background(PauseAsync(window));
                break;
            case FaultKind.PostgresPartition:
                Background(CutAsync(_pgApp!, window));
                break;
            case FaultKind.PostgresRestart:
                Background(Task.Run(() => postgres.RestartAsync(CancellationToken.None), CancellationToken.None));
                break;
            case FaultKind.RabbitMqPartition:
                Background(CutAsync(_rabbitProxy!, window));
                break;
            case FaultKind.RabbitMqRestart:
                Background(RestartRabbitAsync());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, "Not an infrastructure fault.");
        }
    }

    /// <summary>A superuser statement over the admin link, retried through a PostgreSQL restart.</summary>
    internal async Task AdminAsync(string sql, params (string Name, object Value)[] parameters)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Core.ExecuteAsync(sql, parameters);
                return;
            }
            catch (NpgsqlException) when (attempt < 40)
            {
                await Task.Delay(250, Ct);
            }
        }
    }

    private void Background(Task task)
    {
        lock (_backgroundGate)
        {
            _background.Add(task);
        }
    }

    private static async Task CutAsync(FaultProxy proxy, TimeSpan window)
    {
        await proxy.CutAsync(CancellationToken.None);
        try
        {
            await Task.Delay(window, CancellationToken.None);
        }
        finally
        {
            await proxy.RestoreAsync(CancellationToken.None);
        }
    }

    private async Task PauseAsync(TimeSpan window)
    {
        await openSearch.PauseAsync(CancellationToken.None);
        try
        {
            await Task.Delay(window, CancellationToken.None);
        }
        finally
        {
            await openSearch.UnpauseAsync(CancellationToken.None);
        }
    }

    /// <summary>Restarts the broker application on the node (connections drop, durable quorum queues survive).</summary>
    private async Task RestartRabbitAsync()
    {
        (await rabbit.ExecAsync(["rabbitmqctl", "stop_app"], CancellationToken.None)).ExitCode.Should().Be(0);
        (await rabbit.ExecAsync(["rabbitmqctl", "start_app"], CancellationToken.None)).ExitCode.Should().Be(0);
        (await rabbit.ExecAsync(["rabbitmqctl", "await_startup"], CancellationToken.None)).ExitCode.Should().Be(0);
    }

    /// <summary>Kills a host like <c>kill -9</c> (its unsettled deliveries return to the broker) and starts a new one.</summary>
    private async Task RestartAsync(FaultHost host)
    {
        await _hosts.WaitAsync(CancellationToken.None);
        try
        {
            if (host == FaultHost.Worker)
            {
                var old = _worker;
                _worker = null;
                if (old is not null)
                {
                    await old.DisposeAsync();
                }

                _worker = await StartWorkerAsync();
            }
            else
            {
                var old = _dispatcher;
                _dispatcher = null;
                if (old is not null)
                {
                    await old.DisposeAsync();
                }

                _dispatcher = await StartDispatcherAsync();
            }
        }
        finally
        {
            _hosts.Release();
        }
    }

    private Dictionary<string, string?> Settings(string client) => new()
    {
        ["ConnectionStrings:App"] = _workerApp,
        ["ConnectionStrings:OpenSearch"] = $"http://{_osProxy!.Host}:{_osProxy.Port}/",
        ["ConnectionStrings:RabbitMq"] = _vhost!.AmqpUriVia(_rabbitProxy!).ToString(),
        ["OpenSearch:IndexPrefix"] = _scope!.Prefix,
        ["OpenSearch:Placement:CacheTtl"] = "00:00:00",
        ["OpenSearch:RequestTimeout"] = "00:00:05",
        ["Messaging:RabbitMq:ClientName"] = client,
        ["Messaging:RabbitMq:PublishTimeout"] = "00:00:05",
        ["Messaging:RabbitMq:RetryBaseDelay"] = "00:00:00.300",
        ["Messaging:RabbitMq:MaxTransportRetries"] = "3",
        ["Messaging:RabbitMq:DeliveryLimit"] = "3",
        ["Messaging:RabbitMq:NetworkRecoveryInterval"] = "00:00:00.500",
        ["Messaging:RabbitMq:MaxConnectRetryDelay"] = "00:00:01",
        ["ObjectStorage:Provider"] = "FileSystem",
        [$"{ChunkIndexWorkerOptions.SectionName}:WorkerId"] = client,
        [$"{ChunkIndexWorkerOptions.SectionName}:LeaseDuration"] = Lease.ToString("c", CultureInfo.InvariantCulture),
        [$"{ChunkIndexWorkerOptions.SectionName}:HeartbeatInterval"] = "00:00:01",
        [$"{ChunkIndexWorkerOptions.SectionName}:RetryBaseDelay"] = "00:00:00.200",
        [$"{ChunkIndexWorkerOptions.SectionName}:RetryMaxDelay"] = "00:00:01",
        [$"{ChunkIndexWorkerOptions.SectionName}:ThrottleDelay"] = "00:00:00.300",
        [$"{ChunkIndexWorkerOptions.SectionName}:MaxRetryRounds"] = "6",
        // Several _bulk requests per index task, so a fault between them is mid-chunk.
        ["Search:Bulk:MaxRequestActions"] = "8",
    };

    private static JobLeaseOptions LeaseOptions() => new()
    {
        LeaseDuration = Lease,
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        SweepInterval = TimeSpan.FromSeconds(1),
        SweepGrace = TimeSpan.FromMilliseconds(500),
    };

    private async Task<RunningHost> StartWorkerAsync()
    {
        var client = "fault-worker-" + Guid.NewGuid().ToString("N")[..6];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings(client)).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(_ => NpgsqlDataSource.Create(_workerApp));
        services.AddSingleton(Meters.Metrics);
        services.AddSingleton<IObjectStore, NoTextObjects>();
        services.AddSingleton<IFaultInjector>(Injector);
        services.AddSingleton(LeaseOptions());
        services.AddSingleton(new JobChunkConsumerOptions { WorkerId = client });
        services.AddSingleton(sp => new OpenSearchConnection(sp.GetRequiredService<OpenSearchOptions>(), new OpenSearchFaultHandler(OpenSearchFaults)));
        services.AddIndexingWorker(configuration);
        services.AddChunkIndexWorkerModule(configuration);
        services.AddBulkCodingWorker(configuration);
        return await RunningHost.StartAsync(services);
    }

    private async Task<RunningHost> StartDispatcherAsync()
    {
        var client = "fault-dispatcher-" + Guid.NewGuid().ToString("N")[..6];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings(client)).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(_ => NpgsqlDataSource.Create(_workerApp));
        services.AddSingleton(Meters.Metrics);
        services.AddSingleton<IFaultInjector>(Injector);
        services.AddPostgresSearchWorkStore();
        services.AddPostgresJobChunkStore();
        services.AddJobLeaseSweeper(LeaseOptions());
        services.AddSearchWorkHousekeeping(new SearchWorkHousekeepingOptions
        {
            Interval = TimeSpan.FromSeconds(1),
            DispatchedTimeout = TimeSpan.FromSeconds(5),
            LeaseGrace = TimeSpan.FromMilliseconds(500),
        });
        services.AddRabbitMqMessaging(RabbitMqOptions.Bind(configuration));
        services.AddOutboxDispatcher(new OutboxDispatcherOptions
        {
            Owner = client,
            ClaimDuration = TimeSpan.FromSeconds(3),
            OutboxPollInterval = TimeSpan.FromMilliseconds(200),
            OutboxPollIntervalWithoutListener = TimeSpan.FromMilliseconds(200),
            IndexTaskPollInterval = TimeSpan.FromMilliseconds(200),
            JobChunkPollInterval = TimeSpan.FromMilliseconds(200),
            WorkspaceRefreshInterval = TimeSpan.FromMilliseconds(300),
            BacklogSampleInterval = TimeSpan.FromSeconds(1),
            ShutdownGrace = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(200),
            JobChunkLimits = new JobChunkDispatchLimits { Operations = JobChunkRelay.DispatchedOperations, RedispatchAfter = TimeSpan.FromSeconds(5) },
        });
        return await RunningHost.StartAsync(services, runBootstrap: false);
    }

    private ServiceProvider BuildApi()
    {
        var options = new OpenSearchOptions
        {
            Endpoint = openSearch.BaseAddress,
            IndexPrefix = _scope!.Prefix,
            Placement = { CacheTtl = TimeSpan.Zero },
            Search = { FieldCatalogCacheTtl = TimeSpan.Zero },
        };
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAuditEventWriter>(new InMemoryAuditEventWriter());
        services.AddSingleton(options);
        services.AddSingleton(new OpenSearchConnection(options));
        services.AddSingleton<Application.Authorization.ISecurityStateReader>(Authz.Reader);
        services.AddSingleton<IIndexPlacementStore>(new IndexPlacementStore(Core.AppDataSource));
        services.AddSingleton<ISearchSessionStore>(new SearchSessionStore(Core.AppDataSource));
        services.AddSingleton<ISearchFreshnessReader>(new SearchWatermarkStore(Core.AppDataSource));
        services.AddSingleton<IFieldCatalogRepository>(new FieldCatalogRepository(Core.AppDataSource));
        services.AddOpenSearchIndexTemplateBootstrap(options);
        services.AddOpportunityAuthorization();
        services.AddOpenSearchSearchService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>A started host: its hosted services (consumers, dispatcher loops) run until disposal.</summary>
    internal sealed class RunningHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly List<IHostedService> _hosted;

        private RunningHost(ServiceProvider provider, List<IHostedService> hosted)
        {
            _provider = provider;
            _hosted = hosted;
        }

        public IServiceProvider Services => _provider;

        public static async Task<RunningHost> StartAsync(IServiceCollection services, bool runBootstrap = true)
        {
            var provider = services.BuildServiceProvider();
            if (runBootstrap)
            {
                foreach (var step in provider.GetServices<IInfrastructureBootstrapStep>())
                {
                    await step.RunAsync(CancellationToken.None);
                }
            }

            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }

            return new RunningHost(provider, hosted);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var service in Enumerable.Reverse(_hosted))
            {
                await service.StopAsync(CancellationToken.None);
            }

            await _provider.DisposeAsync();
        }
    }

    /// <summary>The trial documents carry no extracted text, so the projection never reads object storage.</summary>
    private sealed class NoTextObjects : IObjectStore
    {
        public Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
#endif
