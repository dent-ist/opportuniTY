using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Data.Jobs;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Jobs;

/// <summary>A migrated database with job repositories and helpers for E06-T02 tests.</summary>
internal sealed class JobDatabase : IAsyncDisposable
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private JobDatabase(CoreSchemaDatabase core, NpgsqlDataSource dataSource)
    {
        Core = core;
        DataSource = dataSource;
        Jobs = new JobRepository(dataSource);
        Chunks = new JobChunkRepository(dataSource);
    }

    public CoreSchemaDatabase Core { get; }

    /// <summary>The data source the repositories use (superuser, or an app-role login for RLS tests).</summary>
    public NpgsqlDataSource DataSource { get; }

    public JobRepository Jobs { get; }

    public JobChunkRepository Chunks { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<JobDatabase> CreateAsync(MigrationPostgresFixture postgres)
    {
        var core = await CoreSchemaDatabase.CreateAsync(postgres);
        return new JobDatabase(core, core.DataSource);
    }

    /// <summary>Same database, repositories connected as a NOBYPASSRLS login in <c>opportunity_app</c>.</summary>
    public async Task<JobDatabase> AsAppRoleAsync()
    {
        var login = "app_" + Guid.NewGuid().ToString("N")[..12];
        const string password = "test-only-password";
        await Core.ExecuteAsync($"CREATE ROLE {login} LOGIN PASSWORD '{password}' IN ROLE opportunity_app");
        var connectionString = new NpgsqlConnectionStringBuilder(Core.ConnectionString) { Username = login, Password = password }.ConnectionString;
        return new JobDatabase(Core, NpgsqlDataSource.Create(connectionString));
    }

    /// <summary>Creates a Running job over one snapshot with <paramref name="chunkCount"/> chunks of 100 ordinals.</summary>
    public async Task<(Guid WorkspaceId, Guid JobId)> CreateRunningJobAsync(
        int chunkCount, int maxAttempts = ChunkRetryPolicy.DefaultMaxAttempts, Guid? workspaceId = null, Guid? initiatedBy = null)
    {
        var ws = workspaceId ?? await Core.CreateWorkspaceAsync();
        var snapshot = Guid.CreateVersion7();
        var created = await Jobs.CreateAsync(new NewJob
        {
            WorkspaceId = ws,
            JobType = JobType.BulkCoding,
            InitiatedBy = initiatedBy ?? Guid.CreateVersion7(),
            TargetSnapshotId = snapshot,
            MaxAttemptsPerChunk = maxAttempts,
        }, Ct);
        (await Jobs.BeginPreparingAsync(ws, created.Job.JobId, Ct)).Applied.Should().BeTrue();
        var plans = ChunkPlanner.SplitByCount(1, chunkCount * 100L, 100)
            .Select(r => new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, r.From, r.To), (int)r.Count))
            .ToList();
        (await Jobs.StartAsync(new JobStartRequest(ws, created.Job.JobId, ChunkOperationKind.BulkCodingChunk, plans), Ct))
            .Status.Should().Be(chunkCount == 0 ? JobStatus.Completed : JobStatus.Running);
        return (ws, created.Job.JobId);
    }

    public async Task<List<JobChunkInfo>> ChunksAsync(Guid workspaceId, Guid jobId) =>
        [.. await Jobs.GetChunksAsync(workspaceId, jobId, cancellationToken: Ct)];

    public async Task<JobInfo> JobAsync(Guid workspaceId, Guid jobId) => (await Jobs.GetAsync(workspaceId, jobId, Ct))!;

    /// <summary>Moves a chunk's lease (or retry wait) into the past instead of sleeping.</summary>
    public Task ExpireAsync(Guid chunkId, TimeSpan ago) =>
        Core.ExecuteAsync(
            "UPDATE opportunity.job_chunk SET lease_expires_at = CASE WHEN status = 3 THEN now() - @ago END, " +
            "available_at = now() - @ago WHERE chunk_id = @chunk",
            ("ago", ago), ("chunk", chunkId));

    /// <summary>
    /// Counters must equal the actual chunk statuses: the O(1) progress may never drift from the rows.
    /// </summary>
    public async Task AssertCountersMatchRowsAsync(Guid workspaceId, Guid jobId)
    {
        var job = await JobAsync(workspaceId, jobId);
        var chunks = await ChunksAsync(workspaceId, jobId);
        job.Counters.ChunksTotal.Should().Be(chunks.Count);
        job.Counters.ChunksCommitted.Should().Be(chunks.Count(c => c.Status == JobChunkStatus.Committed));
        job.Counters.ChunksFailed.Should().Be(chunks.Count(c => c.Status == JobChunkStatus.Failed));
        job.Counters.ChunksCancelled.Should().Be(chunks.Count(c => c.Status == JobChunkStatus.Cancelled));
    }

    public async ValueTask DisposeAsync()
    {
        if (!ReferenceEquals(DataSource, Core.DataSource))
        {
            await DataSource.DisposeAsync();
        }
        else
        {
            await Core.DisposeAsync();
        }
    }
}
