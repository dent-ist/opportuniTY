using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Data.Messaging;
using Opportunity.Hosting.Operations;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Coding;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Jobs;

/// <summary>
/// E06-T06 through the real API host with the real PDP: job list (filters, cursor, updatedSince polling) and detail with
/// the visibility rules (own jobs; Job.ViewAll for others', 403 otherwise, Q-59), cancel semantics (pending chunks
/// cancelled at once, running chunks at their next fence), PostgreSQL-driven replay (Job.Replay, audited, idempotent),
/// failed SearchOutbox rows, the job-events stream (a change arrives within 5 s, only visible jobs) and the operations CLI.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class JobOperationsApiTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_job_list_filters_pages_polls_and_shows_others_jobs_only_with_view_all()
    {
        await using var w = await World.CreateAsync(postgres);
        var adminJob = (await w.Db.CreateRunningJobAsync(1, workspaceId: w.Ws, initiatedBy: w.Admin)).JobId;
        var ownRunning = (await w.Db.CreateRunningJobAsync(2, workspaceId: w.Ws, initiatedBy: w.Reviewer)).JobId;
        var ownDone = (await w.Db.CreateRunningJobAsync(0, workspaceId: w.Ws, initiatedBy: w.Reviewer)).JobId;
        using var client = w.Factory.CreateClient();

        // A member without Job.ViewAll lists only their own jobs, even when asking for everyone's.
        foreach (var query in new[] { string.Empty, "?createdBy=all" })
        {
            var mine = await PageAsync(client, w.Reviewer, w.Url("/jobs" + query));
            Ids(mine).Should().BeEquivalentTo([ownRunning, ownDone]);
        }

        var all = await PageAsync(client, w.Auditor, w.Url("/jobs?createdBy=all"));
        Ids(all).Should().Equal([ownDone, ownRunning, adminJob], "newest first");
        var first = all.GetProperty("items")[1];
        first.GetProperty("type").GetString().Should().Be("bulkCoding");
        first.GetProperty("name").GetString().Should().Be("Mass Edit");
        first.GetProperty("status").GetString().Should().Be("running");
        first.GetProperty("createdBy").GetProperty("userId").GetGuid().Should().Be(w.Reviewer);
        first.GetProperty("createdBy").GetProperty("displayName").GetString().Should().Be("Rae Reviewer");
        first.GetProperty("committed").GetProperty("done").GetInt64().Should().Be(0);
        first.GetProperty("committed").GetProperty("total").GetInt64().Should().Be(2);
        first.GetProperty("searchable").GetProperty("state").GetString().Should().Be("pending");
        first.GetProperty("searchable").GetProperty("jobGeneration").ValueKind.Should().Be(JsonValueKind.Null, "no index task committed yet");
        first.GetProperty("searchable").GetProperty("indexedThroughGeneration").GetInt64().Should().Be(0);
        first.GetProperty("link").GetString().Should().Be($"jobs/{ownRunning}");
        all.GetProperty("items")[0].GetProperty("searchable").GetProperty("state").GetString().Should().Be("current");
        all.GetProperty("items")[0].GetProperty("completedAt").GetString().Should().EndWith("Z");
        Ids(await PageAsync(client, w.Auditor, w.Url("/jobs?createdBy=me"))).Should().BeEmpty();
        Ids(await PageAsync(client, w.Auditor, w.Url("/jobs?status=completed"))).Should().Equal([ownDone]);
        Ids(await PageAsync(client, w.Auditor, w.Url("/jobs?status=running,paused&type=bulkCoding"))).Should().Equal([ownRunning, adminJob]);
        Ids(await PageAsync(client, w.Auditor, w.Url("/jobs?type=import"))).Should().BeEmpty();
        using (var invalid = await SendAsync(client, HttpMethod.Get, w.Url("/jobs?type=nonsense"), w.Auditor))
        {
            await invalid.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        }

        // Keyset pages; a cursor is bound to its user.
        var page1 = await PageAsync(client, w.Auditor, w.Url("/jobs?limit=2"));
        Ids(page1).Should().Equal([ownDone, ownRunning]);
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        Ids(await PageAsync(client, w.Auditor, w.Url($"/jobs?limit=2&cursor={cursor}"))).Should().Equal([adminJob]);
        using (var stolen = await SendAsync(client, HttpMethod.Get, w.Url($"/jobs?limit=2&cursor={cursor}"), w.Admin))
        {
            stolen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // Polling fallback: only what changed after the instant, in change order.
        var since = (await PageAsync(client, w.Auditor, w.Url("/jobs"))).GetProperty("items").EnumerateArray()
            .Max(i => i.GetProperty("updatedAt").GetDateTimeOffset()).AddMilliseconds(1); // timestamps are truncated to ms
        await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        await w.Db.Jobs.CancelAsync(w.Ws, adminJob, w.Admin, cancellationToken: Ct);
        var changed = await PageAsync(client, w.Auditor, w.Url($"/jobs?updatedSince={Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))}"));
        Ids(changed).Should().Equal([adminJob]);
        changed.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("cancelled");
        Ids(await PageAsync(client, w.Reviewer, w.Url($"/jobs?updatedSince={Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))}")))
            .Should().BeEmpty("another user's change is not listed without Job.ViewAll");

        // Detail: own job; another user's job is 403 without Job.ViewAll (Q-59) and visible with it.
        var detail = await JsonAsync(client, HttpMethod.Get, w.Url($"/jobs/{ownRunning}"), w.Reviewer, HttpStatusCode.OK);
        detail.GetProperty("chunks").GetProperty("pending").GetInt64().Should().Be(2);
        detail.GetProperty("attempts").GetInt64().Should().Be(0);
        detail.GetProperty("jobType").GetString().Should().Be("bulkCoding", "the first-slice fields stay");
        detail.GetProperty("indexed").GetProperty("state").GetString().Should().Be("indexing");
        using (var forbidden = await SendAsync(client, HttpMethod.Get, w.Url($"/jobs/{adminJob}"), w.Reviewer))
        {
            await forbidden.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var failures = await SendAsync(client, HttpMethod.Get, w.Url($"/jobs/{adminJob}/failures"), w.Reviewer))
        {
            failures.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        await JsonAsync(client, HttpMethod.Get, w.Url($"/jobs/{adminJob}"), w.Auditor, HttpStatusCode.OK);
        using (var unknown = await SendAsync(client, HttpMethod.Get, w.Url($"/jobs/{Guid.CreateVersion7()}"), w.Auditor))
        {
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Cancel_cancels_pending_chunks_at_once_and_a_running_chunk_at_its_next_fence()
    {
        await using var w = await World.CreateAsync(postgres);
        var (_, jobId) = await w.Db.CreateRunningJobAsync(3, workspaceId: w.Ws, initiatedBy: w.Reviewer);
        var running = (await w.Db.Chunks.ClaimNextAsync(w.Ws, jobId, "worker-1", JobDatabase.Lease, Ct)).Chunk!;
        using var client = w.Factory.CreateClient();

        using (var other = await SendAsync(client, HttpMethod.Post, w.Url($"/jobs/{jobId}/cancel"), w.Reviewer2))
        {
            await other.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        var cancelling = await JsonAsync(client, HttpMethod.Post, w.Url($"/jobs/{jobId}/cancel"), w.Reviewer, HttpStatusCode.Accepted);
        cancelling.GetProperty("status").GetString().Should().Be("cancelling");
        cancelling.GetProperty("chunks").GetProperty("cancelled").GetInt64().Should().Be(2, "pending chunks are cancelled at once");
        cancelling.GetProperty("chunks").GetProperty("running").GetInt64().Should().Be(1, "the running chunk finishes or aborts at its fence");
        (await w.Db.ChunksAsync(w.Ws, jobId)).Select(c => c.Status).Should().BeEquivalentTo(
            [JobChunkStatus.Running, JobChunkStatus.Cancelled, JobChunkStatus.Cancelled]);
        (await JsonAsync(client, HttpMethod.Post, w.Url($"/jobs/{jobId}/cancel"), w.Reviewer, HttpStatusCode.Accepted))
            .GetProperty("status").GetString().Should().Be("cancelling", "a repeated cancel is harmless");

        // Fence F2 (heartbeat) tells the worker to stop; fence F3 refuses its commit, so the chunk never happened.
        (await w.Db.Chunks.HeartbeatAsync(running.Lease, JobDatabase.Lease, Ct)).Fence.Should().Be(ChunkFence.JobCancelling);
        (await w.Db.Chunks.CompleteAsync(running.Lease, new ChunkCompletion { ItemsApplied = 100 }, Ct)).Outcome
            .Should().Be(ChunkCommitOutcome.Cancelled);
        var cancelled = await JsonAsync(client, HttpMethod.Get, w.Url($"/jobs/{jobId}"), w.Reviewer, HttpStatusCode.OK);
        cancelled.GetProperty("status").GetString().Should().Be("cancelled");
        cancelled.GetProperty("committed").GetProperty("itemsApplied").GetInt64().Should().Be(0);
        cancelled.GetProperty("chunks").GetProperty("cancelled").GetInt64().Should().Be(3);

        using (var finished = await SendAsync(client, HttpMethod.Post, w.Url($"/jobs/{(await w.Db.CreateRunningJobAsync(0, workspaceId: w.Ws, initiatedBy: w.Reviewer)).JobId}/cancel"), w.Reviewer))
        {
            await finished.ShouldBeProblemAsync(HttpStatusCode.Conflict, "conflict");
        }

        // Another user's job: Job.Manage (Workspace Admin).
        var (_, adminCancels) = await w.Db.CreateRunningJobAsync(1, workspaceId: w.Ws, initiatedBy: w.Reviewer2);
        (await JsonAsync(client, HttpMethod.Post, w.Url($"/jobs/{adminCancels}/cancel"), w.Admin, HttpStatusCode.Accepted))
            .GetProperty("status").GetString().Should().Be("cancelled", "nothing was running");
        (await w.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Job' AND action = 'Cancelled' AND actor_id = @actor",
            ("ws", w.Ws), ("actor", w.Admin.ToString()))).Should().Be(1);
    }

    [Fact]
    public async Task Retry_failed_replays_chunks_and_index_tasks_from_postgres_once_audited_and_only_with_job_replay()
    {
        await using var w = await World.CreateAsync(postgres);
        var (_, jobId) = await w.Db.CreateRunningJobAsync(2, workspaceId: w.Ws, initiatedBy: w.Reviewer);
        var failing = (await w.Db.Chunks.ClaimNextAsync(w.Ws, jobId, "worker-1", JobDatabase.Lease, Ct)).Chunk!;
        await w.Db.Chunks.FailAsync(failing.Lease, ChunkError.Permanent("FieldMissing", "Field 1005 was deleted."), Ct);
        var committing = (await w.Db.Chunks.ClaimNextAsync(w.Ws, jobId, "worker-1", JobDatabase.Lease, Ct)).Chunk!;
        await w.Db.Chunks.CompleteAsync(committing.Lease, new ChunkCompletion { ItemsApplied = 100, IndexTasks = 1 }, Ct);
        var task = await w.InsertFailedIndexTaskAsync(jobId, committing.Lease.ChunkId);
        (await w.Db.JobAsync(w.Ws, jobId)).Status.Should().Be(JobStatus.CompletedWithErrors);
        // The recorded broker copy of the failed chunk's message (ADR-010 §7.3) is listed while the chunk is failed.
        (await new DeadLetterStore(w.Db.Core.AppDataSource).RecordAsync(new DeadLetterMessage(
            "dl-1", "bulkcoding.chunks", "bulkcoding.dlx", "bulkcoding.chunks", w.Ws, jobId, failing.Lease.ChunkId, "jobs.chunk", "corr-1",
            "delivery_limit", 5, null, null, null, "{}", "{}"u8.ToArray(), 2), Ct)).Should().Be(DeadLetterWriteOutcome.Workspace);
        using var client = w.Factory.CreateClient();

        var failures = await PageAsync(client, w.Reviewer, w.Url($"/jobs/{jobId}/failures"));
        failures.GetProperty("items").EnumerateArray().Select(f => (f.GetProperty("kind").GetString(), f.GetProperty("id").GetString()))
            .Should().BeEquivalentTo([("chunk", failing.Lease.ChunkId.ToString()), ("indexTask", task.ToString()), ("deadLetter", "dl-1")]);
        failures.GetProperty("items").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "deadLetter")
            .GetProperty("attempts").GetInt64().Should().Be(5);
        failures.GetProperty("items").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "chunk")
            .GetProperty("error").GetString().Should().Be("FieldMissing: Field 1005 was deleted.");
        var before = await JsonAsync(client, HttpMethod.Get, w.Url($"/jobs/{jobId}"), w.Reviewer, HttpStatusCode.OK);
        before.GetProperty("errorCount").GetInt64().Should().Be(2);
        before.GetProperty("chunks").GetProperty("failed").GetInt64().Should().Be(1);
        before.GetProperty("lastError").GetString().Should().NotBeNullOrEmpty();

        // The job's own initiator may not replay: Job.Replay (Workspace Admin by default).
        using (var denied = await SendAsync(client, HttpMethod.Post, w.Url($"/jobs/{jobId}/retry-failed"), w.Reviewer))
        {
            await denied.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        var replay = await JsonAsync(client, HttpMethod.Post, w.Url($"/jobs/{jobId}/retry-failed"), w.Admin, HttpStatusCode.Accepted);
        replay.GetProperty("chunksReplayed").GetInt32().Should().Be(1);
        replay.GetProperty("indexTasksReplayed").GetInt32().Should().Be(1);
        replay.GetProperty("job").GetProperty("status").GetString().Should().Be("running", "CompletedWithErrors reopens");
        var chunk = (await w.Db.ChunksAsync(w.Ws, jobId)).Single(c => c.ChunkId == failing.Lease.ChunkId);
        chunk.Status.Should().Be(JobChunkStatus.Pending);
        chunk.AttemptCount.Should().Be(0);
        chunk.ReplayCount.Should().Be(1);
        (await w.Db.Core.ScalarAsync<short>("SELECT status FROM opportunity.index_chunk_task WHERE task_id = @t", ("t", task))).Should().Be(1);
        (await w.Db.Core.ScalarAsync<int>("SELECT attempt_count FROM opportunity.index_chunk_task WHERE task_id = @t", ("t", task))).Should().Be(0);
        (await PageAsync(client, w.Reviewer, w.Url($"/jobs/{jobId}/failures"))).GetProperty("items").GetArrayLength().Should().Be(0);

        // Replaying the same failures again changes nothing and writes no second audit event.
        var again = await JsonAsync(client, HttpMethod.Post, w.Url($"/jobs/{jobId}/retry-failed"), w.Admin, HttpStatusCode.Accepted);
        again.GetProperty("chunksReplayed").GetInt32().Should().Be(0);
        again.GetProperty("indexTasksReplayed").GetInt32().Should().Be(0);
        (await w.Db.ChunksAsync(w.Ws, jobId)).Single(c => c.ChunkId == failing.Lease.ChunkId).ReplayCount.Should().Be(1);
        (await w.Db.Core.ScalarAsync<long>(
            """
            SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Job' AND action = 'Replayed'
               AND actor_id = @actor AND details ->> 'ChunksReplayed' = '1' AND details ->> 'IndexTasksReplayed' = '1'
            """, ("ws", w.Ws), ("actor", w.Admin.ToString()))).Should().Be(1);
        (await w.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Job' AND action = 'Replayed'", ("ws", w.Ws)))
            .Should().Be(1);

        // The replayed chunk runs again and the job completes.
        var rerun = (await w.Db.Chunks.ClaimNextAsync(w.Ws, jobId, "worker-2", JobDatabase.Lease, Ct)).Chunk!;
        await w.Db.Chunks.CompleteAsync(rerun.Lease, ChunkCompletion.Empty, Ct);
        (await w.Db.JobAsync(w.Ws, jobId)).Status.Should().Be(JobStatus.Completed);

        // Failed SearchOutbox rows (interactive edits): listed with Job.ViewAll, replayed with Job.Replay, once.
        await w.InsertFailedOutboxRowAsync();
        using (var reviewerList = await SendAsync(client, HttpMethod.Get, w.Url("/search-outbox/failures"), w.Reviewer))
        {
            reviewerList.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        var outbox = await PageAsync(client, w.Auditor, w.Url("/search-outbox/failures"));
        outbox.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).Should().Equal(["outbox"]);
        using (var auditorReplay = await SendAsync(client, HttpMethod.Post, w.Url("/search-outbox/retry-failed"), w.Auditor))
        {
            auditorReplay.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await JsonAsync(client, HttpMethod.Post, w.Url("/search-outbox/retry-failed"), w.Admin, HttpStatusCode.Accepted))
            .GetProperty("rowsReplayed").GetInt32().Should().Be(1);
        (await JsonAsync(client, HttpMethod.Post, w.Url("/search-outbox/retry-failed"), w.Admin, HttpStatusCode.Accepted))
            .GetProperty("rowsReplayed").GetInt32().Should().Be(0);
        (await w.Db.Core.ScalarAsync<short>("SELECT status FROM opportunity.search_outbox WHERE workspace_id = @ws", ("ws", w.Ws))).Should().Be(1);
        (await w.Db.Core.ScalarAsync<long>(
            """
            SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND category = 'Job' AND action = 'Replayed'
               AND resource_type = 'SearchOutbox' AND actor_id = @actor AND details ->> 'RowsReplayed' = '1'
            """, ("ws", w.Ws), ("actor", w.Admin.ToString()))).Should().Be(1, "the replay is audited (E14-T02); the empty one changes nothing");
    }

    [Fact]
    public async Task Job_events_deliver_a_status_change_within_5_seconds_and_only_jobs_the_caller_may_see()
    {
        await using var w = await World.CreateAsync(postgres);
        var (_, own) = await w.Db.CreateRunningJobAsync(1, workspaceId: w.Ws, initiatedBy: w.Reviewer);
        var (_, others) = await w.Db.CreateRunningJobAsync(1, workspaceId: w.Ws, initiatedBy: w.Admin);
        using var client = w.Factory.CreateClient();

        await using var reviewer = await EventStream.OpenAsync(client, w.Url("/job-events"), w.Reviewer);
        await using var auditor = await EventStream.OpenAsync(client, w.Url("/job-events"), w.Auditor);
        reviewer.ContentType.Should().Be("text/event-stream");

        var clock = Stopwatch.StartNew();
        await w.Db.Jobs.CancelAsync(w.Ws, others, w.Admin, cancellationToken: Ct);
        await w.Db.Jobs.CancelAsync(w.Ws, own, w.Reviewer, cancellationToken: Ct);

        var ownEvent = await reviewer.NextAsync(e => e.JobId == own && e.Status == "cancelled", TimeSpan.FromSeconds(5));
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        ownEvent.Data.GetProperty("committed").GetProperty("total").GetInt64().Should().Be(1);
        ownEvent.Data.GetProperty("searchable").GetProperty("state").GetString().Should().Be("current");
        ownEvent.Id.Should().EndWith("Z");
        (await auditor.NextAsync(e => e.JobId == others && e.Status == "cancelled", TimeSpan.FromSeconds(5))).Should().NotBeNull("Job.ViewAll sees every job");
        reviewer.Seen.Should().NotContain(e => e.JobId == others, "another user's job is not streamed without Job.ViewAll");

        // A non-member gets the PEP-1 404 instead of a stream.
        using var request = new HttpRequestMessage(HttpMethod.Get, w.Url("/job-events"));
        request.Headers.Add(TestAuthentication.UserHeader, w.Outsider.ToString());
        using var outsider = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        outsider.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_operations_cli_lists_and_replays_as_the_named_operator()
    {
        await using var w = await World.CreateAsync(postgres);
        var (_, jobId) = await w.Db.CreateRunningJobAsync(1, workspaceId: w.Ws, initiatedBy: w.Reviewer);
        var chunk = (await w.Db.Chunks.ClaimNextAsync(w.Ws, jobId, "worker-1", JobDatabase.Lease, Ct)).Chunk!;
        await w.Db.Chunks.FailAsync(chunk.Lease, ChunkError.Permanent("Boom", "Simulated."), Ct);

        var (code, output, _) = await CliAsync(w, "list", "--workspace", w.Ws.ToString(), "--status", "completedWithErrors");
        code.Should().Be(JobOperationsCli.ExitSuccess);
        output.Should().Contain(jobId.ToString()).And.Contain("CompletedWithErrors");
        (code, output, _) = await CliAsync(w, "failures", "--workspace", w.Ws.ToString(), "--job", jobId.ToString());
        output.Should().Contain(chunk.Lease.ChunkId.ToString()).And.Contain("Boom: Simulated.");
        (code, _, var error) = await CliAsync(w, "replay", "--workspace", w.Ws.ToString(), "--job", jobId.ToString());
        code.Should().Be(JobOperationsCli.ExitUsage, error);

        (code, output, _) = await CliAsync(w, "replay", "--workspace", w.Ws.ToString(), "--job", jobId.ToString(), "--operator", "ops-oncall");
        code.Should().Be(JobOperationsCli.ExitSuccess);
        output.Should().Contain("Replayed 1 chunk(s) and 0 index task(s); job status Running.");
        (await CliAsync(w, "replay", "--workspace", w.Ws.ToString(), "--job", jobId.ToString(), "--operator", "ops-oncall")).Output
            .Should().Contain("Replayed 0 chunk(s)");
        (await w.Db.Core.ScalarAsync<string>(
            "SELECT actor_display FROM audit.audit_event WHERE workspace_id = @ws AND action = 'Replayed' AND actor_id = 'service:ops-cli'",
            ("ws", w.Ws))).Should().Be("Operations CLI (ops-oncall)");
        (await CliAsync(w, "show", "--workspace", w.Ws.ToString(), "--job", Guid.CreateVersion7().ToString())).Code.Should().Be(JobOperationsCli.ExitNotFound);
        (await CliAsync(w, "backlog", "--workspace", w.Ws.ToString())).Output.Should().Contain("index tasks");
        (await CliAsync(w, "redispatch", "--workspace", w.Ws.ToString())).Output.Should().StartWith("Returned to Pending:");
    }

    [Fact]
    public async Task A_search_reindex_starts_from_the_cli_or_with_job_manage_and_one_runs_at_a_time()
    {
        await using var w = await World.CreateAsync(postgres);
        using var client = w.Factory.CreateClient();

        var (code, output, error) = await CliAsync(w, "reindex", "--workspace", w.Ws.ToString(), "--placement", "dedicated");
        code.Should().Be(JobOperationsCli.ExitUsage, "an operator must be named");
        (code, output, error) = await CliAsync(w, "reindex", "--workspace", w.Ws.ToString(), "--operator", "ops-oncall", "--placement", "dedicated");
        code.Should().Be(JobOperationsCli.ExitSuccess, error);
        output.Should().StartWith("Started reindex job");
        (await w.Db.Core.ScalarAsync<string>(
            "SELECT actor_display FROM audit.audit_event WHERE workspace_id = @ws AND action = 'Created' AND actor_id = 'service:ops-cli'",
            ("ws", w.Ws))).Should().Be("Operations CLI (ops-oncall)");

        (await StartReindexAsync(client, w, w.Reviewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "starting needs Job.Manage");
        using (var unplaced = await StartReindexAsync(client, w, w.Admin))
        {
            unplaced.StatusCode.Should().Be(HttpStatusCode.BadRequest, "nothing is indexed yet");
        }

        // A placement record as index management keeps it (the API reads facts only; no index is touched here).
        await w.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.workspace_index_placement (workspace_id, kind, generation) VALUES (@ws, 2, 2)", ("ws", w.Ws));
        using (var conflict = await StartReindexAsync(client, w, w.Admin))
        {
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict, await conflict.Content.ReadAsStringAsync(Ct));
        }

        (code, _, _) = await CliAsync(w, "reindex", "--workspace", w.Ws.ToString(), "--operator", "ops-oncall");
        code.Should().Be(JobOperationsCli.ExitConflict);

        var status = await JsonAsync(client, HttpMethod.Get, w.Url("/search-index"), w.Auditor, HttpStatusCode.OK);
        var run = status.GetProperty("reindexes").EnumerateArray().Single();
        run.GetProperty("phase").GetString().Should().Be("pending");
        var jobId = run.GetProperty("jobId").GetGuid();
        (await SendAsync(client, HttpMethod.Get, w.Url("/search-index"), w.Reviewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var job = await JsonAsync(client, HttpMethod.Get, w.Url($"/jobs/{jobId}"), w.Admin, HttpStatusCode.OK);
        job.GetProperty("jobType").GetString().Should().Be("reindex");
        job.GetProperty("status").GetString().Should().Be("preparing");

        (code, output, _) = await CliAsync(w, "reindex-status", "--workspace", w.Ws.ToString());
        code.Should().Be(JobOperationsCli.ExitSuccess);
        output.Should().Contain(jobId.ToString()).And.Contain("Pending");

        // Once the run is no longer in flight (here: aborted before it began), a new reindex may start; a retry with the
        // same Idempotency-Key returns the same job.
        await w.Db.Core.ExecuteAsync("UPDATE opportunity.search_reindex SET phase = 10 WHERE workspace_id = @ws", ("ws", w.Ws));
        using var started = await StartReindexAsync(client, w, w.Admin, "same-key");
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
        using var retried = await StartReindexAsync(client, w, w.Admin, "same-key");
        retried.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await retried.Content.ReadAsStringAsync(Ct)).Should().Be(await started.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> StartReindexAsync(HttpClient client, World w, Guid user, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(w.Url("/search-index/reindexes"), UriKind.Relative))
        {
            Content = new StringContent("""{"placement":"dedicated"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request, Ct);
    }

    private static async Task<(int Code, string Output, string Error)> CliAsync(World w, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await JobOperationsCli.RunAsync(args, output, error, w.Db.Core.AppConnectionString, Ct);
        return (code, output.ToString(), error.ToString());
    }

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("jobId").GetGuid())];

    private static Task<JsonElement> PageAsync(HttpClient client, Guid user, string url) => JsonAsync(client, HttpMethod.Get, url, user, HttpStatusCode.OK);

    private static async Task<JsonElement> JsonAsync(HttpClient client, HttpMethod method, string url, Guid user, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, method, url, user);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, Guid user)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
        return await client.SendAsync(request, Ct);
    }

    /// <summary>A workspace with an admin, two reviewers, an auditor (Job.ViewAll) and a non-member, and the API on it.</summary>
    private sealed class World : IAsyncDisposable
    {
        private World(JobDatabase db, Guid ws)
        {
            Db = db;
            Ws = ws;
            Factory = CodingApiHarness.Factory(db.Core.AppConnectionString, b => b.UseSetting("Jobs:Events:PollInterval", "00:00:00.250"));
        }

        public JobDatabase Db { get; }

        public Guid Ws { get; }

        public WebApplicationFactory<Program> Factory { get; }

        public Guid Admin { get; private set; }

        public Guid Reviewer { get; private set; }

        public Guid Reviewer2 { get; private set; }

        public Guid Auditor { get; private set; }

        public Guid Outsider { get; private set; }

        public static async Task<World> CreateAsync(MigrationPostgresFixture postgres)
        {
            var db = await JobDatabase.CreateAsync(postgres);
            var w = new World(db, await db.Core.CreateWorkspaceAsync());
            w.Admin = await w.UserAsync("Ada Admin", WorkspaceRole.WorkspaceAdmin);
            w.Reviewer = await w.UserAsync("Rae Reviewer", WorkspaceRole.Reviewer);
            w.Reviewer2 = await w.UserAsync("Rob Reviewer", WorkspaceRole.Reviewer);
            w.Auditor = await w.UserAsync("Abe Auditor", WorkspaceRole.Auditor);
            w.Outsider = await w.UserAsync("Oz Outsider", null);
            return w;
        }

        public string Url(string suffix) => $"/api/v1/workspaces/{Ws}{suffix}";

        public async Task<Guid> InsertFailedIndexTaskAsync(Guid jobId, Guid chunkId)
        {
            var task = Guid.CreateVersion7();
            await Db.Core.ExecuteAsync("SELECT opportunity.search_work_ensure_partitions(now() + interval '1 day')");
            await Db.Core.ExecuteAsync(
                """
                INSERT INTO opportunity.index_chunk_task (workspace_id, created_at, task_id, job_id, chunk_id, task_kind, membership_kind,
                    snapshot_id, range_from, range_to, change_mask, lane, search_generation, committed_at, status, idempotency_key,
                    attempt_count, max_attempts, last_error, error_class)
                VALUES (@ws, opportunity.uuid_v7_timestamp(@task), @task, @job, @chunk, 2, 1, @snapshot, 101, 200, 4, 4, 1, now(), 6,
                        encode(sha256(convert_to(@task::text, 'UTF8')), 'hex'), 5, 5, 'BulkItemsRejected: simulated', 2)
                """,
                ("ws", Ws), ("task", task), ("job", jobId), ("chunk", chunkId), ("snapshot", Guid.CreateVersion7()));
            return task;
        }

        public async Task InsertFailedOutboxRowAsync()
        {
            await Db.Core.ExecuteAsync("SELECT opportunity.search_work_ensure_partitions(now() + interval '1 day')");
            await Db.Core.ExecuteAsync(
                """
                INSERT INTO opportunity.search_outbox (workspace_id, outbox_id, document_id, document_version, change_mask, lane,
                    search_generation, committed_at, status, attempt_count, last_error)
                VALUES (@ws, 1, @doc, 2, 4, 2, 1, now(), 5, 10, 'mapper_parsing_exception: simulated')
                """,
                ("ws", Ws), ("doc", Guid.CreateVersion7()));
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }

        private async Task<Guid> UserAsync(string name, WorkspaceRole? role)
        {
            var id = Guid.CreateVersion7();
            await Db.Core.ExecuteAsync(
                """
                INSERT INTO opportunity.app_user (user_id, issuer, subject, display_name, groups, groups_refreshed_at, last_sign_in_at)
                VALUES (@id, 'https://idp.test', @id::text, @name, '{}', now(), now())
                """,
                ("id", id), ("name", name));
            if (role is { } r)
            {
                await Db.Core.ExecuteAsync(
                    "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @id, @role, @user)",
                    ("ws", Ws), ("id", Guid.CreateVersion7()), ("role", r.Key()), ("user", id));
            }

            return id;
        }
    }

    internal sealed record SseEvent(string? Id, Guid JobId, string Status, JsonElement Data);

    /// <summary>A job-events stream read line by line, as EventSource would.</summary>
    private sealed class EventStream : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private EventStream(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public string? ContentType => _response.Content.Headers.ContentType?.MediaType;

        public List<SseEvent> Seen { get; } = [];

        public static async Task<EventStream> OpenAsync(HttpClient client, string url, Guid user)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
            request.Headers.Add(TestAuthentication.UserHeader, user.ToString());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
            var stream = new EventStream(response, reader);
            (await reader.ReadLineAsync(Ct)).Should().Be("retry: 5000");
            (await reader.ReadLineAsync(Ct)).Should().Be(": connected", "the stream is open before the test changes anything");
            return stream;
        }

        public async Task<SseEvent> NextAsync(Func<SseEvent, bool> match, TimeSpan timeout)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(timeout);
            string? id = null, type = null, data = null;
            while (true)
            {
                var line = await _reader.ReadLineAsync(limit.Token) ?? throw new InvalidOperationException("The stream ended.");
                if (line.Length == 0)
                {
                    if (type == "job" && data is not null)
                    {
                        var json = JsonDocument.Parse(data).RootElement.Clone();
                        var e = new SseEvent(id, json.GetProperty("jobId").GetGuid(), json.GetProperty("status").GetString()!, json);
                        Seen.Add(e);
                        if (match(e))
                        {
                            return e;
                        }
                    }

                    id = type = data = null;
                }
                else if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line[4..];
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    type = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
