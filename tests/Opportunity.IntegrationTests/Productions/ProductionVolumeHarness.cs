using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Exports;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Productions;
using Opportunity.Application.Storage;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Data.Productions;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Exports;
using Opportunity.Jobs;
using Opportunity.Production.Volumes;
using Opportunity.Rendering.Endorsing;
using Opportunity.Rendering.Renderers;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// Production volume runs without a broker (E12-T05): the export harness's database, file-system object store and import
/// pipeline, the production harness over the same database, the coordinator and the real chunk consumer with
/// <see cref="ProductionVolumeChunkExecutor"/>; pages are imaged in process (the sandbox gives the same bytes, E12-T04).
/// </summary>
internal sealed class ProductionVolumeHarness : IAsyncDisposable
{
    private ProductionVolumeHarness(ExportHarness exports, int documentsPerChunk)
    {
        Exports = exports;
        Productions = ProductionHarness.Over(exports.Db, documentsPerChunk: 100);
        Options = new ProductionVolumeOptions { DocumentsPerChunk = documentsPerChunk, PagesPerChunk = 2_000, WorkerId = "volume-test-worker", DocumentConcurrency = 2 };
        Imager = new RenderSessionPageImager(new RasterRenderer());
    }

    public ExportHarness Exports { get; }

    public ProductionHarness Productions { get; }

    public CoreSchemaDatabase Db => Exports.Db;

    public IObjectStore Store => Exports.Store;

    public ProductionRepository ProductionStore => Productions.Store;

    public ProductionVolumeOptions Options { get; }

    public IProducedPageImager Imager { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ProductionVolumeHarness> CreateAsync(Migrations.MigrationPostgresFixture postgres, int documentsPerChunk = 4) =>
        new(await ExportHarness.CreateAsync(postgres), documentsPerChunk);

    public static ProductionVolumeHarness Over(ExportHarness exports, int documentsPerChunk = 4) => new(exports, documentsPerChunk);

    public ProductionVolumeService Service() => new(ProductionStore, Exports.Exports, TimeProvider.System);

    public ProductionVolumeCoordinator Coordinator() =>
        new(Exports.Exports, ProductionStore, Exports.Import.Jobs, Store, Imager, Options, NullLogger<ProductionVolumeCoordinator>.Instance);

    public ProductionVolumeChunkExecutor Executor(IObjectStore? store = null) =>
        new(Exports.Exports, ProductionStore, Exports.Import.Jobs, Exports.Pdp(), new UnrestrictedFieldAccess(), store ?? Store, Imager, Options, Exports.Audit);

    public JobChunkConsumer Consumer(IObjectStore? store = null) => new(
        Exports.Import.Chunks, [Executor(store)], new InMemoryAuditEventWriter(), new JobLeaseOptions(),
        new JobChunkConsumerOptions { WorkerId = "volume-test-worker" }, NullMessageProcessingMeter.Instance, TimeProvider.System,
        NullLogger<JobChunkConsumer>.Instance);

    /// <summary>Starts a volume run of a finalized production as <paramref name="user"/>.</summary>
    public async Task<ExportCreation> StartAsync(Guid ws, Guid user, Guid productionId, string? key = null)
    {
        var outcome = await Service().StartAsync(ProductionHarness.Principal(user), ws, productionId, key, Ct);
        outcome.Status.Should().Be(ProductionVolumeStartStatus.Accepted, outcome.Reason);
        return outcome.Creation!;
    }

    public async Task<List<ProductionVolumeStep>> CoordinateAsync(Guid ws)
    {
        var steps = new List<ProductionVolumeStep>();
        foreach (var active in await Exports.Exports.GetActiveVolumesAsync(ws, 50, Ct))
        {
            steps.Add(await Coordinator().ProcessAsync(active, Ct));
        }

        return steps;
    }

    public async Task<int> DeliverOpenChunksAsync(Guid ws, Guid jobId, IObjectStore? store = null)
    {
        var delivered = 0;
        foreach (var chunk in await Exports.Import.Jobs.GetChunksAsync(ws, jobId, afterSequence: 0, limit: 500, cancellationToken: Ct))
        {
            if (chunk.Status is JobChunkStatus.Pending or JobChunkStatus.Dispatched or JobChunkStatus.RetryWait)
            {
                await Consumer(store).HandleAsync(Payload(chunk), Received(chunk), Ct);
                delivered++;
            }
        }

        return delivered;
    }

    /// <summary>Plans, runs every chunk and finalizes a run; returns it.</summary>
    public async Task<ExportRecord> RunAsync(ExportCreation creation)
    {
        var ws = creation.Export.WorkspaceId;
        await CoordinateAsync(ws);
        await DeliverOpenChunksAsync(ws, creation.Job.JobId);
        await CoordinateAsync(ws);
        return (await Exports.Exports.GetAsync(ws, creation.Export.ExportId, Ct))!;
    }

    /// <summary>Every registered file of the run of the given kinds by path, with its bytes.</summary>
    public async Task<Dictionary<string, (ExportFileRecord File, byte[] Bytes)>> FilesAsync(ExportRecord volume, IReadOnlyCollection<ExportFileKind>? kinds = null)
    {
        var files = new Dictionary<string, (ExportFileRecord, byte[])>(StringComparer.Ordinal);
        foreach (var file in await Exports.Exports.GetFilesAsync(volume.WorkspaceId, volume.ExportId, kinds ?? Opportunity.Api.Exports.ExportEndpoints.DeliveredKinds, null, 100_000, Ct))
        {
            await using var stream = await Store.OpenReadAsync(ObjectKey.Parse(file.ObjectKey), cancellationToken: Ct);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            files[file.Path] = (file, copy.ToArray());
        }

        return files;
    }

    public static JobChunkMessage Payload(JobChunkInfo chunk) =>
        new() { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = JobChunkOperation.ProductionVolumeChunk };

    public static ReceivedMessage Received(JobChunkInfo chunk) => new(
        new MessageEnvelope
        {
            MessageId = Guid.CreateVersion7(),
            MessageType = MessageTypes.JobChunk,
            SchemaVersion = new SchemaVersion(1, 0),
            WorkspaceId = chunk.WorkspaceId,
            JobId = chunk.JobId,
            CorrelationId = $"corr-{chunk.JobId:N}",
            IdempotencyKey = chunk.IdempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            Attempt = chunk.AttemptCount,
            Payload = JsonSerializer.SerializeToElement(Payload(chunk), MessageJson.PayloadOptions),
        },
        Payload(chunk), WorkQueues.Rendering, false, DeliveryCount: 0, TransportRetry: 0);

    public ValueTask DisposeAsync() => Exports.DisposeAsync();
}
