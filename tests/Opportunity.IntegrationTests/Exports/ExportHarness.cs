using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Exports;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Search;
using Opportunity.Application.Snapshots;
using Opportunity.Application.Storage;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Data.Audit;
using Opportunity.Data.Exports;
using Opportunity.Data.Security;
using Opportunity.Data.Snapshots;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Production.Exports;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Exports;

/// <summary>
/// The export pipeline without a broker (E12-T01): the import harness's migrated database (RLS-bound app login) and
/// file-system object store, frozen sets made by the snapshot service, the real PDP over PostgreSQL security state, the
/// coordinator (plan, finalize) and the real chunk consumer with <see cref="ExportChunkExecutor"/>, one DI-scope-like
/// PDP per delivery as in the worker.
/// </summary>
internal sealed class ExportHarness : IAsyncDisposable
{
    private ExportHarness(ImportHarness import, int documentsPerChunk)
    {
        Import = import;
        Exports = new ExportRepository(import.Db.AppDataSource);
        Snapshots = new DocumentSetSnapshotStore(import.Db.AppDataSource);
        Audit = new PostgresAuditEventWriter(import.Db.AppDataSource);
        Options = new ExportJobOptions { DocumentsPerChunk = documentsPerChunk, WorkerId = "export-test-worker", FileConcurrency = 3 };
    }

    public ImportHarness Import { get; }

    public CoreSchemaDatabase Db => Import.Db;

    public IObjectStore Store => Import.Store;

    public ExportRepository Exports { get; }

    public DocumentSetSnapshotStore Snapshots { get; }

    public PostgresAuditEventWriter Audit { get; }

    public ExportJobOptions Options { get; set; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ExportHarness> CreateAsync(MigrationPostgresFixture postgres, int documentsPerChunk = 7, int importRowsPerChunk = 500) =>
        new(await ImportHarness.CreateAsync(postgres, rowsPerChunk: importRowsPerChunk), documentsPerChunk);

    /// <summary>The export pipeline over another harness's import pipeline (its database and object store).</summary>
    public static ExportHarness Over(ImportHarness import, int documentsPerChunk = 7) => new(import, documentsPerChunk);

    /// <summary>A user with <paramref name="role"/> in the workspace (and an installation user record with groups).</summary>
    public async Task<Guid> UserAsync(Guid ws, WorkspaceRole role, Guid? userId = null)
    {
        var id = userId ?? Guid.CreateVersion7();
        await Db.ExecuteAsync(
            """
            INSERT INTO opportunity.app_user (user_id, issuer, subject, display_name, groups, groups_refreshed_at, last_sign_in_at)
            VALUES (@id, 'https://idp.test', @id::text, 'Export Tester', '{}', now(), now())
            ON CONFLICT DO NOTHING
            """,
            ("id", id));
        await Db.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @aid, @role, @user)",
            ("ws", ws), ("aid", Guid.CreateVersion7()), ("role", role.Key()), ("user", id));
        return id;
    }

    public static SecurityPrincipal Principal(Guid userId) => new() { UserId = userId, DisplayName = "Export Tester", CorrelationId = "export-test" };

    public IAuthorizationService Pdp() => new AuthorizationService(new PostgresSecurityStateReader(Db.AppDataSource), Audit, TimeProvider.System);

    /// <summary>A Ready Export frozen set of the documents (explicit IDs), frozen for <paramref name="userId"/>.</summary>
    public async Task<SnapshotRecord> SnapshotAsync(Guid ws, Guid userId, IReadOnlyList<Guid> documentIds)
    {
        var service = new DocumentSetSnapshotService(Snapshots, null!, Pdp(), Audit, new SnapshotOptions(), TimeProvider.System);
        var outcome = await service.CreateAsync(new SearchCaller(Principal(userId), ws, null),
            new SnapshotCreateRequest(SnapshotPurpose.Export, "Export test", DocumentIds: documentIds), Ct);
        return outcome.Status == SnapshotCreateStatus.Ready
            ? outcome.Snapshot!
            : throw new InvalidOperationException($"Expected a Ready snapshot, got {outcome.Status}.");
    }

    public ExportService Service() => new(Exports, Snapshots, Db.Fields, new UnrestrictedFieldAccess(), Pdp(), TimeProvider.System);

    public async Task<ExportCreation> CreateAsync(Guid ws, Guid userId, CreateExportRequest request, string? key = null)
    {
        var outcome = await Service().CreateAsync(Principal(userId), ws, request, key, Ct);
        return outcome.Status == ExportCreateStatus.Accepted
            ? outcome.Creation!
            : throw new InvalidOperationException($"Export not created: {outcome.Status} {JsonSerializer.Serialize(outcome.Errors)}");
    }

    public ExportCoordinator Coordinator(IObjectStore? store = null) =>
        new(Exports, Snapshots, Import.Jobs, store ?? Store, Options, NullLogger<ExportCoordinator>.Instance);

    public ExportChunkExecutor Executor(IObjectStore? store = null) =>
        new(Exports, Snapshots, Import.Jobs, Pdp(), new UnrestrictedFieldAccess(), Audit, store ?? Store, Options);

    public JobChunkConsumer Consumer(
#if OPPORTUNITY_FAILPOINTS
        IFaultInjector? faults = null,
#endif
        JobLeaseOptions? lease = null, IObjectStore? store = null) => new(
            Import.Chunks, [Executor(store)], new InMemoryAuditEventWriter(), lease ?? new JobLeaseOptions(),
            new JobChunkConsumerOptions { WorkerId = "export-test-worker" }, NullMessageProcessingMeter.Instance, TimeProvider.System,
            NullLogger<JobChunkConsumer>.Instance
#if OPPORTUNITY_FAILPOINTS
            , metrics: null, faults
#endif
            );

    /// <summary>Runs one coordinator pass over the workspace's running exports.</summary>
    public async Task<List<ExportStep>> CoordinateAsync(Guid ws, IObjectStore? store = null)
    {
        var steps = new List<ExportStep>();
        foreach (var active in await Exports.GetActiveAsync(ws, 50, Ct))
        {
            steps.Add(await Coordinator(store).ProcessAsync(active, Ct));
        }

        return steps;
    }

    /// <summary>Delivers every open chunk of the job once, in order, each with a fresh consumer (a fresh PDP scope).</summary>
    public async Task<int> DeliverOpenChunksAsync(Guid ws, Guid jobId, Func<JobChunkConsumer>? consumer = null)
    {
        var delivered = 0;
        var after = 0;
        while (true)
        {
            var page = await Import.Jobs.GetChunksAsync(ws, jobId, afterSequence: after, limit: 500, cancellationToken: Ct);
            foreach (var chunk in page)
            {
                if (chunk.Status is JobChunkStatus.Pending or JobChunkStatus.Dispatched or JobChunkStatus.RetryWait)
                {
                    await (consumer ?? (() => Consumer())).Invoke().HandleAsync(Payload(chunk), Received(chunk), Ct);
                    delivered++;
                }
            }

            if (page.Count < 500)
            {
                return delivered;
            }

            after = page[^1].Sequence;
        }
    }

    /// <summary>Plans, runs every chunk and finalizes; returns the export.</summary>
    public async Task<ExportRecord> RunAsync(ExportCreation creation)
    {
        var ws = creation.Export.WorkspaceId;
        await CoordinateAsync(ws);
        await DeliverOpenChunksAsync(ws, creation.Job.JobId);
        await CoordinateAsync(ws);
        return (await Exports.GetAsync(ws, creation.Export.ExportId, Ct))!;
    }

    /// <summary>Every delivered file of the export by package path, with its bytes read back from object storage.</summary>
    public async Task<Dictionary<string, (ExportFileRecord File, byte[] Bytes)>> FilesAsync(ExportRecord export)
    {
        var files = new Dictionary<string, (ExportFileRecord, byte[])>(StringComparer.Ordinal);
        foreach (var file in await Exports.GetFilesAsync(export.WorkspaceId, export.ExportId, Opportunity.Api.Exports.ExportEndpoints.DeliveredKinds, null, 100_000, Ct))
        {
            await using var stream = await Store.OpenReadAsync(ObjectKey.Parse(file.ObjectKey), cancellationToken: Ct);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            files[file.Path] = (file, copy.ToArray());
        }

        return files;
    }

    /// <summary>Writes the package into <paramref name="directory"/> as a recipient would unpack it.</summary>
    public async Task ExtractAsync(ExportRecord export, string directory)
    {
        foreach (var (path, (_, bytes)) in await FilesAsync(export))
        {
            var target = Path.Combine([directory, .. path.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, Ct);
        }
    }

    public static JobChunkMessage Payload(JobChunkInfo chunk) =>
        new() { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = JobChunkOperation.ExportChunk };

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
        Payload(chunk), WorkQueues.Export, false, DeliveryCount: 0, TransportRetry: 0);

    public ValueTask DisposeAsync() => Import.DisposeAsync();
}
