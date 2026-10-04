using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Identity;
using Opportunity.Application.Jobs;
using Opportunity.Application.Snapshots;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.Snapshots;
using Opportunity.Data.Coding;
using Opportunity.Data.Identity;
using Opportunity.Data.Jobs;
using Opportunity.Data.Snapshots;
using Opportunity.IntegrationTests.Authorization;
using Opportunity.IntegrationTests.Jobs;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;
using Opportunity.Security.Authorization;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Jobs.Faults;
#endif

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// Bulk coding (E10-T04) over a migrated PostgreSQL as the RLS-bound app login: the real PDP, job, chunk, snapshot,
/// coding and user stores, the submission use case and the chunk executor behind the idempotent chunk consumer. Each
/// call runs in its own DI scope, like one request or one delivery (the PDP caches principal state per scope only, so a
/// fresh scope per chunk is what makes the per-chunk re-authorization real). The dispatcher is stood in for by
/// <see cref="DeliverAsync"/>, so tests decide which chunk messages arrive, how often and when.
/// </summary>
internal sealed class BulkCodingHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly bool _ownsDatabase;

    private BulkCodingHarness(AuthorizationDatabase db, bool ownsDatabase, IRestrictionClassBinding? binding)
    {
        Db = db;
        _ownsDatabase = ownsDatabase;
        var app = db.Core.AppDataSource;
        Jobs = new JobRepository(app);
        Chunks = new JobChunkRepository(app);
        Snapshots = new DocumentSetSnapshotStore(app);
        Coding = new CodingRepository(app, binding);

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAuditEventWriter>(Audit);
        services.AddSingleton<ISecurityStateReader>(db.Reader);
        services.AddSingleton<IJobRepository>(Jobs);
        services.AddSingleton<IJobChunkRepository>(Chunks);
        services.AddSingleton<IDocumentSetSnapshotStore>(Snapshots);
        services.AddSingleton<ICodingRepository>(Coding);
        services.AddSingleton<IFieldCatalogRepository>(db.Core.Fields);
        services.AddSingleton<IUserDirectory>(new PostgresUserDirectory(app));
        services.AddSingleton<IFieldAccessFilter, UnrestrictedFieldAccess>();
        services.AddOpportunityAuthorization();
        services.AddScoped<BulkCodingService>();
        services.AddScoped<BulkCodingChunkExecutor>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public AuthorizationDatabase Db { get; }

    public JobRepository Jobs { get; }

    public JobChunkRepository Chunks { get; }

    public DocumentSetSnapshotStore Snapshots { get; }

    public CodingRepository Coding { get; }

    /// <summary>Audit events written outside a store transaction (the PDP's <c>AuthZ.Denied</c>, consumer rejections).</summary>
    public InMemoryAuditEventWriter Audit { get; } = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<BulkCodingHarness> CreateAsync(MigrationPostgresFixture postgres, IRestrictionClassBinding? binding = null) =>
        new(await AuthorizationDatabase.CreateAsync(postgres), ownsDatabase: true, binding);

    /// <summary>Over another harness's database (owned by the caller), e.g. a search harness's.</summary>
    public static BulkCodingHarness Over(AuthorizationDatabase db) => new(db, ownsDatabase: false, null);

    /// <summary>A workspace with the coding fields of <see cref="CodingApiHarness.WorkspaceAsync"/>.</summary>
    public Task<CodingWorkspace> WorkspaceAsync(Guid? workspaceId = null) => CodingApiHarness.WorkspaceAsync(Db.Core, workspaceId);

    public async Task<Guid> MemberAsync(Guid workspaceId, Core.Security.WorkspaceRole role)
    {
        var user = await Db.CreateUserAsync();
        await Db.AssignAsync(workspaceId, role, user);
        return user;
    }

    /// <summary><paramref name="count"/> documents inserted in two statements; their IDs in control-number order.</summary>
    public async Task<IReadOnlyList<Guid>> DocumentsAsync(Guid ws, int count, string prefix = "BULK")
    {
        await Db.Core.ExecuteAsync(
            """
            WITH d AS (SELECT n, gen_random_uuid() AS id FROM generate_series(1, @n) AS n)
            INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id)
            SELECT @ws, d.id, @prefix || lpad(d.n::text, 7, '0'), @prefix || lpad(d.n::text, 7, '0'), d.id FROM d
            """,
            ("ws", ws), ("n", count), ("prefix", prefix));
        await Db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.document_projection_state (workspace_id, document_id)
            SELECT workspace_id, document_id FROM opportunity.document WHERE workspace_id = @ws
            ON CONFLICT DO NOTHING
            """,
            ("ws", ws));
        return [.. (await Db.Core.ColumnAsync(
                $"SELECT document_id::text FROM opportunity.document WHERE workspace_id = '{ws}' AND control_number LIKE '{prefix}%' ORDER BY control_number"))
            .Select(Guid.Parse)];
    }

    /// <summary>
    /// A Ready <c>BulkCoding</c> snapshot of <paramref name="documentIds"/> created by <paramref name="user"/> (staged and
    /// frozen through the store; the freeze reads every member's DocumentVersion as its Q-07 baseline).
    /// </summary>
    public async Task<SnapshotRecord> SnapshotAsync(Guid ws, Guid user, IReadOnlyList<Guid> documentIds)
    {
        var created = await Snapshots.CreateAsync(new NewSnapshot
        {
            WorkspaceId = ws,
            Name = "Mass Edit test",
            Purpose = SnapshotPurpose.BulkCoding,
            SourceKind = SnapshotSourceKind.DocumentIds,
            RequestedCount = documentIds.Count,
            CreatedBy = user,
            CreatedByDisplay = "user " + user.ToString("N")[..6],
            ClaimOwner = "bulk-test",
            ClaimLease = TimeSpan.FromMinutes(5),
        }, Ct);
        foreach (var batch in documentIds.Chunk(5_000))
        {
            await Snapshots.StageAsync(ws, created.Snapshot.SnapshotId, batch, SnapshotInclusionReason.Explicit, Ct);
        }

        // Fresh test tables have no statistics until autovacuum gets to them; without them the freeze joins badly.
        await Db.Core.ExecuteAsync(
            "ANALYZE opportunity.document, opportunity.document_projection_state, opportunity.document_set_snapshot_stage");

        var frozen = await Snapshots.FreezeAsync(
            new SnapshotFreezeRequest { WorkspaceId = ws, SnapshotId = created.Snapshot.SnapshotId, ClaimOwner = "bulk-test", SelectedAt = DateTimeOffset.UtcNow },
            (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()),
            Ct);
        frozen.Outcome.Should().Be(SnapshotFreezeOutcome.Frozen);
        frozen.Snapshot!.Status.Should().Be(SnapshotStatus.Ready);
        return frozen.Snapshot;
    }

    public async Task<BulkCodingSubmitOutcome> SubmitAsync(
        Guid ws, Guid user, Guid snapshotId, IReadOnlyList<CodingChange> operations, string? key = null)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BulkCodingService>().SubmitAsync(
            Caller(ws, user), new BulkCodingSubmission(snapshotId, operations, key ?? Guid.NewGuid().ToString("N")), Ct);
    }

    /// <summary>Submits and asserts the job was accepted (Running, or Completed for an empty set).</summary>
    public async Task<JobInfo> StartAsync(Guid ws, Guid user, Guid snapshotId, params CodingChange[] operations)
    {
        var outcome = await SubmitAsync(ws, user, snapshotId, operations);
        outcome.Status.Should().Be(BulkCodingSubmitStatus.Accepted, string.Join("; ", outcome.Errors.Select(e => e.Message)));
        return outcome.Job!;
    }

    public async Task<BulkCodingReportPage> ReportAsync(Guid ws, Guid user, Guid jobId, BulkCodingOutcome outcome, int limit = 1_000)
    {
        await using var scope = _services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BulkCodingService>();
        var job = await service.GetJobAsync(Caller(ws, user), jobId, Ct);
        job.Should().NotBeNull();
        var items = new List<BulkCodingReportItem>();
        string? position = null;
        do
        {
            var page = (await service.GetReportAsync(job!, outcome, position, limit, Ct))!;
            items.AddRange(page.Items);
            position = page.Next;
        }
        while (position is not null);

        return new BulkCodingReportPage(items, null);
    }

    public static CodingCaller Caller(Guid ws, Guid user) =>
        new(new SecurityPrincipal { UserId = user, DisplayName = "user " + user.ToString("N")[..6] }, ws);

    public Task<List<JobChunkInfo>> ChunksAsync(Guid ws, Guid jobId) =>
        Jobs.GetChunksAsync(ws, jobId, cancellationToken: Ct).ContinueWith(t => t.Result.ToList(), TaskScheduler.Default);

    public async Task<JobInfo> JobAsync(Guid ws, Guid jobId) => (await Jobs.GetAsync(ws, jobId, Ct))!;

    /// <summary>A consumer with this harness's executor, in a fresh scope (one delivery).</summary>
#if OPPORTUNITY_FAILPOINTS
    public async Task DeliverAsync(JobChunkInfo chunk, IFaultInjector? faults = null, JobLeaseOptions? lease = null, bool redelivered = false)
#else
    public async Task DeliverAsync(JobChunkInfo chunk, JobLeaseOptions? lease = null, bool redelivered = false)
#endif
    {
        await using var scope = _services.CreateAsyncScope();
        var consumer = new JobChunkConsumer(
            Chunks, [scope.ServiceProvider.GetRequiredService<BulkCodingChunkExecutor>()], Audit, lease ?? new JobLeaseOptions(),
            ChunkMessages.WorkerOptions, NullMessageProcessingMeter.Instance, TimeProvider.System, NullLogger<JobChunkConsumer>.Instance
#if OPPORTUNITY_FAILPOINTS
            , metrics: null, faults
#endif
            );
        await consumer.HandleAsync(ChunkMessages.Payload(chunk), ChunkMessages.Received(chunk, redelivered), Ct);
    }

    /// <summary>
    /// Delivers every open chunk once, in sequence order (re-reading each before delivery), running
    /// <paramref name="before"/> first; returns the job afterwards.
    /// </summary>
    public async Task<JobInfo> RunAsync(Guid ws, Guid jobId, Func<JobChunkInfo, Task>? before = null)
    {
        foreach (var chunk in await ChunksAsync(ws, jobId))
        {
            if (before is not null)
            {
                await before(chunk);
            }

            var current = (await ChunksAsync(ws, jobId)).Single(c => c.ChunkId == chunk.ChunkId);
            if (current.Status is JobChunkStatus.Pending or JobChunkStatus.Dispatched or JobChunkStatus.RetryWait)
            {
                await DeliverAsync(current);
            }
        }

        return await JobAsync(ws, jobId);
    }

    /// <summary>An interactive save by <paramref name="user"/>, as the coding API makes it (its own outbox row and version).</summary>
    public async Task<CodingWriteResult> InteractiveAsync(Guid ws, Guid user, Guid documentId, params CodingFieldOperation[] operations)
    {
        var result = await Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "api:" + Guid.NewGuid().ToString("N"),
            Actor = new CodingActor(user, CodingActorType.Human),
            Documents = [new CodingTarget(documentId)],
            Operations = operations,
        }, Ct);
        result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        return result;
    }

    /// <summary>Current values of one field per document (absent when never coded or cleared).</summary>
    public async Task<Dictionary<Guid, JsonNode?>> ValuesAsync(Guid ws, IReadOnlyCollection<Guid> documentIds, int fieldId)
    {
        var values = new Dictionary<Guid, JsonNode?>();
        foreach (var batch in documentIds.Chunk(2_000))
        {
            foreach (var document in await Coding.GetCurrentAsync(ws, batch, Ct))
            {
                values[document.DocumentId] = document.Fields.SingleOrDefault(f => f.FieldId == fieldId)?.Value;
            }
        }

        return values;
    }

    /// <summary>The job's coding events, as (document, field, kind) rows.</summary>
    public async Task<List<(Guid DocumentId, int FieldId, CodingEventKind Kind)>> EventsAsync(Guid ws, Guid jobId)
    {
        var events = new List<(Guid, int, CodingEventKind)>();
        CodingEventCursor? after = null;
        do
        {
            var page = await Coding.GetEventsAsync(new CodingEventQuery(ws) { JobId = jobId, After = after, Limit = 1_000 }, Ct);
            events.AddRange(page.Events.Select(e => (e.DocumentId, e.FieldId, e.Kind)));
            after = page.Next;
        }
        while (after is not null);

        return events;
    }

    /// <summary>Stored audit actions of the job (category.action → count), from the PostgreSQL audit store.</summary>
    public async Task<Dictionary<string, long>> AuditActionsAsync(Guid ws, Guid jobId)
    {
        var rows = await Db.Core.ColumnAsync(
            $"SELECT category || '.' || action || '|' || count(*)::text FROM audit.audit_event WHERE workspace_id = '{ws}' AND job_id = '{jobId}' GROUP BY category, action");
        return rows.Select(r => r.Split('|')).ToDictionary(r => r[0], r => long.Parse(r[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_ownsDatabase)
        {
            await Db.DisposeAsync();
        }
    }
}
