using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Audit;
using Opportunity.Application.Bootstrap;
using Opportunity.Application.Messaging;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Search.Projection;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Search.Workers;
using Opportunity.Search.Writing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// The chunk index worker (E07-T04) over the import pipeline: a migrated PostgreSQL used through the RLS-bound app
/// login, a file-system object store, the shared OpenSearch container under a per-test prefix and the real consumer,
/// fed with deliveries the way the transport hands them over. Every <c>_bulk</c> request passes a hook for fault
/// injection; text loading passes another, so tests can stall a worker between its PostgreSQL read and its write.
/// </summary>
internal sealed class ChunkIndexHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly OpenSearchIndexScope? _scope;

    private ChunkIndexHarness(ImportHarness import, ServiceProvider services, OpenSearchIndexScope? scope, BulkHook hook, HookedTextLoader texts, HttpClient http)
    {
        Import = import;
        _services = services;
        _scope = scope;
        Hook = hook;
        Texts = texts;
        Http = http;
    }

    public ImportHarness Import { get; }

    public BulkHook Hook { get; }

    public HookedTextLoader Texts { get; }

    public HttpClient Http { get; }

    public IndexChunkTaskRepository Tasks => (IndexChunkTaskRepository)_services.GetRequiredService<IIndexChunkTaskRepository>();

    public IProjectionService Projections => _services.GetRequiredService<IProjectionService>();

    public IProjectionIndexWriter Writer => _services.GetRequiredService<IProjectionIndexWriter>();

    public InMemoryAuditEventWriter Audit { get; } = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ChunkIndexHarness> CreateAsync(
        OpenSearchFixture openSearch, MigrationPostgresFixture postgres, int rowsPerChunk = 500,
        Action<ChunkIndexWorkerOptions>? configure = null, ProjectionWriterOptions? writer = null)
    {
        var import = await ImportHarness.CreateAsync(postgres, rowsPerChunk);
        var scope = openSearch.CreateIndexScope();
        var options = new OpenSearchOptions { Endpoint = openSearch.BaseAddress, IndexPrefix = scope.Prefix, Placement = { CacheTtl = TimeSpan.Zero } };
        return await CreateAsync(openSearch, import, options, scope, new ProjectionOptions(), realTexts: false, configure, writer);
    }

    /// <summary>
    /// The worker over another harness's import pipeline, database and OpenSearch prefix (both owned by the caller),
    /// reading extracted text from the import's object store with the real loader (end-to-end search tests).
    /// </summary>
    public static Task<ChunkIndexHarness> OverAsync(OpenSearchFixture openSearch, ImportHarness import, OpenSearchOptions options, ProjectionOptions projection) =>
        CreateAsync(openSearch, import, options, null, projection, realTexts: true, null, null);

    private static async Task<ChunkIndexHarness> CreateAsync(
        OpenSearchFixture openSearch, ImportHarness import, OpenSearchOptions options, OpenSearchIndexScope? scope, ProjectionOptions projection, bool realTexts,
        Action<ChunkIndexWorkerOptions>? configure, ProjectionWriterOptions? writer)
    {
        var worker = new ChunkIndexWorkerOptions
        {
            WorkerId = "chunk-index-test",
            RetryBaseDelay = TimeSpan.FromMilliseconds(50),
            RetryMaxDelay = TimeSpan.FromMilliseconds(200),
            ThrottleDelay = TimeSpan.FromMilliseconds(100),
        };
        configure?.Invoke(worker);

        var hook = new BulkHook();
        var services = new ServiceCollection().AddLogging();
        var harness = default(ChunkIndexHarness);
        services.AddSingleton(options);
        services.AddSingleton(new OpenSearchConnection(options, hook));
        services.AddSingleton<IIndexPlacementStore>(new IndexPlacementStore(import.Db.AppDataSource));
        services.AddSingleton<IProjectionSourceReader>(new ProjectionSourceReader(import.Db.AppDataSource));
        services.AddSingleton<IIndexChunkTaskRepository>(new IndexChunkTaskRepository(import.Db.AppDataSource));
        services.AddSingleton<IIndexTaskMembershipReader>(new IndexTaskMembershipReader(import.Db.AppDataSource));
        services.AddSingleton<IObjectStore>(_ => import.Store);
        services.AddSingleton<IAuditEventWriter>(_ => harness!.Audit);
        var store = import.Store;
        var inner = realTexts
            ? new ServiceCollection().AddSingleton(store).AddSearchProjection(projection).BuildServiceProvider().GetRequiredService<IProjectionTextLoader>()
            : null;
        services.AddSingleton(projection);
        services.AddSingleton(sp => new HookedTextLoader(projection, inner));
        services.AddSingleton<IProjectionTextLoader>(sp => sp.GetRequiredService<HookedTextLoader>());
        services.AddOpenSearchIndexTemplateBootstrap(options);
        services.AddChunkIndexWorker(worker, writer);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        harness = new ChunkIndexHarness(import, provider, scope, hook, provider.GetRequiredService<HookedTextLoader>(),
            new HttpClient { BaseAddress = openSearch.BaseAddress });
        await provider.GetServices<IInfrastructureBootstrapStep>().Single().RunAsync(Ct);
        return harness;
    }

    /// <summary>A consumer as the transport resolves it (one DI scope), optionally over another task repository.</summary>
    public ChunkIndexTaskConsumer Consumer(IIndexChunkTaskRepository? tasks = null)
    {
        var scope = _services.CreateScope();
        return tasks is null
            ? scope.ServiceProvider.GetRequiredService<ChunkIndexTaskConsumer>()
            : ActivatorUtilities.CreateInstance<ChunkIndexTaskConsumer>(scope.ServiceProvider, tasks);
    }

    public static ReceivedMessage Received(IndexChunkTaskInfo task, bool redelivered = false) => new(
        new MessageEnvelope
        {
            MessageId = Guid.CreateVersion7(),
            MessageType = MessageTypes.IndexChunkTask,
            SchemaVersion = new SchemaVersion(1, 0),
            WorkspaceId = task.WorkspaceId,
            JobId = task.JobId,
            CorrelationId = $"index-task:{task.TaskId}",
            IdempotencyKey = task.IdempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Attempt = task.AttemptCount,
            Payload = JsonSerializer.SerializeToElement(new IndexChunkTaskMessage { TaskId = task.TaskId }, MessageJson.PayloadOptions),
        },
        new IndexChunkTaskMessage { TaskId = task.TaskId },
        task.Lane == MessageLane.SecurityBulk ? WorkQueues.IndexSecurityBulk : WorkQueues.IndexBulk,
        redelivered, DeliveryCount: 0, TransportRetry: 0);

    public Task DeliverAsync(IndexChunkTaskInfo task, ChunkIndexTaskConsumer? consumer = null, CancellationToken? cancellationToken = null) =>
        (consumer ?? Consumer()).HandleAsync(new IndexChunkTaskMessage { TaskId = task.TaskId }, Received(task), cancellationToken ?? Ct);

    public Task<IReadOnlyList<IndexChunkTaskInfo>> TasksAsync(Guid ws, Guid jobId) => Tasks.GetByJobAsync(ws, jobId, Ct);

    /// <summary>Delivers every task of the job that is not Applied, <paramref name="parallelism"/> at a time.</summary>
    public async Task DeliverAllAsync(Guid ws, Guid jobId, int parallelism = 1)
    {
        var open = (await TasksAsync(ws, jobId)).Where(t => t.Status != IndexChunkTaskStatus.Applied).ToList();
        await Parallel.ForEachAsync(open, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = Ct },
            async (task, ct) => await DeliverAsync(task, cancellationToken: ct));
    }

    /// <summary>The indexed copy of a document: external version and source, or null when absent.</summary>
    public async Task<(long Version, JsonObject Source)?> IndexedAsync(Guid ws, Guid documentId)
    {
        var placement = await _services.GetRequiredService<IIndexManager>().ResolveAsync(ws, IndexPurpose.Read, Ct);
        var routing = placement.Read.Routing is { } r ? "?routing=" + r : string.Empty;
        using var response = await Http.GetAsync($"{placement.Read.Index}/_doc/{documentId:D}{routing}", Ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        return (body["_version"]!.GetValue<long>(), body["_source"]!.AsObject());
    }

    /// <summary>Documents of the workspace in its index, after a refresh.</summary>
    public async Task<long> CountAsync(Guid ws)
    {
        var placement = await _services.GetRequiredService<IIndexManager>().ResolveAsync(ws, IndexPurpose.Read, Ct);
        (await Http.PostAsync($"{placement.Read.Index}/_refresh", null, Ct)).Dispose();
        var routing = placement.Read.Routing is { } r ? "?routing=" + r : string.Empty;
        using var response = await Http.PostAsync($"{placement.Read.Index}/_count{routing}",
            new StringContent(new JsonObject { ["query"] = new JsonObject { ["term"] = new JsonObject { ["workspaceId"] = ws.ToString("D") } } }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json"), Ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["count"]!.GetValue<long>();
    }

    /// <summary>Asserts the index holds exactly the projection of current PostgreSQL state, at its version.</summary>
    public async Task<List<string>> DriftAsync(Guid ws, IReadOnlyCollection<Guid> documentIds)
    {
        var drift = new List<string>();
        foreach (var page in documentIds.Chunk(500))
        {
            foreach (var expected in await Projections.BuildAsync(ws, page, Ct))
            {
                var indexed = await IndexedAsync(ws, expected.DocumentId);
                var write = expected.Writes.Single();
                var body = write.Body?.DeepClone().AsObject();
                body?.Remove("text"); // excluded from _source by the mapping
                if (indexed is not { } actual)
                {
                    drift.Add($"{expected.DocumentId}: missing (expected v{expected.Version})");
                }
                else if (actual.Version != expected.Version || !JsonNode.DeepEquals(actual.Source, body))
                {
                    drift.Add($"{expected.DocumentId}: indexed v{actual.Version}, PostgreSQL v{expected.Version}");
                }
            }
        }

        return drift;
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is not null)
        {
            foreach (var generation in Enumerable.Range(1, 4))
            {
                using var _ = await Http.DeleteAsync($"_index_template/{_scope.Prefix}-projection-g{generation}", CancellationToken.None);
            }

            await _scope.DisposeAsync();
        }

        Http.Dispose();
        await _services.DisposeAsync();
        await Import.DisposeAsync();
    }

    /// <summary>Every request to OpenSearch; <see cref="OnBulk"/> may replace the response of a <c>_bulk</c> request.</summary>
    internal sealed class BulkHook() : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentQueue<(long Bytes, int Actions, string Body)> Bulks { get; } = new();

        /// <summary>(request number, request body, real response) → response to hand back; null keeps the real one.</summary>
        public Func<int, string, HttpResponseMessage, Task<HttpResponseMessage?>>? OnBulk { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/_bulk" || request.Content is null)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var bytes = request.Content.Headers.ContentLength ?? System.Text.Encoding.UTF8.GetByteCount(body);
            var actions = body.Split('\n').Count(l => l.StartsWith("{\"index\":", StringComparison.Ordinal) || l.StartsWith("{\"delete\":", StringComparison.Ordinal));
            Bulks.Enqueue((bytes, actions, body.Length > 4096 ? body[..4096] : body));
            var response = await base.SendAsync(request, cancellationToken);
            return OnBulk is { } hook && await hook(Bulks.Count, body, response) is { } replaced ? replaced : response;
        }
    }

    /// <summary>The real loader shape with hooks: <see cref="Override"/> supplies text, <see cref="Gate"/> stalls a read.</summary>
    internal sealed class HookedTextLoader(ProjectionOptions options, IProjectionTextLoader? inner = null) : IProjectionTextLoader
    {
        public Func<ProjectionSource, string?>? Override { get; set; }

        /// <summary>Awaited before a document's text is returned: the PostgreSQL snapshot is already taken.</summary>
        public Func<ProjectionSource, CancellationToken, Task>? Gate { get; set; }

        public async Task<ProjectionText?> LoadAsync(ProjectionSource source, CancellationToken cancellationToken = default)
        {
            if (Gate is { } gate)
            {
                await gate(source, cancellationToken);
            }

            if (Override?.Invoke(source) is { } text)
            {
                return IndexedText.Cap(text, options.IndexedTextCap);
            }

            return inner is null ? null : await inner.LoadAsync(source, cancellationToken);
        }
    }
}
