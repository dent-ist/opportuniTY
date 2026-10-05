using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Jobs;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E06-T06 acceptance against real PostgreSQL and OpenSearch: an IndexChunkTask that failed permanently is replayed from
/// PostgreSQL after its cause is fixed (ADR-010 §7.4: reset to Pending, the dispatcher publishes it again); the worker
/// then indexes the documents' current PostgreSQL state and, after the next refresh, the visible watermark passes the
/// job's generation. A second replay finds nothing to do.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class IndexTaskReplayTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replaying_a_failed_index_task_after_fixing_the_cause_indexes_current_state_and_advances_the_watermark()
    {
        await using var h = await ChunkIndexHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Import.WorkspaceAsync();
        var batch = await h.Import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(
            [["BEGDOC", "CUSTODIAN"], .. Enumerable.Range(1, 12).Select(i => new[] { $"REPLAY-{i:D4}", "Doe" })])));
        (await h.Import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        var task = (await h.TasksAsync(ws, batch.JobId)).Single();
        var store = new JobOperationsStore(h.Import.Db.AppDataSource);

        // The cause: OpenSearch rejects every document with a mapping error, a permanent failure.
        h.Hook.OnBulk = async (_, _, response) =>
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
            foreach (var item in body["items"]!.AsArray())
            {
                var result = item!.AsObject().First().Value!.AsObject();
                result["status"] = 400;
                result["error"] = new JsonObject { ["type"] = "mapper_parsing_exception", ["reason"] = "simulated mapping conflict" };
            }

            body["errors"] = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        };
        await h.DeliverAsync(task);
        (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!.Status.Should().Be(IndexChunkTaskStatus.Failed);
        var failed = (await store.GetDetailAsync(ws, batch.JobId, Ct))!;
        var generation = failed.Overview.Job.JobGeneration!.Value;
        (await h.TickWatermarkAsync(ws)).IndexedThroughGeneration.Should().BeLessThan(generation, "a Failed task holds the watermark back");
        failed = (await store.GetDetailAsync(ws, batch.JobId, Ct))!;
        failed.IndexedThroughGeneration.Should().BeLessThan(generation);
        failed.Overview.FailedIndexTasks.Should().Be(1);
        JobSearchability.Evaluate(failed.Overview.Job, failed.IndexedThroughGeneration).State.Should().Be(SearchabilityState.Pending);
        (await store.ListFailuresAsync(ws, batch.JobId, null, 10, Ct)).Should().ContainSingle()
            .Which.Error.Should().Contain("BulkItemsRejected");

        // While the task waits, PostgreSQL moves on: the replay must index the state as it is now, not as it was.
        var documents = await h.Import.DocumentsAsync(ws);
        var changed = documents["REPLAY-0003"].DocumentId;
        await h.Import.Db.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET document_version = document_version + 1 WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", ws), ("doc", changed));

        // Fix the cause, then replay from PostgreSQL (twice: the second finds nothing failed).
        h.Hook.OnBulk = null;
        var replay = await store.ReplayFailedAsync(ws, batch.JobId, OperationsActor.User(Guid.CreateVersion7()), Ct);
        replay.IndexTasksReplayed.Should().Be(1);
        replay.ChunksReplayed.Should().Be(0);
        replay.Status.Should().Be(JobStatus.Completed, "replaying index tasks never reopens the job's PostgreSQL phase");
        (await store.ReplayFailedAsync(ws, batch.JobId, OperationsActor.User(Guid.CreateVersion7()), Ct)).IndexTasksReplayed.Should().Be(0);

        var pending = (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!;
        pending.Status.Should().Be(IndexChunkTaskStatus.Pending, "the dispatcher publishes Pending tasks again");
        pending.AttemptCount.Should().Be(0);
        await h.DeliverAsync(pending);

        var applied = (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!;
        applied.Status.Should().Be(IndexChunkTaskStatus.Applied);
        applied.AttemptCount.Should().Be(1);
        (await h.CountAsync(ws)).Should().Be(12);
        (await h.DriftAsync(ws, [.. documents.Values.Select(d => d.DocumentId)])).Should().BeEmpty("the index holds current PostgreSQL state");
        (await h.IndexedAsync(ws, changed))!.Value.Version.Should().Be(documents["REPLAY-0003"].Version + 1);

        var current = (await store.GetDetailAsync(ws, batch.JobId, Ct))!;
        current.IndexedThroughGeneration.Should().BeLessThan(generation, "applied is not searchable until a refresh is observed");
        JobSearchability.Evaluate(current.Overview.Job, current.IndexedThroughGeneration).State.Should().Be(SearchabilityState.CatchingUp);
        (await h.TickWatermarkAsync(ws)).IndexedThroughGeneration.Should().BeGreaterThanOrEqualTo(generation);
        current = (await store.GetDetailAsync(ws, batch.JobId, Ct))!;
        current.IndexedThroughGeneration.Should().BeGreaterThanOrEqualTo(generation, "the watermark advances past the job's generation");
        current.Overview.FailedIndexTasks.Should().Be(0);
        JobSearchability.Evaluate(current.Overview.Job, current.IndexedThroughGeneration).Should().Be((1L, 1L, SearchabilityState.Current));
        (await h.Import.Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Job' AND action = 'Replayed'", ("ws", ws)))
            .Should().Be(1);
    }
}
