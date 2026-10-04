using System.Diagnostics;
using AwesomeAssertions;
using Opportunity.Application.Coding;
using Opportunity.Application.Search;
using Opportunity.Contracts.Api;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.Search;
using Opportunity.Search.Projection;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E10-T04 end to end (§32 BULK TAG → chunk index tasks → search): a 10,000-document frozen set is bulk coded in ten
/// chunks, the chunk index worker applies the ten IndexChunkTasks (SnapshotRange membership), and the search service
/// finds exactly the coded documents. A document edited interactively after the snapshot keeps its value in PostgreSQL
/// and in OpenSearch (§21: an old bulk write never overwrites a newer interactive edit). Durations are reported.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class BulkCodingSearchabilityTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const int Documents = 10_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_10k_document_bulk_tag_is_searchable_once_its_chunk_index_tasks_are_applied()
    {
        await using var search = await SearchHarness.CreateAsync(openSearch, postgres);
        var ws = await search.WorkspaceAsync();
        await using var bulk = BulkCodingHarness.Over(search.Db);
        var w = await bulk.WorkspaceAsync(ws);
        var user = await bulk.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var reviewer = await bulk.MemberAsync(ws, WorkspaceRole.Reviewer);
        var docs = await bulk.DocumentsAsync(ws, Documents);
        await using var index = await ChunkIndexHarness.OverAsync(
            openSearch, ImportHarness.Over(search.Db.Core), search.Options, new ProjectionOptions());

        var clock = Stopwatch.StartNew();
        var snapshot = await bulk.SnapshotAsync(ws, user, docs);
        snapshot.DocumentCount.Should().Be(Documents);
        var frozenIn = clock.Elapsed;

        var job = await bulk.StartAsync(ws, user, snapshot.SnapshotId,
            new CodingChange(w.Responsive, CodingOperationKind.Set, true),
            new CodingChange(w.Issues, CodingOperationKind.AddChoices, new System.Text.Json.Nodes.JsonArray(w.IssueA)));
        job.Counters.ChunksTotal.Should().Be(10);

        // A reviewer's edit after the snapshot: Q-07 skips it in PostgreSQL, and its newer version wins in the index.
        await bulk.InteractiveAsync(ws, reviewer, docs[42], CodingFieldOperation.Set(w.Responsive, false));

        clock.Restart();
        var done = await bulk.RunAsync(ws, job.JobId);
        var committedIn = clock.Elapsed;
        done.Status.Should().Be(JobStatus.Completed);
        done.Counters.ItemsApplied.Should().Be(Documents - 1);
        done.Counters.ItemsSkippedConcurrentEdit.Should().Be(1);
        done.Counters.IndexTasksTotal.Should().Be(10);

        clock.Restart();
        var tasks = await index.TasksAsync(ws, job.JobId);
        tasks.Should().HaveCount(10).And.OnlyContain(t => t.Kind == IndexTaskKind.BulkCoding && t.Membership.Kind == ChunkMembershipKind.SnapshotRange);
        await index.DeliverAllAsync(ws, job.JobId, parallelism: 4);
        var indexedIn = clock.Elapsed;
        (await index.TasksAsync(ws, job.JobId)).Should().OnlyContain(t => t.Status == IndexChunkTaskStatus.Applied);
        (await bulk.JobAsync(ws, job.JobId)).Counters.IndexTasksApplied.Should().Be(10);
        (await index.CountAsync(ws)).Should().Be(Documents);

        var responsive = await SearchAsync(search, ws, user, "responsive:true");
        responsive.Total.Value.Should().Be(Documents - 1);
        responsive.Total.Relation.Should().Be(TotalRelation.Eq);
        (await SearchAsync(search, ws, user, "responsive:false")).Items.Select(i => i.DocumentId).Should().Equal(docs[42]);
        (await SearchAsync(search, ws, user, "issues:\"Supply contract\"")).Total.Value.Should().Be(Documents);
        (await index.DriftAsync(ws, [docs[42], .. docs.Where((_, i) => i % 97 == 0)])).Should().BeEmpty();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Documents} documents: frozen in {frozenIn.TotalMilliseconds:F0} ms, 10 chunks committed in {committedIn.TotalMilliseconds:F0} ms, " +
            $"indexed in {indexedIn.TotalMilliseconds:F0} ms");
    }

    private static async Task<Opportunity.Contracts.Search.SearchResultPage> SearchAsync(SearchHarness search, Guid ws, Guid user, string query)
    {
        var outcome = await search.SearchAsync(ws, user, query, pageSize: 10, countExact: true);
        outcome.Status.Should().Be(SearchStatus.Ok, string.Join("; ", outcome.QueryErrors.Select(e => e.Code + " " + e.Message)));
        return outcome.Page!;
    }
}
