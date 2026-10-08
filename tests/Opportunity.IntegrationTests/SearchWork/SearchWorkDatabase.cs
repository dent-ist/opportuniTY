using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Core.Coding;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Data.Jobs;
using Opportunity.Data.SearchWork;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// A migrated database with the search work repositories (E06-T03), all connected as an <c>opportunity_app</c> login so
/// row-level security applies. <see cref="Core"/> arranges and inspects as the superuser.
/// </summary>
internal sealed class SearchWorkDatabase : IAsyncDisposable
{
    public static readonly Guid Reviewer = Guid.CreateVersion7();

    private SearchWorkDatabase(CoreSchemaDatabase core)
    {
        Core = core;
        Outbox = new SearchOutboxRepository(core.AppDataSource);
        Tasks = new IndexChunkTaskRepository(core.AppDataSource);
        Maintenance = new SearchWorkMaintenance(core.AppDataSource);
        Jobs = new JobRepository(core.AppDataSource);
        Chunks = new JobChunkRepository(core.AppDataSource);
    }

    public CoreSchemaDatabase Core { get; }

    public SearchOutboxRepository Outbox { get; }

    public IndexChunkTaskRepository Tasks { get; }

    public SearchWorkMaintenance Maintenance { get; }

    public JobRepository Jobs { get; }

    public JobChunkRepository Chunks { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SearchWorkDatabase> CreateAsync(MigrationPostgresFixture postgres) =>
        new(await CoreSchemaDatabase.CreateAsync(postgres));

    /// <summary>A workspace with a plain coding field and a security-affecting one (Q-11).</summary>
    public async Task<TestWorkspace> WorkspaceAsync(int documents = 0)
    {
        var ws = await Core.CreateWorkspaceAsync();
        await Core.Fields.InitializeWorkspaceAsync(ws, Ct);
        var responsive = (await Core.Fields.CreateFieldAsync(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding), Ct))
            .Value!.FieldId;
        var privilege = (await Core.Fields.CreateFieldAsync(new NewField(ws, "Privilege Call", FieldType.SingleChoice,
            FieldStorage.Coding, SecurityClass: SecurityClass.PrivilegeStatus), Ct)).Value!.FieldId;
        var privileged = (await Core.Fields.AddChoiceAsync(ws, privilege, "Privileged", Ct)).Value!.ChoiceId;
        var docs = Enumerable.Range(1, documents)
            .Select(i => Document.Create(ws, $"DOC{i:D6}", caseSensitive: false))
            .ToList();
        if (docs.Count > 0)
        {
            await Core.Documents.InsertManyAsync(ws, docs, Ct);
        }

        return new TestWorkspace(ws, responsive, privilege, privileged, [.. docs.Select(d => d.DocumentId).Order()]);
    }

    public static CodingWriteRequest Interactive(Guid ws, Guid documentId, params CodingFieldOperation[] operations) => new()
    {
        WorkspaceId = ws,
        IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
        Actor = new CodingActor(Reviewer, CodingActorType.Human),
        Documents = [new CodingTarget(documentId)],
        Operations = operations,
    };

    public static CodingWriteRequest SetResponsive(TestWorkspace w, Guid documentId, bool value) =>
        Interactive(w.Id, documentId, CodingFieldOperation.Set(w.Responsive, JsonValue.Create(value)));

    /// <summary>Creates a Running job with <paramref name="plans"/> and claims its first chunk.</summary>
    public async Task<ClaimedChunk> RunningJobChunkAsync(
        Guid ws, JobType type, ChunkOperationKind operation, IReadOnlyList<ChunkPlan> plans, Guid? snapshotId = null, Guid? importBatchId = null)
    {
        var created = await Jobs.CreateAsync(new NewJob
        {
            WorkspaceId = ws,
            JobType = type,
            InitiatedBy = Reviewer,
            TargetSnapshotId = snapshotId,
            ImportBatchId = importBatchId,
        }, Ct);
        (await Jobs.BeginPreparingAsync(ws, created.Job.JobId, Ct)).Applied.Should().BeTrue();
        (await Jobs.StartAsync(new JobStartRequest(ws, created.Job.JobId, operation, plans), Ct)).Status.Should().Be(JobStatus.Running);
        var claim = await Chunks.ClaimNextAsync(ws, created.Job.JobId, "worker-1", TimeSpan.FromMinutes(1), Ct);
        claim.Claimed.Should().BeTrue();
        return claim.Chunk!;
    }

    public Task<long> CountAsync(string table, Guid ws) =>
        Core.ScalarAsync<long>($"SELECT count(*) FROM opportunity.{table} WHERE workspace_id = @ws", ("ws", ws));

    public ValueTask DisposeAsync() => Core.DisposeAsync();
}

internal sealed record TestWorkspace(Guid Id, int Responsive, int Privilege, int Privileged, IReadOnlyList<Guid> Documents);
