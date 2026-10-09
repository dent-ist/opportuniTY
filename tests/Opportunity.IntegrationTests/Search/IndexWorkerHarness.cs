using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Opportunity.Application.Bootstrap;
using Opportunity.Application.Messaging;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Storage;
using Opportunity.Application.Telemetry;
using Opportunity.Application.Workspaces;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Audit;
using Opportunity.Data.Search;
using Opportunity.Data.SearchWork;
using Opportunity.Data.Workspaces;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.SearchWork;
using Opportunity.Search;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Search.Workers;
using Opportunity.Search.Writing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// The interactive index worker over a migrated PostgreSQL (search work, projection sources and placements as the
/// RLS-bound app login) and the shared OpenSearch container under a per-test index prefix. The dispatcher is stood in
/// for by <see cref="DispatchAsync"/> (the real relay, publishing into a list), so a test decides exactly which
/// messages arrive, how often and in which order.
/// </summary>
internal sealed class IndexWorkerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private IndexWorkerHarness(ServiceProvider services, SearchWorkDatabase db, OpenSearchIndexScope scope, MeterCapture meters, HttpClient http)
    {
        _services = services;
        Db = db;
        Scope = scope;
        Meters = meters;
        Http = http;
    }

    public SearchWorkDatabase Db { get; }

    public OpenSearchIndexScope Scope { get; }

    public MeterCapture Meters { get; }

    public HttpClient Http { get; }

    public IServiceProvider Services => _services;

    public IProjectionService Projections => _services.GetRequiredService<IProjectionService>();

    public IProjectionIndexWriter Writer => _services.GetRequiredService<IProjectionIndexWriter>();

    public IIndexManager Indexes => _services.GetRequiredService<IIndexManager>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<IndexWorkerHarness> CreateAsync(
        OpenSearchFixture openSearch,
        MigrationPostgresFixture postgres,
        Action<InteractiveIndexWorkerOptions>? worker = null,
        Action<IServiceCollection>? configure = null)
    {
        var db = await SearchWorkDatabase.CreateAsync(postgres);
        var scope = openSearch.CreateIndexScope();
        var options = Options(openSearch, scope);
        var workerOptions = new InteractiveIndexWorkerOptions();
        worker?.Invoke(workerOptions);

        var meters = new MeterCapture();
        var services = new ServiceCollection().AddLogging();
        AddWorkerDependencies(services, db, meters);
        services.AddPostgresSearchWorkStore();
        services.AddPostgresProjectionSource();
        services.AddPostgresIndexPlacementStore();
        services.AddSingleton<IWorkspaceReader>(sp => new WorkspaceReader(db.Core.AppDataSource));
        services.AddOpenSearchIndexTemplateBootstrap(options);
        services.AddInteractiveIndexWorker(workerOptions);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        await provider.GetServices<IInfrastructureBootstrapStep>().Single().RunAsync(Ct);
        return new IndexWorkerHarness(provider, db, scope, meters, new HttpClient { BaseAddress = openSearch.BaseAddress });
    }

    public static OpenSearchOptions Options(OpenSearchFixture openSearch, OpenSearchIndexScope scope) => new()
    {
        Endpoint = openSearch.BaseAddress,
        IndexPrefix = scope.Prefix,
        Placement = { CacheTtl = TimeSpan.Zero },
    };

    /// <summary>What the worker host supplies outside the module: the data source, metrics and object storage.</summary>
    public static void AddWorkerDependencies(IServiceCollection services, SearchWorkDatabase db, MeterCapture meters)
    {
        services.AddSingleton(db.Core.AppDataSource);
        services.AddSingleton(meters.Metrics);
        services.AddSingleton<IObjectStore, NoTextObjects>();
        services.AddPostgresAuditStore();
    }

    /// <summary>One relay pass: claims due outbox rows, marks them Dispatched and returns the messages it "published".</summary>
    public async Task<IReadOnlyList<ReceivedMessage>> DispatchAsync(Guid workspaceId)
    {
        var publisher = new CollectingPublisher();
        var relay = new SearchWorkRelay(Db.Outbox, Db.Tasks, publisher, new SearchWorkRelayOptions { Owner = "test-dispatcher", BatchSize = 5_000 });
        await relay.RelayOutboxAsync(workspaceId, Ct);
        return [.. publisher.Messages];
    }

    /// <summary>Handles one message in a fresh scope, as the message host does.</summary>
    public Task HandleAsync(ReceivedMessage message) => HandleUntilAsync(message, Ct);

    public async Task HandleUntilAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InteractiveIndexWorker>()
            .HandleAsync((SearchOutboxMessage)message.Payload, message, cancellationToken);
    }

    public async Task<IndexTarget> TargetAsync(Guid workspaceId) =>
        (await Indexes.ResolveAsync(workspaceId, IndexPurpose.Write, Ct)).WriteTargets.Single();

    /// <summary>The stored document (realtime GET) or null; <c>_version</c> and <c>_source</c>.</summary>
    public async Task<(long Version, JsonObject Source)?> GetAsync(Guid workspaceId, Guid documentId)
    {
        var target = await TargetAsync(workspaceId);
        using var response = await Http.GetAsync(
            $"{target.Index}/_doc/{documentId:D}" + (target.Routing is { } r ? $"?routing={r}" : string.Empty), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonNode.Parse(text)!.AsObject();
        if (response.StatusCode == HttpStatusCode.NotFound || body["found"]?.GetValue<bool>() == false)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return (body["_version"]!.GetValue<long>(), body["_source"]!.AsObject());
    }

    /// <summary>Whether a search (refresh-dependent) finds the document at exactly <paramref name="version"/>.</summary>
    public async Task<bool> SearchableAtAsync(Guid workspaceId, Guid documentId, long version)
    {
        Placement placement;
        try
        {
            placement = await Indexes.ResolveAsync(workspaceId, IndexPurpose.Read, Ct);
        }
        catch (WorkspaceNotPlacedException)
        {
            return false;
        }

        var query = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["filter"] = new JsonArray(
                        new JsonObject { ["term"] = new JsonObject { ["workspaceId"] = placement.WorkspaceFilterValue } },
                        new JsonObject { ["term"] = new JsonObject { ["documentId"] = documentId.ToString("D") } },
                        new JsonObject { ["term"] = new JsonObject { ["projectionVersion"] = version } }),
                },
            },
            ["size"] = 0,
        };
        var routing = placement.Read.Routing is { } r ? $"?routing={r}" : string.Empty;
        using var response = await Http.PostAsync(
            $"{placement.Read.Index}/_search{routing}", new StringContent(query.ToJsonString(), Encoding.UTF8, "application/json"), Ct);
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        return body["hits"]!["total"]!["value"]!.GetValue<long>() > 0;
    }

    public async Task PutSettingsAsync(Guid workspaceId, JsonObject settings)
    {
        var target = await TargetAsync(workspaceId);
        using var response = await Http.PutAsync(
            $"{target.Index}/_settings", new StringContent(new JsonObject { ["index"] = settings }.ToJsonString(), Encoding.UTF8, "application/json"), Ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RefreshAsync(Guid workspaceId)
    {
        var target = await TargetAsync(workspaceId);
        using var response = await Http.PostAsync($"{target.Index}/_refresh", null, Ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Soft-deletes a document the way a delete use case will (E04/E20): version bump, tombstone and outbox row.</summary>
    public async Task<long> SoftDeleteAsync(Guid workspaceId, Guid documentId)
    {
        long version = 0;
        await Db.Core.InWorkspaceAsync(workspaceId, async tx =>
        {
            await using (var command = tx.Command(
                """
                UPDATE opportunity.document_projection_state SET document_version = document_version + 1, is_deleted = true, deleted_at = now()
                WHERE workspace_id = @ws AND document_id = @doc RETURNING document_version
                """))
            {
                command.Parameters.AddWithValue("ws", workspaceId);
                command.Parameters.AddWithValue("doc", documentId);
                version = (long)(await command.ExecuteScalarAsync(Ct))!;
            }

            await SearchWorkSql.AddOutboxRowsAsync(tx, [(documentId, version)], SearchChangeMask.Delete, Ct);
        });
        return version;
    }

    public Task<long> VersionAsync(Guid workspaceId, Guid documentId) =>
        Db.Core.ScalarAsync<long>(
            "SELECT document_version FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", workspaceId), ("doc", documentId));

    public static ReceivedMessage Message(Guid? workspaceId, long outboxId, Guid documentId, long version, WorkQueue? queue = null) => new(
        new MessageEnvelope
        {
            MessageId = Guid.CreateVersion7(),
            MessageType = MessageTypes.SearchOutbox,
            SchemaVersion = new SchemaVersion(1, 0),
            WorkspaceId = workspaceId,
            CorrelationId = $"search-outbox:{outboxId}",
            IdempotencyKey = $"test-{outboxId}",
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { outboxId, documentId, documentVersion = version }),
        },
        new SearchOutboxMessage { OutboxId = outboxId, DocumentId = documentId, DocumentVersion = version },
        queue ?? WorkQueues.IndexInteractive,
        Redelivered: false,
        DeliveryCount: 0,
        TransportRetry: 0);

    public async ValueTask DisposeAsync()
    {
        foreach (var generation in Enumerable.Range(1, 4))
        {
            using var _ = await Http.DeleteAsync($"_index_template/{Scope.Prefix}-projection-g{generation}", CancellationToken.None);
        }

        await Scope.DisposeAsync();
        Http.Dispose();
        await _services.DisposeAsync();
        Meters.Dispose();
        await Db.DisposeAsync();
    }

    private sealed class CollectingPublisher : IMessagePublisher
    {
        public ConcurrentQueue<ReceivedMessage> Messages { get; } = new();

        public Task<MessageEnvelope> PublishAsync<TPayload>(OutgoingMessage<TPayload> message, CancellationToken cancellationToken = default)
            where TPayload : class
        {
            var payload = (SearchOutboxMessage)(object)message.Payload;
            Messages.Enqueue(Message(message.Correlation.WorkspaceId, payload.OutboxId, payload.DocumentId, payload.DocumentVersion, message.Destination));
            return Task.FromResult<MessageEnvelope>(null!);
        }
    }

    /// <summary>The test documents carry no extracted text, so the projection never reads object storage.</summary>
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

/// <summary>Collects counter measurements of one <see cref="OpportunityMetrics"/> instance.</summary>
internal sealed class MeterCapture : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Name, long Value, string? Lane)> _values = new();

    public MeterCapture()
    {
        var factory = _provider.GetRequiredService<IMeterFactory>();
        Metrics = new OpportunityMetrics(factory);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OpportunityTelemetry.MeterName && instrument.Meter.Scope == factory)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? lane = null;
            foreach (var tag in tags)
            {
                if (tag.Key == TelemetryAttributes.Lane)
                {
                    lane = tag.Value as string;
                }
            }

            _values.Enqueue((instrument.Name, value, lane));
        });
        _listener.Start();
    }

    public OpportunityMetrics Metrics { get; }

    public long Sum(string name, string? lane = null) =>
        _values.Where(v => v.Name == name && (lane is null || v.Lane == lane)).Sum(v => v.Value);

    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }
}
