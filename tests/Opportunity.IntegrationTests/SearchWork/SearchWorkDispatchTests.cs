using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// E06-T03: claiming and relaying search work (at-least-once, no double claims, PostgreSQL as the retry ledger), worker
/// coalescing and leases, and retention by partition drop (ADR-001 §1 R4, §5, §6; ADR-010 §3).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class SearchWorkDispatchTests(MigrationPostgresFixture postgres)
{
    private static readonly TimeSpan Claim = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_dispatchers_never_claim_the_same_row()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 120);
        await Parallel.ForEachAsync(w.Documents, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct },
            async (d, ct) => await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, d, true), ct));

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => db.Outbox.ClaimAsync(w.Id, $"dispatcher-{i}", 50, Claim, Ct)));

        var ids = claims.SelectMany(c => c.Select(r => r.OutboxId)).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(120, "four claimers of 50 drain 120 rows between them");
        claims.SelectMany(c => c).Should().OnlyContain(r => r.AttemptCount == 1);
        (await db.Outbox.ClaimAsync(w.Id, "dispatcher-late", 50, Claim, Ct)).Should().BeEmpty("live claims are not reclaimed");
    }

    [Fact]
    public async Task A_relay_that_dies_after_publishing_leaves_the_rows_to_be_published_again_with_the_same_idempotency_key()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 3);
        foreach (var doc in w.Documents)
        {
            await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, doc, true), Ct);
        }

        // Dispatcher 1 claims and publishes, then crashes before marking the rows Dispatched.
        var publisher = new RecordingPublisher();
        var crashed = await db.Outbox.ClaimAsync(w.Id, "dispatcher-1", 10, TimeSpan.FromMilliseconds(300), Ct);
        foreach (var row in crashed)
        {
            await publisher.PublishAsync(new OutgoingMessage<SearchOutboxMessage>(SearchLanes.Queue(row.Lane),
                new SearchOutboxMessage { OutboxId = row.OutboxId, DocumentId = row.DocumentId, DocumentVersion = row.DocumentVersion },
                new MessageCorrelation("crash"), ChunkIdempotencyKey.ForOutbox(w.Id, row.OutboxId))
            { Attempt = row.AttemptCount }, Ct);
        }

        // Dispatcher 2 finds nothing while the claim lives, then takes over once it expired.
        var relay = Relay(db, publisher, "dispatcher-2");
        (await relay.RelayOnceAsync(w.Id, Ct)).Claimed.Should().Be(0);
        await Task.Delay(400, Ct);
        var result = await relay.RelayOnceAsync(w.Id, Ct);

        result.OutboxPublished.Should().Be(3);
        publisher.Messages.Should().HaveCount(6, "at-least-once: each row was delivered twice, a bounded and harmless duplicate");
        publisher.Messages.GroupBy(m => m.IdempotencyKey).Should().HaveCount(3).And.OnlyContain(g => g.Count() == 2);
        publisher.Messages.Skip(3).Should().OnlyContain(m => m.Attempt == 2);
        publisher.Messages.Should().OnlyContain(m => m.Destination == WorkQueues.IndexInteractive);
        foreach (var row in crashed)
        {
            var stored = (await db.Outbox.GetAsync(w.Id, row.OutboxId, Ct))!;
            stored.Status.Should().Be(SearchOutboxStatus.Dispatched);
            stored.AttemptCount.Should().Be(2);
        }

        // The late mark of the crashed dispatcher changes nothing: it no longer holds the claim.
        (await db.Outbox.MarkDispatchedAsync(w.Id, "dispatcher-1", crashed, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task An_unconfirmed_publish_keeps_the_row_in_postgres_with_backoff_and_fails_it_after_the_last_attempt()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 1);
        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, w.Documents[0], true), Ct);
        var relay = Relay(db, new RecordingPublisher { FailWith = "broker unavailable" }, "dispatcher-1");

        var result = await relay.RelayOnceAsync(w.Id, Ct);

        result.OutboxUnconfirmed.Should().Be(1);
        var row = await SingleRowAsync(db, w.Id);
        row.Status.Should().Be(SearchOutboxStatus.Pending);
        row.AttemptCount.Should().Be(1);
        row.LastError.Should().Be("broker unavailable");
        (await relay.RelayOnceAsync(w.Id, Ct)).Claimed.Should().Be(0, "the backoff delays the next attempt");

        await db.Core.ExecuteAsync(
            "UPDATE opportunity.search_outbox SET attempt_count = @max, available_at = now() - interval '1 s' WHERE workspace_id = @ws",
            ("max", SearchOutboxRetryPolicy.MaxAttempts), ("ws", w.Id));
        (await relay.RelayOnceAsync(w.Id, Ct)).Claimed.Should().Be(0);
        (await SingleRowAsync(db, w.Id)).Status.Should().Be(SearchOutboxStatus.Failed);
        (await db.Maintenance.GetBacklogAsync(w.Id, Ct)).OutboxFailed.Should().Be(1, "a failed row holds the watermark back and alerts");
    }

    [Fact]
    public async Task Relay_publishes_chunk_tasks_by_lane_and_a_dispatched_task_nobody_leased_is_redispatched()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 4);
        var (taskId, chunk) = await BulkTaskAsync(db, w);
        var publisher = new RecordingPublisher();
        var relay = Relay(db, publisher, "dispatcher-1");

        (await relay.RelayOnceAsync(w.Id, Ct)).TasksPublished.Should().Be(1);
        var message = publisher.Messages.Single();
        message.Destination.Should().Be(WorkQueues.IndexBulk);
        message.Payload.Should().Be(new IndexChunkTaskMessage { TaskId = taskId });
        message.IdempotencyKey.Should().Be(ChunkIdempotencyKey.ForChunk(w.Id, chunk.Lease.JobId, 1, ChunkOperationKind.IndexChunk, 0));
        message.Correlation.JobId.Should().Be(chunk.Lease.JobId);
        (await db.Tasks.GetAsync(w.Id, taskId, Ct))!.Status.Should().Be(IndexChunkTaskStatus.Dispatched);

        // The message is lost: after the timeout the task returns to Pending and is published again.
        await db.Core.ExecuteAsync("UPDATE opportunity.index_chunk_task SET dispatched_at = now() - interval '2 minutes'");
        (await db.Maintenance.RecoverAsync(w.Id, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), Ct)).TasksRedispatched.Should().Be(1);
        (await relay.RelayOnceAsync(w.Id, Ct)).TasksPublished.Should().Be(1);
        publisher.Messages.Select(m => m.IdempotencyKey).Distinct().Should().HaveCount(1, "the key is stable across re-publish");
    }

    /// <summary>ADR-001 §6.3: a Dispatched row whose message never got applied (lost or dead-lettered) is published again.</summary>
    [Fact]
    public async Task A_dispatched_outbox_row_never_applied_is_redispatched_after_the_timeout()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 2);
        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, w.Documents[0], true), Ct);
        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, w.Documents[1], true), Ct);
        var publisher = new RecordingPublisher();
        var relay = Relay(db, publisher, "dispatcher-1");
        (await relay.RelayOnceAsync(w.Id, Ct)).OutboxPublished.Should().Be(2);
        var lost = publisher.Messages[0];
        var lostId = ((SearchOutboxMessage)lost.Payload).OutboxId;
        var appliedDoc = ((SearchOutboxMessage)publisher.Messages[1].Payload).DocumentId;
        await db.Outbox.MarkAppliedThroughAsync(w.Id, appliedDoc, long.MaxValue, Ct);

        (await db.Maintenance.RecoverAsync(w.Id, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), Ct)).OutboxRedispatched
            .Should().Be(0, "a recently dispatched row is still in flight");
        await db.Core.ExecuteAsync(
            "UPDATE opportunity.search_outbox SET dispatched_at = now() - interval '2 minutes' WHERE workspace_id = @ws", ("ws", w.Id));
        (await db.Maintenance.RecoverAsync(w.Id, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), Ct)).OutboxRedispatched
            .Should().Be(1, "only the unapplied row returns to Pending");
        (await db.Outbox.GetAsync(w.Id, lostId, Ct))!.Status.Should().Be(SearchOutboxStatus.Pending);

        (await relay.RelayOnceAsync(w.Id, Ct)).OutboxPublished.Should().Be(1);
        var again = publisher.Messages[^1];
        ((SearchOutboxMessage)again.Payload).OutboxId.Should().Be(lostId);
        again.IdempotencyKey.Should().Be(lost.IdempotencyKey, "the key is stable across re-publish");
        var row = (await db.Outbox.GetAsync(w.Id, lostId, Ct))!;
        row.Status.Should().Be(SearchOutboxStatus.Dispatched);
        row.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task The_index_worker_coalesces_every_row_of_a_document_up_to_the_version_it_wrote()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 1);
        var doc = w.Documents[0];
        foreach (var value in new[] { true, false, true })
        {
            await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, doc, value), Ct);
        }

        // Rows for versions 2, 3 and 4. A worker that read and wrote version 3 applies 2 and 3, whatever their status.
        await db.Outbox.ClaimAsync(w.Id, "dispatcher-1", 1, Claim, Ct);
        (await db.Outbox.MarkAppliedThroughAsync(w.Id, doc, 3, Ct)).Should().Be(2);
        (await db.Outbox.MarkAppliedThroughAsync(w.Id, doc, 3, Ct)).Should().Be(0, "idempotent");

        var pending = await db.Outbox.ClaimAsync(w.Id, "dispatcher-1", 10, Claim, Ct);
        pending.Should().ContainSingle().Which.DocumentVersion.Should().Be(4, "coalesced rows are never published");
        (await db.Maintenance.GetBacklogAsync(w.Id, Ct)).OutboxUnapplied.Should().Be(1);
    }

    [Fact]
    public async Task Index_task_leases_fence_stale_workers_and_completion_counts_the_job_once()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 2);
        var (taskId, chunk) = await BulkTaskAsync(db, w);

        var first = await db.Tasks.LeaseAsync(w.Id, taskId, "indexer-1", TimeSpan.FromMinutes(1), Ct);
        first.Outcome.Should().Be(IndexTaskLeaseOutcome.Leased);
        first.Task!.Membership.Should().Be(chunk.Membership);
        (await db.Tasks.LeaseAsync(w.Id, taskId, "indexer-2", TimeSpan.FromMinutes(1), Ct)).Outcome.Should().Be(IndexTaskLeaseOutcome.LeaseHeld);

        // Worker 1 stalls; its lease expires and the recovery hands the task to worker 2.
        await db.Core.ExecuteAsync("UPDATE opportunity.index_chunk_task SET lease_expires_at = now() - interval '1 minute'");
        (await db.Maintenance.RecoverAsync(w.Id, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), Ct)).TasksLeaseExpired.Should().Be(1);
        var second = await db.Tasks.LeaseAsync(w.Id, taskId, "indexer-2", TimeSpan.FromMinutes(1), Ct);
        second.Lease!.LeaseToken.Should().Be(first.Lease!.LeaseToken + 1);

        (await db.Tasks.CompleteAsync(first.Lease, Ct)).Should().BeFalse("the stale token is fenced");
        (await db.Tasks.FailAsync(first.Lease, ChunkError.Transient("timeout", "stale"), Ct)).Outcome.Should().Be(IndexTaskFailureOutcome.LeaseLost);
        (await db.Tasks.CompleteAsync(second.Lease!, Ct)).Should().BeTrue();
        (await db.Tasks.CompleteAsync(second.Lease!, Ct)).Should().BeFalse();

        var task = (await db.Tasks.GetAsync(w.Id, taskId, Ct))!;
        task.Status.Should().Be(IndexChunkTaskStatus.Applied);
        task.AttemptCount.Should().Be(2);
        (await db.Jobs.GetAsync(w.Id, chunk.Lease.JobId, Ct))!.Counters.IndexTasksApplied.Should().Be(1);
        (await db.Tasks.LeaseAsync(w.Id, taskId, "indexer-3", TimeSpan.FromMinutes(1), Ct)).Outcome
            .Should().Be(IndexTaskLeaseOutcome.AlreadySettled, "a duplicate delivery is acked and dropped");
        (await db.Tasks.LeaseAsync(w.Id, Guid.CreateVersion7(), "indexer-3", TimeSpan.FromMinutes(1), Ct)).Outcome
            .Should().Be(IndexTaskLeaseOutcome.NotFound);
    }

    [Fact]
    public async Task Failed_index_task_attempts_retry_with_backoff_and_end_in_failed_never_cancelled()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 2);
        var (taskId, _) = await BulkTaskAsync(db, w);

        var lease = (await db.Tasks.LeaseAsync(w.Id, taskId, "indexer-1", TimeSpan.FromMinutes(1), Ct)).Lease!;
        var retry = await db.Tasks.FailAsync(lease, ChunkError.Transient("os-429", "too many requests"), Ct);
        retry.Outcome.Should().Be(IndexTaskFailureOutcome.RetryScheduled);
        retry.RetryAt.Should().BeAfter(DateTimeOffset.UtcNow);
        (await db.Tasks.LeaseAsync(w.Id, taskId, "indexer-1", TimeSpan.FromMinutes(1), Ct)).Outcome.Should().Be(IndexTaskLeaseOutcome.NotDue);

        await db.Core.ExecuteAsync("UPDATE opportunity.index_chunk_task SET available_at = now() - interval '1 s'");
        lease = (await db.Tasks.LeaseAsync(w.Id, taskId, "indexer-1", TimeSpan.FromMinutes(1), Ct)).Lease!;
        (await db.Tasks.FailAsync(lease, ChunkError.Permanent("mapper_parsing_exception", "bad field"), Ct)).Outcome
            .Should().Be(IndexTaskFailureOutcome.Failed);
        var task = (await db.Tasks.GetAsync(w.Id, taskId, Ct))!;
        task.Status.Should().Be(IndexChunkTaskStatus.Failed);
        task.LastError.Should().Be("mapper_parsing_exception: bad field");
        (await db.Maintenance.GetBacklogAsync(w.Id, Ct)).TasksFailed.Should().Be(1);
    }

    [Fact]
    public async Task Rows_past_retention_are_removed_by_dropping_their_partition_never_by_delete()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 1);
        var today = DateTime.UtcNow.Date;
        var (old, blocked) = (today.AddDays(-10), today.AddDays(-9));
        foreach (var day in new[] { old, blocked })
        {
            var suffix = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            await db.Core.ExecuteAsync(
                $"""
                CREATE TABLE opportunity.search_outbox_p{suffix} PARTITION OF opportunity.search_outbox
                    FOR VALUES FROM ('{day:yyyy-MM-dd} 00:00:00+00') TO ('{day.AddDays(1):yyyy-MM-dd} 00:00:00+00');
                CREATE TABLE opportunity.index_chunk_task_p{suffix} PARTITION OF opportunity.index_chunk_task
                    FOR VALUES FROM ('{day:yyyy-MM-dd} 00:00:00+00') TO ('{day.AddDays(1):yyyy-MM-dd} 00:00:00+00');
                """);
        }

        // Day "old": every row applied. Day "blocked": one row the index never confirmed. Today: a fresh pending row.
        await InsertOutboxAsync(db, w, old.AddHours(1), SearchOutboxStatus.Applied);
        await InsertOutboxAsync(db, w, old.AddHours(2), SearchOutboxStatus.Applied);
        await InsertOutboxAsync(db, w, blocked.AddHours(1), SearchOutboxStatus.Applied);
        await InsertOutboxAsync(db, w, blocked.AddHours(2), SearchOutboxStatus.Dispatched);
        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, w.Documents[0], true), Ct);

        // Any DELETE on the outbox fails the test.
        await db.Core.ExecuteAsync(
            """
            CREATE FUNCTION opportunity.test_no_delete() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'retention must not DELETE'; END $$;
            CREATE TRIGGER test_no_delete BEFORE DELETE ON opportunity.search_outbox FOR EACH STATEMENT EXECUTE FUNCTION opportunity.test_no_delete();
            """);

        var examined = await db.Maintenance.DropExpiredPartitionsAsync(DateTimeOffset.UtcNow.AddDays(-3), Ct);

        var outbox = examined.Where(p => p.Table == "search_outbox").ToDictionary(p => p.Partition);
        outbox[$"search_outbox_p{old:yyyyMMdd}"].Dropped.Should().BeTrue();
        outbox[$"search_outbox_p{blocked:yyyyMMdd}"].Should().Match<SearchWorkPartition>(p => !p.Dropped && p.UnappliedRows == 1);
        outbox.Keys.Should().NotContain($"search_outbox_p{today:yyyyMMdd}", "only partitions past the cutoff are examined");
        examined.Where(p => p.Table == "index_chunk_task").Should().OnlyContain(p => p.Dropped, "empty task partitions are dropped too");

        (await db.Core.ScalarAsync<bool>($"SELECT to_regclass('opportunity.search_outbox_p{old:yyyyMMdd}') IS NULL")).Should().BeTrue();
        (await db.CountAsync("search_outbox", w.Id)).Should().Be(3, "the blocked day and today's row remain");
        (await db.Core.ScalarAsync<bool>(
            $"SELECT relforcerowsecurity FROM pg_class WHERE oid = 'opportunity.search_outbox_p{blocked:yyyyMMdd}'::regclass"))
            .Should().BeTrue("a kept partition is re-forced");
        (await db.Core.ScalarAsync<bool>("SELECT has_table_privilege('opportunity_app', 'opportunity.search_outbox', 'DELETE')"))
            .Should().BeFalse("the application role cannot delete outbox rows at all");
    }

    [Fact]
    public async Task Partitions_are_created_ahead_and_are_closed_to_the_runtime_roles()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var through = DateTimeOffset.UtcNow.AddDays(40);

        (await db.Maintenance.EnsurePartitionsAsync(through, Ct)).Should().BeGreaterThan(0);
        (await db.Maintenance.EnsurePartitionsAsync(through, Ct)).Should().Be(0, "idempotent");

        var last = through.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        (await db.Core.ScalarAsync<bool>($"SELECT to_regclass('opportunity.index_chunk_task_p{last}') IS NOT NULL")).Should().BeTrue();
        (await db.Core.ScalarAsync<bool>(
            $"SELECT has_table_privilege('opportunity_app', 'opportunity.search_outbox_p{last}', 'SELECT')")).Should().BeFalse();
    }

    private static SearchWorkRelay Relay(SearchWorkDatabase db, IMessagePublisher publisher, string owner) =>
        new(db.Outbox, db.Tasks, publisher, new SearchWorkRelayOptions { Owner = owner, BatchSize = 100 });

    private static async Task<SearchOutboxRow> SingleRowAsync(SearchWorkDatabase db, Guid ws)
    {
        var id = await db.Core.ScalarAsync<long>("SELECT outbox_id FROM opportunity.search_outbox WHERE workspace_id = @ws", ("ws", ws));
        return (await db.Outbox.GetAsync(ws, id, Ct))!;
    }

    private static async Task<(Guid TaskId, ClaimedChunk Chunk)> BulkTaskAsync(SearchWorkDatabase db, TestWorkspace w)
    {
        var snapshot = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, w.Documents.Count), w.Documents.Count)], snapshotId: snapshot);
        var result = await db.Core.Coding.ApplyChunkAsync(chunk, new CodingWriteRequest
        {
            WorkspaceId = w.Id,
            IdempotencyKey = chunk.IdempotencyKey,
            Actor = new CodingActor(SearchWorkDatabase.Reviewer, CodingActorType.BulkHuman),
            JobId = chunk.Lease.JobId,
            Documents = [.. w.Documents.Select(d => new CodingTarget(d, 1))],
            Operations = [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))],
        }, Ct);
        return (result.IndexTaskId!.Value, chunk);
    }

    private static Task InsertOutboxAsync(SearchWorkDatabase db, TestWorkspace w, DateTime createdAt, SearchOutboxStatus status) =>
        db.Core.ExecuteAsync(
            """
            INSERT INTO opportunity.search_outbox (workspace_id, created_at, outbox_id, document_id, document_version, change_mask, lane,
                search_generation, committed_at, status, dispatched_at, applied_at)
            VALUES (@ws, @created, 1000 + extract(epoch FROM @created)::bigint, @doc, 2, 4, 2, 1, @created, @status, @created,
                    CASE WHEN @status = 4 THEN @created END)
            """,
            ("ws", w.Id), ("created", DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)), ("doc", w.Documents[0]), ("status", (short)status));

    /// <summary>Records every publish; optionally fails every publish as an unconfirmed one.</summary>
    private sealed class RecordingPublisher : IMessagePublisher
    {
        public ConcurrentQueue<Published> Queue { get; } = new();

        public IReadOnlyList<Published> Messages => [.. Queue];

        public string? FailWith { get; init; }

        public Task<MessageEnvelope> PublishAsync<TPayload>(OutgoingMessage<TPayload> message, CancellationToken cancellationToken = default)
            where TPayload : class
        {
            if (FailWith is not null)
            {
                throw new MessagePublishException(FailWith);
            }

            Queue.Enqueue(new Published(message.Destination, message.Payload, message.Correlation, message.IdempotencyKey, message.Attempt));
            return Task.FromResult<MessageEnvelope>(null!);
        }
    }

    private sealed record Published(WorkQueue Destination, object Payload, MessageCorrelation Correlation, string IdempotencyKey, int Attempt);
}
