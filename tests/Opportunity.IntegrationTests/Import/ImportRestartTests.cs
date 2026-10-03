using AwesomeAssertions;

using Opportunity.Application.Jobs;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Jobs.Faults;
#endif

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T03 restartability: an import survives a worker dying at every point of a chunk — before the claim is used,
/// in the middle of the chunk's PostgreSQL transaction, after the commit but before the ack — plus a storage outage
/// mid-read and duplicate deliveries, and ends with exactly the documents and index tasks of a clean run.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportRestartTests(MigrationPostgresFixture postgres)
{
    private const int Rows = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Header = ["BEGDOC", "CUSTODIAN"];

    private static byte[] Dat() =>
        ImportHarness.Utf8Bom(ImportHarness.Dat([Header, .. Enumerable.Range(1, Rows).Select(i => new[] { $"R-{i:D3}", "Custodian " + i })]));

#if OPPORTUNITY_FAILPOINTS
    [Fact]
    public async Task Crashes_mid_import_resume_to_the_state_of_a_clean_run_with_no_duplicate_documents()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 2);
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, Dat());
        await h.PrepareAsync(batch);
        var chunks = await h.Jobs.GetChunksAsync(ws, batch.JobId, cancellationToken: Ct);
        chunks.Should().HaveCount(5);

        // A database fault inside chunk 5's transaction, after its documents were inserted (serialization failure:
        // transient, so the chunk is retried). The trigger only fires while the flag row exists.
        await h.Db.ExecuteAsync(
            """
            CREATE TABLE public.import_crash_flag (armed boolean);
            CREATE FUNCTION public.import_crash() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
            BEGIN
                IF NEW.row_no = 9 AND EXISTS (SELECT FROM public.import_crash_flag) THEN
                    RAISE EXCEPTION 'simulated crash inside the chunk transaction' USING ERRCODE = '40001';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER import_crash BEFORE INSERT ON opportunity.import_batch_member FOR EACH ROW EXECUTE FUNCTION public.import_crash();
            INSERT INTO public.import_crash_flag VALUES (true);
            """);

        var faults = new CrashOnce(new Dictionary<int, string> { [2] = Failpoints.AfterCommit, [3] = Failpoints.AfterClaim });
        var consumer = h.Consumer(faults);
        var flaky = new FlakyObjectStore(h.Store);
        h.Store = flaky;
        var crashingConsumer = h.Consumer(faults);

        foreach (var chunk in chunks)
        {
            if (chunk.Sequence == 4)
            {
                flaky.FailReads = 1; // the storage read of chunk 4 breaks after a few bytes
                flaky.FailAfterBytes = 5;
            }

            try
            {
                await crashingConsumer.HandleAsync(ImportHarness.Payload(chunk), ImportHarness.Received(chunk), Ct);
            }
            catch (SimulatedCrashException)
            {
                // The worker died: no ack, no outcome recorded.
            }
        }

        var states = (await h.Jobs.GetChunksAsync(ws, batch.JobId, cancellationToken: Ct)).ToDictionary(c => c.Sequence);
        states[1].Status.Should().Be(JobChunkStatus.Committed);
        states[2].Status.Should().Be(JobChunkStatus.Committed, "the commit happened; only the ack was lost");
        states[3].Status.Should().Be(JobChunkStatus.Running, "the worker died holding the lease");
        states[4].Status.Should().Be(JobChunkStatus.RetryWait);
        states[5].Status.Should().Be(JobChunkStatus.RetryWait);
        states[5].ErrorCode.Should().Be("PostgresException");
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws))
            .Should().Be(4, "only chunks 1 and 2 committed; chunk 5's inserted documents rolled back with its transaction");

        // Recovery: the fault is gone, the lease sweeper reclaims chunk 3, retry waits elapse, and the broker redelivers
        // every message, including the unacked one of chunk 2 (twice, for good measure).
        await h.Db.ExecuteAsync("DELETE FROM public.import_crash_flag");
        await h.Db.ExecuteAsync(
            "UPDATE opportunity.job_chunk SET lease_expires_at = CASE WHEN status = 3 THEN now() - interval '1 minute' END, available_at = now() - interval '1 minute' WHERE workspace_id = @ws",
            ("ws", ws));
        (await h.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).ReturnedToPending.Should().Be(1);
        foreach (var chunk in chunks.Concat(chunks.Where(c => c.Sequence == 2)))
        {
            await consumer.HandleAsync(ImportHarness.Payload(chunk), ImportHarness.Received(chunk, redelivered: true), Ct);
        }

        await AssertCleanRunStateAsync(h, batch);

        // Late duplicate deliveries of every chunk change nothing.
        foreach (var chunk in chunks)
        {
            await consumer.HandleAsync(ImportHarness.Payload(chunk), ImportHarness.Received(chunk, redelivered: true), Ct);
        }

        await AssertCleanRunStateAsync(h, batch);
    }

    private sealed class CrashOnce(Dictionary<int, string> plan) : IFaultInjector
    {
        public ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken)
        {
            var sequence = ((JobChunkMessage)context.Message.Payload).Sequence;
            if (plan.TryGetValue(sequence, out var at) && at == failpoint)
            {
                plan.Remove(sequence);
                throw new SimulatedCrashException($"Crash at {failpoint} of chunk {sequence}.");
            }

            return ValueTask.CompletedTask;
        }
    }
#endif

    [Fact]
    public async Task A_chunk_whose_job_was_paused_writes_nothing_and_runs_after_resume()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 5);
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, Dat());
        await h.PrepareAsync(batch);
        var chunks = await h.Jobs.GetChunksAsync(ws, batch.JobId, cancellationToken: Ct);

        // Claimed while running; the job is paused before the chunk commits: fence F3 refuses and rolls everything back.
        var claim = await h.Chunks.ClaimAsync(ws, chunks[0].ChunkId, "worker-a", TimeSpan.FromMinutes(1), Ct);
        claim.Claimed.Should().BeTrue();
        (await h.Jobs.PauseAsync(ws, batch.JobId, "operator", Ct)).Applied.Should().BeTrue();
        var context = new ChunkExecutionContext(claim.Chunk!, ImportHarness.Received(chunks[0]), _ => Task.FromResult(ChunkFence.Proceed));
        var result = await h.Executor().ExecuteAsync(context, Ct);
        result.CommitResult!.Outcome.Should().Be(ChunkCommitOutcome.JobNotRunning);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(0);
        (await h.CountAsync("SELECT count(*) FROM opportunity.index_chunk_task WHERE workspace_id = @ws", ws)).Should().Be(0);
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_row_issue WHERE workspace_id = @ws", ws)).Should().Be(0);

        (await h.Jobs.ResumeAsync(ws, batch.JobId, Ct)).Applied.Should().BeTrue();
        await h.DeliverOpenChunksAsync(batch);
        await AssertCleanRunStateAsync(h, batch);
    }

    private static async Task AssertCleanRunStateAsync(ImportHarness h, Application.Import.ImportBatchRecord batch)
    {
        var ws = batch.WorkspaceId;
        var job = (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!;
        job.Status.Should().Be(JobStatus.Completed);
        job.Counters.ItemsApplied.Should().Be(Rows);
        job.Counters.IndexTasksTotal.Should().Be(job.Counters.ChunksTotal);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(Rows);
        (await h.CountAsync("SELECT count(DISTINCT control_number_norm) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(Rows);
        (await h.CountAsync("SELECT count(*) FROM opportunity.import_batch_member WHERE workspace_id = @ws", ws)).Should().Be(Rows);
        (await h.IndexTasksPerChunkAsync(batch)).Should().HaveCount((int)job.Counters.ChunksTotal).And.OnlyContain(t => t.Value == 1);
        (await h.CountAsync("SELECT count(*) FROM opportunity.search_outbox WHERE workspace_id = @ws", ws)).Should().Be(0);
        var report = await h.BatchAsync(batch);
        (report.RowsImported, report.RowsErrored).Should().Be((Rows, 0L));
        (await h.Db.ColumnAsync($"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' ORDER BY recorded_at"))
            .Should().Equal("Started", "Completed");
    }
}
