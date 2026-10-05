using System.Net.Http.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Search;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Search;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Data.Jobs;
using Opportunity.Data.Search;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Indexing;
using Opportunity.Search.Projection;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T08 end to end on real PostgreSQL and OpenSearch with the real bulk-coding chunk handler, chunk index worker,
/// search service and watermark ticker: during a Mass Edit, the job's searchable state and the search freshness stay
/// "not current" until the refresh-aware watermark reaches the job's generation. Automatic index refresh is switched
/// off, so writes become searchable only through the ticker's observed refresh: a task that is applied (acknowledged by
/// OpenSearch) but not yet refreshed must not count.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class SearchFreshnessEndToEndTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const int Documents = 2_500;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task During_a_bulk_job_neither_the_job_nor_search_is_current_until_the_watermark_reaches_the_job_generation()
    {
        await using var search = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await search.WorkspaceAsync();
        await using var bulk = BulkCodingHarness.Over(search.Db);
        var w = await bulk.WorkspaceAsync(ws);
        var user = await bulk.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var docs = await bulk.DocumentsAsync(ws, Documents);
        await using var index = await ChunkIndexHarness.OverAsync(
            openSearch, ImportHarness.Over(search.Db.Core), search.Options, new ProjectionOptions());
        var jobs = new JobOperationsStore(search.Db.Core.AppDataSource);
        var freshness = new SearchWatermarkStore(search.Db.Core.AppDataSource);

        (await index.TickWatermarkAsync(ws)).IsCurrent.Should().BeTrue("nothing was committed yet");
        await DisableAutomaticRefreshAsync(search, ws);

        var snapshot = await bulk.SnapshotAsync(ws, user, docs);
        var job = await bulk.StartAsync(ws, user, snapshot.SnapshotId, new CodingChange(w.Responsive, CodingOperationKind.Set, true));
        (await bulk.RunAsync(ws, job.JobId)).Status.Should().Be(JobStatus.Completed);
        var tasks = await index.TasksAsync(ws, job.JobId);
        tasks.Should().HaveCount(3);
        var jobGeneration = (await jobs.GetDetailAsync(ws, job.JobId, Ct))!.Overview.Job.JobGeneration!.Value;
        jobGeneration.Should().Be(tasks.Max(t => t.SearchGeneration!.Value), "the job generation is its last chunk task's generation");

        // Committed, nothing applied: not current anywhere.
        await ExpectNotCurrentAsync(search, jobs, ws, user, job.JobId, jobGeneration, SearchabilityState.Pending, visible: 0);

        // Out of order: the last chunk first. Each task is applied (OpenSearch acknowledged it), checked before the
        // ticker observes a refresh, then after; only the final refresh covers the job's generation.
        var visible = 0L;
        foreach (var task in tasks.OrderByDescending(t => t.SearchGeneration))
        {
            await index.DeliverAsync(task);
            var applied = await freshness.ReadAsync(ws, Ct);
            var last = applied.AppliedGeneration >= jobGeneration;
            applied.IndexedThroughGeneration.Should().BeLessThan(jobGeneration, "acknowledged writes are not searchable before a refresh");
            await ExpectNotCurrentAsync(search, jobs, ws, user, job.JobId, jobGeneration, SearchabilityState.CatchingUp, visible);

            var ticked = await index.TickWatermarkAsync(ws);
            ticked.IndexedThroughGeneration.Should().Be(applied.AppliedGeneration, "the refresh made everything applied before it searchable");
            if (!last)
            {
                ticked.IndexedThroughGeneration.Should().BeLessThan(jobGeneration);
                await ExpectNotCurrentAsync(search, jobs, ws, user, job.JobId, jobGeneration, SearchabilityState.CatchingUp, visible: null);
            }

            visible = (await SearchAsync(search, ws, user, "responsive:true")).Total.Value;
        }

        var reading = await freshness.ReadAsync(ws, Ct);
        reading.IndexedThroughGeneration.Should().BeGreaterThanOrEqualTo(jobGeneration);
        reading.Level.Should().Be(SearchFreshnessLevel.Current);
        var detail = (await jobs.GetDetailAsync(ws, job.JobId, Ct))!;
        JobSearchability.Evaluate(detail.Overview.Job, detail.IndexedThroughGeneration).State.Should().Be(SearchabilityState.Current);

        var page = await SearchAsync(search, ws, user, "responsive:true");
        page.Total.Should().Be(new TotalCount(Documents, TotalRelation.Eq));
        page.Freshness.Current.Should().BeTrue();
        page.Freshness.ServedGeneration.Should().Be(reading.IndexedThroughGeneration);
        page.Freshness.State.Should().Be(SearchFreshnessState.Current);
        page.Freshness.IndexedThroughGeneration.Should().Be(reading.IndexedThroughGeneration);
        page.Freshness.PendingChanges.Should().Be(0);
        page.Freshness.LagSeconds.Should().Be(0);
    }

    private static async Task ExpectNotCurrentAsync(
        SearchHarness search, JobOperationsStore jobs, Guid ws, Guid user, Guid jobId, long jobGeneration, SearchabilityState state, long? visible)
    {
        var detail = (await jobs.GetDetailAsync(ws, jobId, Ct))!;
        detail.IndexedThroughGeneration.Should().BeLessThan(jobGeneration);
        var (_, _, evaluated) = JobSearchability.Evaluate(detail.Overview.Job, detail.IndexedThroughGeneration);
        evaluated.Should().NotBe(SearchabilityState.Current);
        if (state == SearchabilityState.Pending)
        {
            evaluated.Should().Be(SearchabilityState.Pending);
        }

        var page = await SearchAsync(search, ws, user, "responsive:true");
        page.Freshness.Current.Should().BeFalse("isProjectionCurrent stays false until the watermark reaches the job's generation");
        page.Freshness.State.Should().Be(SearchFreshnessState.Updating);
        page.Freshness.IndexedThroughGeneration.Should().BeLessThan(jobGeneration);
        page.Freshness.PendingChanges.Should().BePositive();
        page.Freshness.LagSeconds.Should().BeGreaterThanOrEqualTo(0);
        if (visible is { } expected)
        {
            page.Total.Value.Should().Be(expected, "an applied but unrefreshed chunk is not searchable yet");
        }
    }

    private static async Task<SearchResultPage> SearchAsync(SearchHarness search, Guid ws, Guid user, string query)
    {
        var outcome = await search.SearchAsync(ws, user, query, pageSize: 10, countExact: true);
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)));
        return outcome.Page!;
    }

    /// <summary>Only explicit refreshes (the watermark ticker's) make writes searchable from now on.</summary>
    private static async Task DisableAutomaticRefreshAsync(SearchHarness search, Guid ws)
    {
        var placement = await search.Indexes.ResolveAsync(ws, IndexPurpose.Write, Ct);
        foreach (var target in placement.WriteTargets)
        {
            using var response = await search.OpenSearchHttp.PutAsJsonAsync(
                $"{target.Index}/_settings", new JsonObject { ["index"] = new JsonObject { ["refresh_interval"] = "-1" } }, Ct);
            response.EnsureSuccessStatusCode();
        }
    }
}
