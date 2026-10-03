using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;

using Opportunity.Application.Jobs;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Jobs;

/// <summary>E06-T02: <c>GET /api/v1/workspaces/{workspaceId}/jobs/{jobId}</c> with separate committed and indexed progress.</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class JobStatusApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Job_status_reports_committed_and_indexed_progress_separately()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 3);
        var claim = (await db.Chunks.ClaimNextAsync(ws, jobId, "w", JobDatabase.Lease, Ct)).Chunk!;
        await db.Chunks.CompleteAsync(claim.Lease, new ChunkCompletion
        {
            ItemsApplied = 98,
            IndexTasks = 1,
            ItemResults = [new JobItemResult(Core.Jobs.JobItemResultKind.SkippedConcurrentEdit, Guid.CreateVersion7(), null, 1005, "ChangedAfterJobStart")],
        }, Ct);

        await using var factory = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.Core.ConnectionString));
        using var client = factory.CreateClient();

        using var running = await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/jobs/{jobId}", UriKind.Relative), Ct);
        running.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await running.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;
        root.GetProperty("jobId").GetGuid().Should().Be(jobId);
        root.GetProperty("status").GetString().Should().Be("running");
        root.GetProperty("jobType").GetString().Should().Be("bulkCoding");
        var committed = root.GetProperty("committed");
        committed.GetProperty("chunksTotal").GetInt64().Should().Be(3);
        committed.GetProperty("chunksCommitted").GetInt64().Should().Be(1);
        committed.GetProperty("chunksPending").GetInt64().Should().Be(2);
        committed.GetProperty("itemsApplied").GetInt64().Should().Be(98);
        committed.GetProperty("itemsSkippedConcurrentEdit").GetInt64().Should().Be(1);
        var indexed = root.GetProperty("indexed");
        indexed.GetProperty("state").GetString().Should().Be("indexing");
        indexed.GetProperty("indexTasksTotal").GetInt64().Should().Be(1);
        indexed.GetProperty("indexTasksApplied").GetInt64().Should().Be(0);

        foreach (var chunk in new[] { 0, 1 })
        {
            var next = (await db.Chunks.ClaimNextAsync(ws, jobId, "w", JobDatabase.Lease, Ct)).Chunk!;
            await db.Chunks.CompleteAsync(next.Lease, ChunkCompletion.Empty, Ct);
        }

        await db.Jobs.RecordIndexTasksAppliedAsync(ws, jobId, 1, Ct);
        using var done = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/api/v1/workspaces/{ws}/jobs/{jobId}", UriKind.Relative), Ct));
        done.RootElement.GetProperty("status").GetString().Should().Be("completed");
        done.RootElement.GetProperty("indexed").GetProperty("state").GetString().Should().Be("current");
        done.RootElement.GetProperty("finishedAt").GetString().Should().EndWith("Z");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Unknown_or_foreign_jobs_are_not_found(bool validWorkspace, bool validJob)
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 1);
        await using var factory = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:App", db.Core.ConnectionString));
        using var client = factory.CreateClient();
        var workspace = validWorkspace ? ws.ToString() : Guid.CreateVersion7().ToString();
        var job = validJob ? jobId.ToString() : "not-a-guid";

        using var response = await client.GetAsync(new Uri($"/api/v1/workspaces/{workspace}/jobs/{job}", UriKind.Relative), Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not-found");
    }
}
