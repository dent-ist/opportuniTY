using AwesomeAssertions;

using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// E06-T04: the dispatcher's publish claims on <c>job_chunk</c> (V0013) under RLS — ADR-010 §6 concurrency limits, index
/// backpressure and the security throttle, RetryWait and lost-message re-dispatch, and which jobs are dispatched at all.
/// </summary>
public sealed class JobChunkDispatchTests(MigrationPostgresFixture postgres) : IClassFixture<MigrationPostgresFixture>
{
    private static readonly TimeSpan Claim = TimeSpan.FromSeconds(30);
    private static readonly JobChunkDispatchLimits Limits = new() { Operations = JobChunkRelay.DispatchedOperations };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Claims_follow_sequence_within_the_per_job_and_per_workspace_limits_and_skip_rows_another_dispatcher_holds()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var first = await db.RunningJobAsync(ws, chunks: 10);
        var second = await db.RunningJobAsync(ws, chunks: 10);
        var third = await db.RunningJobAsync(ws, chunks: 10);

        var a = await db.Chunks.ClaimForDispatchAsync(ws, "dispatcher-a", Limits, Claim, Ct);
        var b = await db.Chunks.ClaimForDispatchAsync(ws, "dispatcher-b", Limits, Claim, Ct);

        a.Should().HaveCount(8, "ADR-010 §6: at most 8 chunks of a workspace in flight");
        a.GroupBy(c => c.JobId).Should().OnlyContain(g => g.Count() <= 4, "at most 4 chunks of a job");
        a.GroupBy(c => c.JobId).Should().HaveCount(3, "jobs take turns");
        a.GroupBy(c => c.JobId).Should().OnlyContain(g => g.Select(c => c.Sequence).SequenceEqual(Enumerable.Range(1, g.Count())));
        b.Should().BeEmpty("held claims count as in flight; nothing is substituted for them");

        (await db.Chunks.MarkDispatchedAsync(ws, "dispatcher-b", [.. a.Select(c => c.ChunkId)], Ct)).Should().Be(0, "only the owner marks");
        (await db.Chunks.MarkDispatchedAsync(ws, "dispatcher-a", [.. a.Select(c => c.ChunkId)], Ct)).Should().Be(8);
        (await db.Chunks.ClaimForDispatchAsync(ws, "dispatcher-b", Limits, Claim, Ct)).Should().BeEmpty("dispatched chunks are in flight");
        var chunks = (await db.Jobs.GetChunksAsync(ws, first, cancellationToken: Ct)).Concat(await db.Jobs.GetChunksAsync(ws, second, cancellationToken: Ct))
            .Concat(await db.Jobs.GetChunksAsync(ws, third, cancellationToken: Ct));
        chunks.Count(c => c.Status == JobChunkStatus.Dispatched).Should().Be(8);
    }

    [Fact]
    public async Task An_unconfirmed_publish_keeps_the_claim_for_the_retry_delay_then_the_chunk_is_due_again()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        await db.RunningJobAsync(ws, chunks: 1);

        var claimed = await db.Chunks.ClaimForDispatchAsync(ws, "dispatcher-a", Limits, Claim, Ct);
        (await db.Chunks.ReleaseDispatchClaimAsync(ws, "dispatcher-a", [claimed[0].ChunkId], TimeSpan.FromMilliseconds(300), Ct)).Should().Be(1);
        (await db.Chunks.ClaimForDispatchAsync(ws, "dispatcher-b", Limits, Claim, Ct)).Should().BeEmpty();
        await Task.Delay(500, Ct);
        (await db.Chunks.ClaimForDispatchAsync(ws, "dispatcher-b", Limits, Claim, Ct)).Should().ContainSingle()
            .Which.Status.Should().Be(JobChunkStatus.Pending);
    }

    [Fact]
    public async Task A_retry_wait_chunk_is_dispatched_once_its_backoff_elapsed_and_a_lost_message_is_re_dispatched()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var job = await db.RunningJobAsync(ws, chunks: 2);
        var chunks = await db.Jobs.GetChunksAsync(ws, job, cancellationToken: Ct);

        // Chunk 1 failed transiently (RetryWait, backoff pending); chunk 2 was dispatched and its message lost.
        var lease = (await db.Chunks.ClaimAsync(ws, chunks[0].ChunkId, "worker-1", TimeSpan.FromMinutes(1), Ct)).Chunk!.Lease;
        (await db.Chunks.FailAsync(lease, ChunkError.Transient("Timeout", "timed out"), Ct)).Should().NotBeNull();
        (await db.Chunks.MarkDispatchedAsync(ws, chunks[1].ChunkId, Ct)).Should().BeTrue();
        (await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits, Claim, Ct)).Should().BeEmpty("backoff pending, message not yet overdue");

        await db.Core.ExecuteAsync(
            "UPDATE opportunity.job_chunk SET available_at = now() - interval '1 s', " +
            "dispatched_at = CASE WHEN chunk_id = @c2 THEN now() - interval '2 min' ELSE dispatched_at END WHERE job_id = @job",
            ("c2", chunks[1].ChunkId), ("job", job));
        var claimed = await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits, Claim, Ct);
        claimed.Select(c => (c.Sequence, c.Status)).Should().BeEquivalentTo(
            [(1, JobChunkStatus.RetryWait), (2, JobChunkStatus.Dispatched)]);
        (await db.Chunks.MarkDispatchedAsync(ws, "d", [.. claimed.Select(c => c.ChunkId)], Ct)).Should().Be(2);

        var after = await db.Jobs.GetChunksAsync(ws, job, cancellationToken: Ct);
        after.Should().OnlyContain(c => c.Status == JobChunkStatus.Dispatched, "RetryWait → Dispatched; Dispatched stays");
        (await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits, Claim, Ct)).Should().BeEmpty("each is published once per dispatch");
    }

    [Fact]
    public async Task Index_backpressure_and_the_security_throttle_hold_back_new_chunks_of_a_job()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var backlogged = await db.RunningJobAsync(ws, chunks: 5);
        var secure = await db.RunningJobAsync(ws, chunks: 5);
        await db.AddIndexTasksAsync(ws, backlogged, 51);
        await db.AddIndexTasksAsync(ws, secure, 2, SearchChangeMask.Security);

        var claimed = await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits, Claim, Ct);

        claimed.Should().NotContain(c => c.JobId == backlogged, "more than 50 un-applied index tasks stop new chunks (ADR-010 §6)");
        claimed.Where(c => c.JobId == secure).Should().HaveCount(2, "Q-10 throttle: 4 − 2 un-applied security tasks");
    }

    [Fact]
    public async Task Only_running_jobs_of_an_active_workspace_with_a_work_queue_are_dispatched()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var paused = await db.RunningJobAsync(ws, chunks: 2);
        await db.Jobs.PauseAsync(ws, paused, "test", Ct);

        (await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits, Claim, Ct)).Should().BeEmpty("a paused job is not dispatched");
        (await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits with { Operations = [] }, Claim, Ct)).Should().BeEmpty();

        await db.Jobs.ResumeAsync(ws, paused, Ct);
        (await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits with { Operations = [ChunkOperationKind.ImportChunk] }, Claim, Ct))
            .Should().BeEmpty("bulk-coding chunks are only dispatched while that kind has a queue");
        (await db.Chunks.ClaimForDispatchAsync(ws, "d", Limits, Claim, Ct)).Should().HaveCount(2);
    }
}
