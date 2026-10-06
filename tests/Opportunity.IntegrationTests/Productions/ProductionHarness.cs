using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Productions;
using Opportunity.Application.Snapshots;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.Data.Audit;
using Opportunity.Data.Jobs;
using Opportunity.Data.Productions;
using Opportunity.Data.Security;
using Opportunity.Data.Snapshots;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Production.Productions;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// Productions without a broker (E12-T02/T03): a migrated database (RLS-bound app login), frozen sets made by the
/// snapshot service, the real PDP over PostgreSQL security state, the allocation coordinator and the real chunk
/// consumer with <see cref="BatesChunkExecutor"/>.
/// </summary>
internal sealed class ProductionHarness : IAsyncDisposable
{
    private ProductionHarness(CoreSchemaDatabase db, int documentsPerChunk)
    {
        Db = db;
        Store = new ProductionRepository(db.AppDataSource);
        Snapshots = new DocumentSetSnapshotStore(db.AppDataSource);
        Jobs = new JobRepository(db.AppDataSource);
        Chunks = new JobChunkRepository(db.AppDataSource);
        Audit = new PostgresAuditEventWriter(db.AppDataSource);
        Options = new ProductionJobOptions { DocumentsPerChunk = documentsPerChunk, WorkerId = "production-test-worker" };
    }

    public CoreSchemaDatabase Db { get; }

    public ProductionRepository Store { get; }

    public DocumentSetSnapshotStore Snapshots { get; }

    public JobRepository Jobs { get; }

    public JobChunkRepository Chunks { get; }

    public PostgresAuditEventWriter Audit { get; }

    public ProductionJobOptions Options { get; set; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ProductionHarness> CreateAsync(MigrationPostgresFixture postgres, int documentsPerChunk = 4) =>
        new(await CoreSchemaDatabase.CreateAsync(postgres), documentsPerChunk);

    public async Task<Guid> UserAsync(Guid ws, WorkspaceRole role)
    {
        var id = Guid.CreateVersion7();
        await Db.ExecuteAsync(
            """
            INSERT INTO opportunity.app_user (user_id, issuer, subject, display_name, groups, groups_refreshed_at, last_sign_in_at)
            VALUES (@id, 'https://idp.test', @id::text, 'Production Tester', '{}', now(), now())
            """,
            ("id", id));
        await Db.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @aid, @role, @user)",
            ("ws", ws), ("aid", Guid.CreateVersion7()), ("role", role.Key()), ("user", id));
        return id;
    }

    public static SecurityPrincipal Principal(Guid userId) => new() { UserId = userId, DisplayName = "Production Tester", CorrelationId = "production-test" };

    public IAuthorizationService Pdp() => new AuthorizationService(new PostgresSecurityStateReader(Db.AppDataSource), Audit, TimeProvider.System);

    public DocumentSetSnapshotService SnapshotService() =>
        new(Snapshots, null!, Pdp(), Audit, new SnapshotOptions(), TimeProvider.System);

    public ProductionService Service() => new(
        Store, Snapshots, Db.Fields, new UnrestrictedFieldAccess(), Pdp(), Jobs, TimeProvider.System, NullLogger<ProductionService>.Instance);

    /// <summary>
    /// Documents in families: each entry is one family (first = parent) of (pages, extension) members, with control
    /// numbers <c>{prefix}0001…</c> in order, so the frozen set's order is the families' order.
    /// </summary>
    public async Task<List<Guid>> FamiliesAsync(Guid ws, string prefix, params (int Pages, string Extension)[][] families)
    {
        var ids = new List<Guid>();
        var n = 0;
        foreach (var family in families)
        {
            Guid? parent = null;
            for (var i = 0; i < family.Length; i++)
            {
                var (pages, extension) = family[i];
                var document = await Db.InsertDocumentAsync(ws, $"{prefix}{++n:D4}", d =>
                {
                    d.FileExtension = extension;
                    if (parent is { } p)
                    {
                        d.FamilyId = p;
                        d.ParentDocumentId = p;
                        d.FamilySequence = i;
                    }
                });
                parent ??= document.DocumentId;
                if (pages > 0)
                {
                    var pageSet = Guid.CreateVersion7();
                    await Db.ExecuteAsync(
                        "INSERT INTO opportunity.page_set (workspace_id, page_set_id, document_id, source, page_count, status) VALUES (@ws, @id, @doc, 1, @n, 1)",
                        ("ws", ws), ("id", pageSet), ("doc", document.DocumentId), ("n", pages));
                    await Db.ExecuteAsync("UPDATE opportunity.document SET active_page_set_id = @ps WHERE workspace_id = @ws AND document_id = @doc",
                        ("ws", ws), ("ps", pageSet), ("doc", document.DocumentId));
                }

                ids.Add(document.DocumentId);
            }
        }

        return ids;
    }

    /// <summary>A Ready Production frozen set of the documents, frozen for <paramref name="userId"/>.</summary>
    public async Task<SnapshotRecord> SnapshotAsync(Guid ws, Guid userId, IReadOnlyList<Guid> documentIds)
    {
        var outcome = await SnapshotService().CreateAsync(new Application.Search.SearchCaller(Principal(userId), ws, null),
            new SnapshotCreateRequest(SnapshotPurpose.Production, "Production test", DocumentIds: documentIds), Ct);
        return outcome.Status == SnapshotCreateStatus.Ready
            ? outcome.Snapshot!
            : throw new InvalidOperationException($"Expected a Ready snapshot, got {outcome.Status}.");
    }

    public static ProductionSpecification Spec(string prefix, long start = 1, BatesLevelResource level = BatesLevelResource.Page, int padding = 7) =>
        new(new ProductionBatesSettings(prefix, start, padding, Level: level));

    public async Task<ProductionOutcome> CreateAsync(Guid ws, Guid user, Guid snapshotId, ProductionSpecification specification, string? name = null) =>
        await Service().CreateAsync(Principal(user), ws, new CreateProductionRequest(name, specification, snapshotId), snapshotId, Ct);

    public async Task<ProductionRecord> CreateOkAsync(Guid ws, Guid user, Guid snapshotId, ProductionSpecification specification)
    {
        var outcome = await CreateAsync(ws, user, snapshotId, specification);
        outcome.Status.Should().Be(ProductionOutcomeStatus.Ok, "{0} {1}", outcome.Reason, JsonSerializer.Serialize(outcome.Errors));
        return outcome.Production!;
    }

    public BatesAllocationCoordinator Coordinator() => new(Store, Jobs, Options, NullLogger<BatesAllocationCoordinator>.Instance);

    public BatesChunkExecutor Executor(
#if OPPORTUNITY_FAILPOINTS
        IFaultInjector? faults = null
#endif
        ) => new(Store, Jobs, Pdp()
#if OPPORTUNITY_FAILPOINTS
            , faults
#endif
            );

    public JobChunkConsumer Consumer(
#if OPPORTUNITY_FAILPOINTS
        IFaultInjector? faults = null
#endif
        ) => new(
            Chunks, [Executor(
#if OPPORTUNITY_FAILPOINTS
                faults
#endif
                )], new InMemoryAuditEventWriter(), new JobLeaseOptions(), new JobChunkConsumerOptions { WorkerId = "production-test-worker" },
            NullMessageProcessingMeter.Instance, TimeProvider.System, NullLogger<JobChunkConsumer>.Instance
#if OPPORTUNITY_FAILPOINTS
            , metrics: null, faults
#endif
            );

    /// <summary>One coordinator pass over the workspace's allocating productions.</summary>
    public async Task<List<BatesAllocationStep>> CoordinateAsync(Guid ws)
    {
        var steps = new List<BatesAllocationStep>();
        foreach (var active in await Store.GetActiveAllocationsAsync(ws, 50, Ct))
        {
            steps.Add(await Coordinator().ProcessAsync(active, Ct));
        }

        return steps;
    }

    /// <summary>Delivers every open chunk of the job once, in order, each with a fresh consumer.</summary>
    public async Task<int> DeliverOpenChunksAsync(Guid ws, Guid jobId, Func<JobChunkConsumer>? consumer = null)
    {
        var delivered = 0;
        var after = 0;
        while (true)
        {
            var page = await Jobs.GetChunksAsync(ws, jobId, afterSequence: after, limit: 500, cancellationToken: Ct);
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

    /// <summary>Starts the allocation, plans, runs every chunk and completes; returns the production.</summary>
    public async Task<ProductionRecord> AllocateAsync(Guid ws, Guid user, Guid productionId)
    {
        var outcome = await Service().AllocateAsync(Principal(user), ws, productionId, null, Ct);
        outcome.Status.Should().Be(ProductionOutcomeStatus.Ok, outcome.Reason);
        await CoordinateAsync(ws);
        await DeliverOpenChunksAsync(ws, outcome.Job!.JobId);
        await CoordinateAsync(ws);
        return (await Store.GetAsync(ws, productionId, Ct))!;
    }

    public async Task<List<ProductionDocumentRow>> AssignmentAsync(Guid ws, Guid productionId) =>
        [.. await Store.ReadDocumentsAsync(ws, productionId, 0, 100_000, Ct)];

    public static JobChunkMessage Payload(JobChunkInfo chunk) =>
        new() { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = JobChunkOperation.ProductionChunk };

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
        Payload(chunk), WorkQueues.Production, false, DeliveryCount: 0, TransportRetry: 0);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
