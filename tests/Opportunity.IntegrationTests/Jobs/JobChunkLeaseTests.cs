using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Opportunity.Application.Jobs;
using Opportunity.Core.Jobs;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;

namespace Opportunity.IntegrationTests.Jobs;

/// <summary>E06-T02: Job/JobChunk state machine with leases and fencing against PostgreSQL (ADR-010 §2–§3, §7).</summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class JobChunkLeaseTests(MigrationPostgresFixture postgres)
{
    private static readonly TimeSpan Lease = JobDatabase.Lease;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_runs_from_creation_to_completion_with_O1_progress_and_item_results()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var initiator = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        var created = await db.Jobs.CreateAsync(new NewJob
        {
            WorkspaceId = ws,
            JobType = JobType.BulkCoding,
            InitiatedBy = initiator,
            TargetSnapshotId = snapshot,
            Parameters = new JsonObject { ["fieldId"] = 1005 },
            ClientIdempotencyKey = "client-key-1",
            CorrelationId = "corr-1",
        }, Ct);
        created.Created.Should().BeTrue();
        created.Job.Status.Should().Be(JobStatus.Created);

        // Starting before preparing is not in the matrix.
        (await db.Jobs.StartAsync(new JobStartRequest(ws, created.Job.JobId, ChunkOperationKind.BulkCodingChunk, []), Ct))
            .Should().Be(new JobTransitionResult(JobTransitionOutcome.NotAllowed, JobStatus.Created));
        (await db.Jobs.BeginPreparingAsync(ws, created.Job.JobId, Ct)).Status.Should().Be(JobStatus.Preparing);
        (await db.Jobs.BeginPreparingAsync(ws, created.Job.JobId, Ct)).Outcome.Should().Be(JobTransitionOutcome.NotAllowed);
        var explicitIds = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var start = await db.Jobs.StartAsync(new JobStartRequest(ws, created.Job.JobId, ChunkOperationKind.BulkCodingChunk,
        [
            new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, 1_000), 1_000, 4_096),
            new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1_001, 1_500), 500),
            new ChunkPlan(ChunkMembership.ExplicitIds(explicitIds), 2),
        ]), Ct);
        start.Should().Be(new JobTransitionResult(JobTransitionOutcome.Applied, JobStatus.Running));

        var chunks = await db.ChunksAsync(ws, created.Job.JobId);
        chunks.Select(c => c.Sequence).Should().Equal(1, 2, 3);
        chunks.Should().OnlyContain(c => c.Status == JobChunkStatus.Pending && c.AttemptCount == 0 && c.MaxAttempts == 5);
        chunks.Select(c => c.IdempotencyKey).Should().Equal(chunks.Select(c =>
            ChunkIdempotencyKey.ForChunk(ws, created.Job.JobId, c.Sequence, ChunkOperationKind.BulkCodingChunk, 0)));
        chunks[0].Membership.Should().Be(ChunkMembership.SnapshotRange(snapshot, 1, 1_000));
        chunks[2].Membership.Should().Be(ChunkMembership.ExplicitIds(explicitIds));

        (await db.Chunks.MarkDispatchedAsync(ws, chunks[0].ChunkId, Ct)).Should().BeTrue();
        (await db.Chunks.MarkDispatchedAsync(ws, chunks[0].ChunkId, Ct)).Should().BeFalse("already dispatched");
        (await db.Chunks.GetDispatchableAsync(ws, created.Job.JobId, 10, Ct)).Select(c => c.Sequence).Should().Equal(2, 3);

        // Workers take workspace, actor, type and parameters from PostgreSQL.
        var claim = await db.Chunks.ClaimAsync(ws, chunks[0].ChunkId, "worker-a", Lease, Ct);
        claim.Outcome.Should().Be(ChunkClaimOutcome.Claimed);
        claim.Chunk!.InitiatedBy.Should().Be(initiator);
        claim.Chunk.JobType.Should().Be(JobType.BulkCoding);
        claim.Chunk.OperationKind.Should().Be(ChunkOperationKind.BulkCodingChunk);
        claim.Chunk.TargetSnapshotId.Should().Be(snapshot);
        claim.Chunk.Parameters["fieldId"]!.GetValue<int>().Should().Be(1005);
        claim.Chunk.CorrelationId.Should().Be("corr-1");
        claim.Chunk.AttemptCount.Should().Be(1);
        claim.Chunk.Lease.LeaseToken.Should().Be(1);
        claim.Chunk.IdempotencyKey.Should().Be(chunks[0].IdempotencyKey);

        // A redelivery of the same message is deduplicated by the claim itself (the inbox).
        (await db.Chunks.ClaimAsync(ws, chunks[0].ChunkId, "worker-b", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.LeaseHeld);
        var heartbeat = await db.Chunks.HeartbeatAsync(claim.Chunk.Lease, Lease, Ct);
        heartbeat.Fence.Should().Be(ChunkFence.Proceed);
        heartbeat.LeaseExpiresAt.Should().BeOnOrAfter(claim.Chunk.LeaseExpiresAt);

        var skipped = Guid.CreateVersion7();
        var commit = await db.Chunks.CompleteAsync(claim.Chunk.Lease, new ChunkCompletion
        {
            ItemsApplied = 990,
            ItemsUnchanged = 7,
            IndexTasks = 1,
            ItemResults =
            [
                new JobItemResult(JobItemResultKind.SkippedConcurrentEdit, skipped, null, 1005, "ChangedAfterJobStart"),
                new JobItemResult(JobItemResultKind.SkippedConcurrentEdit, Guid.CreateVersion7(), null, 1005, "DocumentDeleted"),
                new JobItemResult(JobItemResultKind.ExcludedNoAccess, Guid.CreateVersion7(), null, null, "NoAccess"),
            ],
        }, Ct);
        commit.Should().Be(new ChunkCommitResult(ChunkCommitOutcome.Committed, JobStatus.Running));
        (await db.Chunks.ClaimAsync(ws, chunks[0].ChunkId, "worker-b", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.AlreadySettled);
        (await db.Chunks.CompleteAsync(claim.Chunk.Lease, ChunkCompletion.Empty, Ct)).Outcome
            .Should().Be(ChunkCommitOutcome.LeaseLost, "a duplicate completion never counts twice");

        foreach (var chunk in chunks.Skip(1))
        {
            var next = await db.Chunks.ClaimNextAsync(ws, created.Job.JobId, "worker-c", Lease, Ct);
            next.Chunk!.Sequence.Should().Be(chunk.Sequence);
            (await db.Chunks.CompleteAsync(next.Chunk.Lease, new ChunkCompletion { ItemsApplied = chunk.ItemCount }, Ct)).Committed.Should().BeTrue();
        }

        (await db.Chunks.ClaimNextAsync(ws, created.Job.JobId, "worker-c", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.NoneAvailable);
        var job = await db.JobAsync(ws, created.Job.JobId);
        job.Status.Should().Be(JobStatus.Completed);
        job.FinishedAt.Should().NotBeNull();
        job.StartedAt.Should().NotBeNull();
        job.Counters.Should().BeEquivalentTo(new JobCounters
        {
            ChunksTotal = 3,
            ChunksCommitted = 3,
            ItemsApplied = 990 + 500 + 2,
            ItemsUnchanged = 7,
            ItemsSkippedConcurrentEdit = 2,
            ItemsExcludedNoAccess = 1,
            IndexTasksTotal = 1,
        });
        await db.AssertCountersMatchRowsAsync(ws, created.Job.JobId);

        // Q-07: the job result carries the skipped list, keyset-paged.
        var page1 = await db.Jobs.GetItemResultsAsync(new JobItemResultQuery(ws, created.Job.JobId)
        {
            Kind = JobItemResultKind.SkippedConcurrentEdit,
            Limit = 1,
        }, Ct);
        page1.Single().Result.DocumentId.Should().Be(skipped);
        var page2 = await db.Jobs.GetItemResultsAsync(new JobItemResultQuery(ws, created.Job.JobId)
        {
            Kind = JobItemResultKind.SkippedConcurrentEdit,
            After = (page1[0].ChunkSequence, page1[0].ItemNo),
        }, Ct);
        page2.Single().Result.ReasonCode.Should().Be("DocumentDeleted");

        await db.Jobs.RecordIndexTasksAppliedAsync(ws, created.Job.JobId, 5, Ct);
        (await db.JobAsync(ws, created.Job.JobId)).Counters.IndexTasksApplied.Should().Be(1, "applied never exceeds total");

        // Progress is read from the counters, not by counting chunk rows.
        var plan = await db.Core.ColumnAsync(
            $"EXPLAIN SELECT chunks_committed FROM opportunity.job WHERE workspace_id = '{ws}' AND job_id = '{created.Job.JobId}'");
        plan.Should().NotContain(line => line.Contains("job_chunk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_creation_is_idempotent_per_initiator_and_client_key()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var user = Guid.CreateVersion7();
        NewJob Job(Guid initiator) => new() { WorkspaceId = ws, JobType = JobType.Export, InitiatedBy = initiator, ClientIdempotencyKey = "k-1" };

        var first = await db.Jobs.CreateAsync(Job(user), Ct);
        var retry = await db.Jobs.CreateAsync(Job(user), Ct);
        var other = await db.Jobs.CreateAsync(Job(Guid.CreateVersion7()), Ct);

        first.Created.Should().BeTrue();
        retry.Created.Should().BeFalse();
        retry.Job.JobId.Should().Be(first.Job.JobId);
        other.Created.Should().BeTrue();
        other.Job.JobId.Should().NotBe(first.Job.JobId);
        (await db.Jobs.GetAsync(Guid.CreateVersion7(), first.Job.JobId, Ct)).Should().BeNull("jobs are scoped to their workspace");
    }

    [Fact]
    public async Task Concurrent_claimers_never_process_a_chunk_twice()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 60);
        var processed = new ConcurrentBag<(Guid Chunk, long Token)>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            while (true)
            {
                var claim = await db.Chunks.ClaimNextAsync(ws, jobId, $"worker-{worker}", Lease, Ct);
                if (!claim.Claimed)
                {
                    claim.Outcome.Should().Be(ChunkClaimOutcome.NoneAvailable);
                    return;
                }

                processed.Add((claim.Chunk!.Lease.ChunkId, claim.Chunk.Lease.LeaseToken));
                (await db.Chunks.CompleteAsync(claim.Chunk.Lease, new ChunkCompletion { ItemsApplied = 100 }, Ct)).Committed.Should().BeTrue();
            }
        }, Ct)));

        processed.Should().HaveCount(60);
        processed.Select(p => p.Chunk).Should().OnlyHaveUniqueItems();
        processed.Should().OnlyContain(p => p.Token == 1, "no chunk was claimed twice");
        var job = await db.JobAsync(ws, jobId);
        job.Status.Should().Be(JobStatus.Completed);
        job.Counters.ItemsApplied.Should().Be(6_000);
        await db.AssertCountersMatchRowsAsync(ws, jobId);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_chunk_are_claimed_exactly_once()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 5);
        var chunks = await db.ChunksAsync(ws, jobId);

        foreach (var chunk in chunks)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
                Task.Run(() => db.Chunks.ClaimAsync(ws, chunk.ChunkId, $"worker-{i}", Lease, Ct), Ct)));
            results.Count(r => r.Claimed).Should().Be(1);
            results.Where(r => !r.Claimed).Should().OnlyContain(r => r.Outcome == ChunkClaimOutcome.LeaseHeld);
        }

        (await db.ChunksAsync(ws, jobId)).Should().OnlyContain(c => c.Status == JobChunkStatus.Running && c.AttemptCount == 1 && c.LeaseToken == 1);
    }

    [Fact]
    public async Task A_killed_worker_loses_its_lease_to_the_sweeper_and_exactly_one_completion_counts()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 2);
        var chunk = (await db.ChunksAsync(ws, jobId))[0];

        // Worker A claims with a short real lease and is "killed": no heartbeat, no release.
        var a = (await db.Chunks.ClaimAsync(ws, chunk.ChunkId, "worker-a", TimeSpan.FromSeconds(1), Ct)).Chunk!;
        (await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).Should().Be(LeaseRecoveryResult.None, "the lease is live");
        await Task.Delay(TimeSpan.FromMilliseconds(1_500), Ct);

        (await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.FromMinutes(1), cancellationToken: Ct)).Total.Should().Be(0, "within the grace period");
        (await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).Should().Be(new LeaseRecoveryResult(1, 0, 0));
        var reclaimed = (await db.ChunksAsync(ws, jobId))[0];
        reclaimed.Status.Should().Be(JobChunkStatus.Pending);
        reclaimed.AttemptCount.Should().Be(1, "the crashed attempt counts");

        var b = (await db.Chunks.ClaimAsync(ws, chunk.ChunkId, "worker-b", Lease, Ct)).Chunk!;
        b.Lease.LeaseToken.Should().Be(a.Lease.LeaseToken + 1);
        b.AttemptCount.Should().Be(2);

        // A comes back (a paused process, not dead after all): every write is fenced out by the token.
        (await db.Chunks.HeartbeatAsync(a.Lease, Lease, Ct)).Fence.Should().Be(ChunkFence.LeaseLost);
        (await db.Chunks.CompleteAsync(a.Lease, new ChunkCompletion { ItemsApplied = 100 }, Ct)).Outcome.Should().Be(ChunkCommitOutcome.LeaseLost);
        (await db.Chunks.FailAsync(a.Lease, ChunkError.Permanent("Boom", "stale"), Ct)).Outcome.Should().Be(ChunkFailureOutcome.LeaseLost);
        (await db.Chunks.ReleaseAsync(a.Lease, Ct)).Should().Be(ChunkReleaseOutcome.LeaseLost);

        (await db.Chunks.CompleteAsync(b.Lease, new ChunkCompletion { ItemsApplied = 100 }, Ct)).Committed.Should().BeTrue();
        (await db.Chunks.CompleteAsync(b.Lease, new ChunkCompletion { ItemsApplied = 100 }, Ct)).Committed.Should().BeFalse();
        var job = await db.JobAsync(ws, jobId);
        job.Counters.ChunksCommitted.Should().Be(1);
        job.Counters.ItemsApplied.Should().Be(100, "exactly one completion took effect");
    }

    [Fact]
    public async Task The_sweeper_visits_every_workspace()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var first = await db.CreateRunningJobAsync(chunkCount: 2);
        var second = await db.CreateRunningJobAsync(chunkCount: 1);
        foreach (var (ws, jobId) in new[] { first, second })
        {
            var claim = (await db.Chunks.ClaimNextAsync(ws, jobId, "dead", Lease, Ct)).Chunk!;
            await db.ExpireAsync(claim.Lease.ChunkId, TimeSpan.FromMinutes(1));
        }

        var sweeper = new JobLeaseSweeper(db.Chunks, new JobLeaseOptions(), NullLogger<JobLeaseSweeper>.Instance);

        (await sweeper.SweepOnceAsync(Ct)).Should().Be(new LeaseRecoveryResult(2, 0, 0));
        (await sweeper.SweepOnceAsync(Ct)).Should().Be(LeaseRecoveryResult.None);
    }

    [Fact]
    public async Task An_expired_lease_is_taken_over_directly_by_a_redelivered_claim()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 1);
        var chunk = (await db.ChunksAsync(ws, jobId))[0];
        var a = (await db.Chunks.ClaimAsync(ws, chunk.ChunkId, "worker-a", Lease, Ct)).Chunk!;
        await db.ExpireAsync(chunk.ChunkId, TimeSpan.FromSeconds(1));

        var b = (await db.Chunks.ClaimAsync(ws, chunk.ChunkId, "worker-b", Lease, Ct)).Chunk!;

        b.Lease.LeaseToken.Should().Be(2);
        (await db.Chunks.CompleteAsync(a.Lease, ChunkCompletion.Empty, Ct)).Outcome.Should().Be(ChunkCommitOutcome.LeaseLost);
        (await db.Chunks.CompleteAsync(b.Lease, ChunkCompletion.Empty, Ct)).Outcome.Should().Be(ChunkCommitOutcome.Committed);
        (await db.JobAsync(ws, jobId)).Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task Cancel_mid_run_keeps_committed_chunks_cancels_open_ones_and_fences_running_workers()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 10);
        for (var i = 0; i < 3; i++)
        {
            var done = (await db.Chunks.ClaimNextAsync(ws, jobId, "w", Lease, Ct)).Chunk!;
            (await db.Chunks.CompleteAsync(done.Lease, new ChunkCompletion { ItemsApplied = 100 }, Ct)).Committed.Should().BeTrue();
        }

        var running1 = (await db.Chunks.ClaimNextAsync(ws, jobId, "w1", Lease, Ct)).Chunk!;
        var running2 = (await db.Chunks.ClaimNextAsync(ws, jobId, "w2", Lease, Ct)).Chunk!;
        var running3 = (await db.Chunks.ClaimNextAsync(ws, jobId, "w3", Lease, Ct)).Chunk!;
        var retrying = (await db.Chunks.ClaimNextAsync(ws, jobId, "w4", Lease, Ct)).Chunk!;
        (await db.Chunks.FailAsync(retrying.Lease, ChunkError.Transient("Timeout", "slow"), Ct)).Outcome.Should().Be(ChunkFailureOutcome.RetryScheduled);
        var canceller = Guid.CreateVersion7();

        var cancel = await db.Jobs.CancelAsync(ws, jobId, canceller, "No longer needed.", Ct);

        cancel.Should().Be(new JobTransitionResult(JobTransitionOutcome.Applied, JobStatus.Cancelling));
        var job = await db.JobAsync(ws, jobId);
        job.CancelRequestedBy.Should().Be(canceller);
        job.Counters.ChunksCommitted.Should().Be(3);
        job.Counters.ChunksCancelled.Should().Be(4, "3 pending and 1 in retry wait");
        (await db.Jobs.CancelAsync(ws, jobId, canceller, cancellationToken: Ct)).Outcome.Should().Be(JobTransitionOutcome.NotAllowed);
        (await db.Chunks.ClaimNextAsync(ws, jobId, "late", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.NoneAvailable);

        // F2: the heartbeat tells running workers to stop; F3: a commit after the cancel does not happen.
        (await db.Chunks.HeartbeatAsync(running1.Lease, Lease, Ct)).Fence.Should().Be(ChunkFence.JobCancelling);
        (await db.Chunks.CompleteAsync(running1.Lease, new ChunkCompletion { ItemsApplied = 100 }, Ct)).Outcome.Should().Be(ChunkCommitOutcome.Cancelled);
        (await db.Chunks.ReleaseAsync(running2.Lease, Ct)).Should().Be(ChunkReleaseOutcome.Cancelled);
        (await db.JobAsync(ws, jobId)).Status.Should().Be(JobStatus.Cancelling, "one chunk is still running");
        (await db.Chunks.FailAsync(running3.Lease, ChunkError.Transient("Timeout", "slow"), Ct)).Outcome
            .Should().Be(ChunkFailureOutcome.Cancelled, "a cancelling job does not retry");

        job = await db.JobAsync(ws, jobId);
        job.Status.Should().Be(JobStatus.Cancelled);
        job.FinishedAt.Should().NotBeNull();
        job.Counters.ChunksCommitted.Should().Be(3, "cancellation never undoes committed chunks (Q-34)");
        job.Counters.ItemsApplied.Should().Be(300);
        job.Counters.ChunksCancelled.Should().Be(7);
        await db.AssertCountersMatchRowsAsync(ws, jobId);
        (await db.Jobs.ResumeAsync(ws, jobId, Ct)).Outcome.Should().Be(JobTransitionOutcome.NotAllowed);
    }

    [Fact]
    public async Task A_cancelled_job_with_a_dead_worker_finishes_cancelling_through_the_sweeper()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 3);
        var dead = (await db.Chunks.ClaimNextAsync(ws, jobId, "dead", Lease, Ct)).Chunk!;
        var dispatched = (await db.ChunksAsync(ws, jobId))[1];
        (await db.Chunks.MarkDispatchedAsync(ws, dispatched.ChunkId, Ct)).Should().BeTrue();

        (await db.Jobs.CancelAsync(ws, jobId, Guid.CreateVersion7(), cancellationToken: Ct)).Status.Should().Be(JobStatus.Cancelling);
        (await db.Chunks.ClaimAsync(ws, dispatched.ChunkId, "late", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.AlreadySettled);
        await db.ExpireAsync(dead.Lease.ChunkId, TimeSpan.FromMinutes(1));
        (await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.FromSeconds(10), cancellationToken: Ct)).Should().Be(new LeaseRecoveryResult(0, 0, 1));

        (await db.JobAsync(ws, jobId)).Status.Should().Be(JobStatus.Cancelled);
        await db.AssertCountersMatchRowsAsync(ws, jobId);
    }

    [Fact]
    public async Task Cancelling_a_created_job_cancels_it_at_once()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var job = (await db.Jobs.CreateAsync(new NewJob { WorkspaceId = ws, JobType = JobType.Import, InitiatedBy = Guid.CreateVersion7() }, Ct)).Job;

        (await db.Jobs.CancelAsync(ws, job.JobId, Guid.CreateVersion7(), cancellationToken: Ct)).Status.Should().Be(JobStatus.Cancelled);
        (await db.Jobs.BeginPreparingAsync(ws, job.JobId, Ct)).Outcome.Should().Be(JobTransitionOutcome.NotAllowed);
        (await db.Jobs.CancelAsync(ws, Guid.CreateVersion7(), Guid.CreateVersion7(), cancellationToken: Ct)).Outcome.Should().Be(JobTransitionOutcome.NotFound);
    }

    [Fact]
    public async Task Transient_failures_back_off_until_attempts_run_out_and_replay_resets_PostgreSQL_state()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 2, maxAttempts: 3);
        var chunks = await db.ChunksAsync(ws, jobId);
        var target = chunks[0].ChunkId;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claim = await db.Chunks.ClaimAsync(ws, target, "worker", Lease, Ct);
            claim.Chunk!.AttemptCount.Should().Be(attempt);
            var before = DateTimeOffset.UtcNow;
            var failure = await db.Chunks.FailAsync(claim.Chunk.Lease, ChunkError.Transient("OpenSearch503", "Service unavailable"), Ct);
            if (attempt < 3)
            {
                failure.Outcome.Should().Be(ChunkFailureOutcome.RetryScheduled);
                var nominal = 5 * Math.Pow(2, attempt - 1);
                failure.RetryAt!.Value.Should().BeCloseTo(before.AddSeconds(nominal), TimeSpan.FromSeconds((nominal * 0.2) + 2));
                (await db.Chunks.ClaimAsync(ws, target, "worker", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.NotDue);
                (await db.Chunks.GetDispatchableAsync(ws, jobId, 10, Ct)).Select(c => c.ChunkId).Should().NotContain(target);
                await db.ExpireAsync(target, TimeSpan.FromSeconds(1));
                (await db.Chunks.GetDispatchableAsync(ws, jobId, 10, Ct)).Should().Contain(c => c.ChunkId == target && c.Status == JobChunkStatus.RetryWait);
            }
            else
            {
                failure.Outcome.Should().Be(ChunkFailureOutcome.Failed);
            }
        }

        var failed = (await db.ChunksAsync(ws, jobId))[0];
        failed.Status.Should().Be(JobChunkStatus.Failed);
        failed.ErrorClass.Should().Be(ChunkErrorClass.Transient);
        failed.ErrorCode.Should().Be("OpenSearch503");
        failed.LastError.Should().Be("Service unavailable");

        var other = (await db.Chunks.ClaimAsync(ws, chunks[1].ChunkId, "worker", Lease, Ct)).Chunk!;
        (await db.Chunks.CompleteAsync(other.Lease, ChunkCompletion.Empty, Ct)).JobStatus.Should().Be(JobStatus.CompletedWithErrors);
        (await db.JobAsync(ws, jobId)).FinishedAt.Should().NotBeNull();

        // Replay = reset PG state (ADR-010 §7.4).
        var replay = await db.Jobs.ReplayFailedChunksAsync(ws, jobId, cancellationToken: Ct);
        replay.Should().Be(new ChunkReplayResult(JobTransitionOutcome.Applied, 1, JobStatus.Running));
        var replayed = (await db.ChunksAsync(ws, jobId))[0];
        replayed.Status.Should().Be(JobChunkStatus.Pending);
        replayed.AttemptCount.Should().Be(0);
        replayed.ReplayCount.Should().Be(1);
        replayed.SettledAt.Should().BeNull();
        (await db.JobAsync(ws, jobId)).FinishedAt.Should().BeNull();

        var again = (await db.Chunks.ClaimAsync(ws, target, "worker", Lease, Ct)).Chunk!;
        again.AttemptCount.Should().Be(1);
        (await db.Chunks.CompleteAsync(again.Lease, ChunkCompletion.Empty, Ct)).JobStatus.Should().Be(JobStatus.Completed);
        (await db.Jobs.ReplayFailedChunksAsync(ws, jobId, cancellationToken: Ct)).Outcome.Should().Be(JobTransitionOutcome.NotAllowed);
        await db.AssertCountersMatchRowsAsync(ws, jobId);
    }

    [Fact]
    public async Task A_permanent_error_fails_the_chunk_at_once()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 1);
        var claim = (await db.Chunks.ClaimNextAsync(ws, jobId, "worker", Lease, Ct)).Chunk!;

        var failure = await db.Chunks.FailAsync(claim.Lease, ChunkError.Permanent("EnvelopeMismatch", "workspace differs"), Ct);

        failure.Should().Be(new ChunkFailureResult(ChunkFailureOutcome.Failed, null, JobStatus.CompletedWithErrors));
        (await db.JobAsync(ws, jobId)).Counters.ChunksFailed.Should().Be(1);
    }

    [Fact]
    public async Task A_crash_loop_ends_in_Failed_when_the_attempts_are_used_up()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 2, maxAttempts: 2);
        var chunk = (await db.ChunksAsync(ws, jobId))[0].ChunkId;

        // Two attempts that die without recording anything: the claim itself takes over the expired lease.
        for (var i = 0; i < 2; i++)
        {
            (await db.Chunks.ClaimAsync(ws, chunk, "crashing", Lease, Ct)).Claimed.Should().BeTrue();
            await db.ExpireAsync(chunk, TimeSpan.FromSeconds(1));
        }

        (await db.Chunks.ClaimAsync(ws, chunk, "crashing", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.AttemptsExhausted);
        var failed = (await db.ChunksAsync(ws, jobId))[0];
        failed.Status.Should().Be(JobChunkStatus.Failed);
        failed.ErrorClass.Should().Be(ChunkErrorClass.AttemptsExhausted);

        // The sweeper ends the same loop for the other chunk.
        var other = (await db.ChunksAsync(ws, jobId))[1].ChunkId;
        for (var i = 0; i < 2; i++)
        {
            (await db.Chunks.ClaimAsync(ws, other, "crashing", Lease, Ct)).Claimed.Should().BeTrue();
            await db.ExpireAsync(other, TimeSpan.FromMinutes(1));
            var swept = await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.FromSeconds(10), cancellationToken: Ct);
            swept.Should().Be(i == 0 ? new LeaseRecoveryResult(1, 0, 0) : new LeaseRecoveryResult(0, 1, 0));
        }

        var job = await db.JobAsync(ws, jobId);
        job.Status.Should().Be(JobStatus.CompletedWithErrors);
        job.Counters.ChunksFailed.Should().Be(2);
        await db.AssertCountersMatchRowsAsync(ws, jobId);
    }

    [Fact]
    public async Task Pause_returns_chunks_to_pending_at_the_fences_without_charging_attempts()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 3);
        var chunks = await db.ChunksAsync(ws, jobId);
        var running = (await db.Chunks.ClaimAsync(ws, chunks[0].ChunkId, "w1", Lease, Ct)).Chunk!;
        var yielding = (await db.Chunks.ClaimAsync(ws, chunks[1].ChunkId, "w2", Lease, Ct)).Chunk!;
        (await db.Chunks.MarkDispatchedAsync(ws, chunks[2].ChunkId, Ct)).Should().BeTrue();

        (await db.Jobs.PauseAsync(ws, jobId, "Operator pause.", Ct)).Status.Should().Be(JobStatus.Paused);
        (await db.Jobs.PauseAsync(ws, jobId, "Again.", Ct)).Outcome.Should().Be(JobTransitionOutcome.NotAllowed);

        // F1: a delivery while paused is acked and the chunk returns to Pending for re-dispatch after resume.
        (await db.Chunks.ClaimAsync(ws, chunks[2].ChunkId, "w3", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.JobNotRunning);
        (await db.Chunks.ClaimNextAsync(ws, jobId, "w3", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.NoneAvailable);
        // F2 and F3.
        (await db.Chunks.HeartbeatAsync(yielding.Lease, Lease, Ct)).Fence.Should().Be(ChunkFence.JobNotRunning);
        (await db.Chunks.ReleaseAsync(yielding.Lease, Ct)).Should().Be(ChunkReleaseOutcome.ReturnedToPending);
        (await db.Chunks.CompleteAsync(running.Lease, new ChunkCompletion { ItemsApplied = 5 }, Ct)).Outcome.Should().Be(ChunkCommitOutcome.JobNotRunning);

        var paused = await db.ChunksAsync(ws, jobId);
        paused.Should().OnlyContain(c => c.Status == JobChunkStatus.Pending && c.AttemptCount == 0 && c.LeaseOwner == null);
        (await db.JobAsync(ws, jobId)).Counters.ItemsApplied.Should().Be(0, "the rolled-back completion recorded nothing");

        (await db.Jobs.ResumeAsync(ws, jobId, Ct)).Status.Should().Be(JobStatus.Running);
        var retry = (await db.Chunks.ClaimAsync(ws, chunks[0].ChunkId, "w1", Lease, Ct)).Chunk!;
        retry.AttemptCount.Should().Be(1);
        retry.Lease.LeaseToken.Should().Be(2);
    }

    [Fact]
    public async Task Five_consecutive_failures_trip_the_circuit_breaker()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 8);
        for (var i = 0; i < 5; i++)
        {
            var claim = (await db.Chunks.ClaimNextAsync(ws, jobId, "w", Lease, Ct)).Chunk!;
            var result = await db.Chunks.FailAsync(claim.Lease, ChunkError.Permanent("Mapping", "mapper_parsing_exception"), Ct);
            result.JobStatus.Should().Be(i < 4 ? JobStatus.Running : JobStatus.Paused);
        }

        var job = await db.JobAsync(ws, jobId);
        job.StatusReason.Should().StartWith("Circuit breaker");
        (await db.Jobs.ResumeAsync(ws, jobId, Ct)).Status.Should().Be(JobStatus.Running);
        var next = (await db.Chunks.ClaimNextAsync(ws, jobId, "w", Lease, Ct)).Chunk!;
        (await db.Chunks.FailAsync(next.Lease, ChunkError.Permanent("Mapping", "again"), Ct)).JobStatus
            .Should().Be(JobStatus.Running, "resume resets the consecutive count");
    }

    [Fact]
    public async Task A_job_level_failure_cancels_open_chunks_and_running_ones_at_their_fence()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 4);
        var running = (await db.Chunks.ClaimNextAsync(ws, jobId, "w", Lease, Ct)).Chunk!;

        (await db.Jobs.FailAsync(ws, jobId, "Target field deleted.", Ct)).Status.Should().Be(JobStatus.Failed);

        (await db.Chunks.CompleteAsync(running.Lease, ChunkCompletion.Empty, Ct)).Outcome.Should().Be(ChunkCommitOutcome.Cancelled);
        var job = await db.JobAsync(ws, jobId);
        job.Status.Should().Be(JobStatus.Failed);
        job.StatusReason.Should().Be("Target field deleted.");
        job.Counters.ChunksCancelled.Should().Be(4);
        await db.AssertCountersMatchRowsAsync(ws, jobId);
    }

    [Fact]
    public async Task A_job_without_chunks_completes_when_started()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 0);

        (await db.JobAsync(ws, jobId)).Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task Chunks_of_an_inactive_workspace_do_not_run()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: 2);
        var running = (await db.Chunks.ClaimNextAsync(ws, jobId, "w", Lease, Ct)).Chunk!;
        await db.Core.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Closed', closed_at = now() WHERE workspace_id = @ws", ("ws", ws));

        (await db.Chunks.ClaimNextAsync(ws, jobId, "w", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.NoneAvailable);
        (await db.Chunks.HeartbeatAsync(running.Lease, Lease, Ct)).Fence.Should().Be(ChunkFence.JobNotRunning);
        (await db.Chunks.CompleteAsync(running.Lease, ChunkCompletion.Empty, Ct)).Outcome.Should().Be(ChunkCommitOutcome.JobNotRunning);
        (await db.JobAsync(ws, jobId)).Counters.ChunksCommitted.Should().Be(0);
    }

    [Fact]
    public async Task The_repositories_work_under_forced_row_level_security_as_the_app_role()
    {
        await using var owner = await JobDatabase.CreateAsync(postgres);
        await using var app = await owner.AsAppRoleAsync();
        var (ws, jobId) = await app.CreateRunningJobAsync(chunkCount: 2);
        var otherWs = await owner.Core.CreateWorkspaceAsync();

        var claim = (await app.Chunks.ClaimNextAsync(ws, jobId, "w", Lease, Ct)).Chunk!;
        (await app.Chunks.HeartbeatAsync(claim.Lease, Lease, Ct)).Fence.Should().Be(ChunkFence.Proceed);
        (await app.Chunks.CompleteAsync(claim.Lease, new ChunkCompletion
        {
            ItemResults = [new JobItemResult(JobItemResultKind.Failed, null, 17, null, "InvalidRow")],
        }, Ct)).Committed.Should().BeTrue();
        (await app.Chunks.GetWorkspacesToSweepAsync(Ct)).Should().Contain([ws, otherWs]);
        (await app.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).Should().Be(LeaseRecoveryResult.None);
        (await app.Jobs.GetItemResultsAsync(new JobItemResultQuery(ws, jobId), Ct)).Single().Result.RowNo.Should().Be(17);

        // Another workspace's context sees nothing; no context sees nothing.
        (await app.Jobs.GetAsync(otherWs, jobId, Ct)).Should().BeNull();
        (await app.Chunks.ClaimAsync(otherWs, claim.Lease.ChunkId, "w", Lease, Ct)).Outcome.Should().Be(ChunkClaimOutcome.NotFound);
        await using var raw = app.DataSource.CreateCommand("SELECT count(*) FROM opportunity.job_chunk");
        ((long)(await raw.ExecuteScalarAsync(Ct))!).Should().Be(0);
        foreach (var table in new[] { "job", "job_chunk", "job_chunk_item_result" })
        {
            (await owner.Core.ScalarAsync<bool>(
                "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = ('opportunity.' || @t)::regclass", ("t", table)))
                .Should().BeTrue(table);
        }
    }

    [Fact]
    public async Task Stored_enumerations_match_the_domain()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        var checks = string.Join('\n', await db.Core.ColumnAsync(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname IN ('job_status_ck', 'job_type_ck', 'job_operation_kind_ck')"));

        foreach (var name in Enum.GetNames<JobStatus>().Concat(Enum.GetNames<JobType>()).Concat(Enum.GetNames<ChunkOperationKind>()))
        {
            checks.Should().Contain($"'{name}'");
        }

        var ws = await db.Core.CreateWorkspaceAsync();
        var job = await db.Jobs.CreateAsync(new NewJob { WorkspaceId = ws, JobType = JobType.Reindex, InitiatedBy = Guid.CreateVersion7() }, Ct);
        await db.Jobs.BeginPreparingAsync(ws, job.Job.JobId, Ct);
        Func<int, Task> insertWithFixedKey = seq => db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.job_chunk (workspace_id, chunk_id, job_id, chunk_sequence, membership_kind, projection_generation,
                document_id_from, document_id_to, item_count, idempotency_key, max_attempts)
            VALUES (@ws, gen_random_uuid(), @job, @seq, 3, 1, '00000000-0000-0000-0000-000000000000',
                    'ffffffff-ffff-ffff-ffff-ffffffffffff', 0, repeat('a', 64), 5)
            """,
            ("ws", ws), ("job", job.Job.JobId), ("seq", seq));
        await insertWithFixedKey(1);
        (await FluentActions.Invoking(() => insertWithFixedKey(2)).Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("job_chunk_idempotency_key_uq");
    }

    [Fact]
    public async Task Randomized_operations_keep_counters_statuses_and_fences_consistent()
    {
        await using var db = await JobDatabase.CreateAsync(postgres);
        for (var seed = 1; seed <= 4; seed++)
        {
            var random = new Random(seed);
            var (ws, jobId) = await db.CreateRunningJobAsync(chunkCount: random.Next(2, 9), maxAttempts: random.Next(1, 4));
            var leases = new List<ChunkLease>();
            var commits = new Dictionary<Guid, int>();
            for (var step = 0; step < 120; step++)
            {
                var chunks = await db.ChunksAsync(ws, jobId);
                var chunk = chunks[random.Next(chunks.Count)];
                switch (random.Next(9))
                {
                    case 0 or 1:
                        if ((await db.Chunks.ClaimAsync(ws, chunk.ChunkId, $"w{step}", Lease, Ct)).Chunk is { } claimed)
                        {
                            leases.Add(claimed.Lease);
                        }

                        break;
                    case 2 or 3 when leases.Count > 0:
                        var lease = leases[random.Next(leases.Count)];
                        leases.Remove(lease);
                        if ((await db.Chunks.CompleteAsync(lease, new ChunkCompletion { ItemsApplied = 1 }, Ct)).Committed)
                        {
                            commits[lease.ChunkId] = commits.GetValueOrDefault(lease.ChunkId) + 1;
                        }

                        break;
                    case 4 when leases.Count > 0:
                        var failing = leases[random.Next(leases.Count)];
                        leases.Remove(failing);
                        await db.Chunks.FailAsync(failing, random.Next(2) == 0
                            ? ChunkError.Transient("T", "transient") : ChunkError.Permanent("P", "permanent"), Ct);
                        break;
                    case 5:
                        await db.ExpireAsync(chunk.ChunkId, TimeSpan.FromMinutes(1));
                        break;
                    case 6:
                        await db.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.FromSeconds(10), cancellationToken: Ct);
                        break;
                    case 7:
                        switch (random.Next(5))
                        {
                            case 0: await db.Jobs.PauseAsync(ws, jobId, "random", Ct); break;
                            case 1: await db.Jobs.ResumeAsync(ws, jobId, Ct); break;
                            case 2: await db.Jobs.ReplayFailedChunksAsync(ws, jobId, cancellationToken: Ct); break;
                            case 3 when random.Next(4) == 0: await db.Jobs.CancelAsync(ws, jobId, Guid.CreateVersion7(), cancellationToken: Ct); break;
                            default: break;
                        }

                        break;
                    default:
                        if (leases.Count > 0)
                        {
                            await db.Chunks.HeartbeatAsync(leases[random.Next(leases.Count)], Lease, Ct);
                        }

                        break;
                }

                var because = $"seed {seed}, step {step}";
                var job = await db.JobAsync(ws, jobId);
                chunks = await db.ChunksAsync(ws, jobId);
                job.Counters.ChunksCommitted.Should().Be(chunks.Count(c => c.Status == JobChunkStatus.Committed), because);
                job.Counters.ChunksFailed.Should().Be(chunks.Count(c => c.Status == JobChunkStatus.Failed), because);
                job.Counters.ChunksCancelled.Should().Be(chunks.Count(c => c.Status == JobChunkStatus.Cancelled), because);
                job.Counters.ItemsApplied.Should().Be(commits.Values.Sum(), because);
                commits.Values.Should().OnlyContain(n => n == 1, because + ": a chunk commits at most once");
                chunks.Should().OnlyContain(c => c.AttemptCount <= c.MaxAttempts, because);
                chunks.Should().OnlyContain(c => (c.Status == JobChunkStatus.Running) == (c.LeaseOwner != null), because);
                if (job.Status == JobStatus.Completed)
                {
                    chunks.Should().OnlyContain(c => c.Status == JobChunkStatus.Committed, because);
                }

                if (job.Status is JobStatus.Cancelled or JobStatus.CompletedWithErrors)
                {
                    chunks.Should().OnlyContain(c => JobChunkStateMachine.IsSettled(c.Status), because);
                }

                (job.FinishedAt is not null).Should().Be(JobStateMachine.IsFinished(job.Status), because);
            }
        }
    }
}
