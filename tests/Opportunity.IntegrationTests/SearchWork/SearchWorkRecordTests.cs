using System.Text.Json.Nodes;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Audit;
using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.Data;
using Opportunity.Data.SearchWork;
using Opportunity.IntegrationTests.Audit;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.SearchWork;

/// <summary>
/// E06-T03: which transaction writes which search work record (ADR-001 §1 R1/R2), and the commit-ordered
/// SearchGeneration / CommittedAt stamp (ADR-001 §7.1, QA finding 1).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class SearchWorkRecordTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_interactive_coding_transaction_inserts_exactly_one_outbox_row_and_no_chunk_task()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 2);
        var doc = w.Documents[0];

        var result = await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, doc, true), Ct);

        result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        (await db.CountAsync("search_outbox", w.Id)).Should().Be(1);
        (await db.CountAsync("index_chunk_task", w.Id)).Should().Be(0);
        var row = await OnlyOutboxRowAsync(db, w.Id);
        row.DocumentId.Should().Be(doc);
        row.DocumentVersion.Should().Be(2, "the version this transaction wrote");
        row.ChangeMask.Should().Be(SearchChangeMask.Coding);
        row.Lane.Should().Be(MessageLane.Interactive);
        row.SearchGeneration.Should().Be(1);
        row.Status.Should().Be(SearchOutboxStatus.Pending);

        // A write that changes nothing creates no search work; a replay of the same key creates none either.
        var request = SearchWorkDatabase.SetResponsive(w, doc, true);
        (await db.Core.Coding.ApplyAsync(request, Ct)).Documents.Single().Outcome.Should().Be(DocumentCodingOutcome.Unchanged);
        (await db.CountAsync("search_outbox", w.Id)).Should().Be(1);
        var change = SearchWorkDatabase.SetResponsive(w, doc, false);
        await db.Core.Coding.ApplyAsync(change, Ct);
        (await db.Core.Coding.ApplyAsync(change, Ct)).Outcome.Should().Be(CodingWriteOutcome.Replayed);
        (await db.CountAsync("search_outbox", w.Id)).Should().Be(2);

        // A security-affecting field (Q-11) goes to the security lane (Q-10).
        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.Interactive(w.Id, w.Documents[1],
            CodingFieldOperation.Set(w.Privilege, JsonValue.Create(w.Privileged))), Ct);
        var security = await db.Core.ScalarAsync<short>(
            "SELECT lane FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", w.Id), ("doc", w.Documents[1]));
        security.Should().Be((short)MessageLane.Security);
        (await db.Core.ScalarAsync<short>(
            "SELECT change_mask FROM opportunity.search_outbox WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", w.Id), ("doc", w.Documents[1]))).Should().Be((short)(SearchChangeMask.Coding | SearchChangeMask.Security));
    }

    [Fact]
    public async Task A_rolled_back_coding_transaction_leaves_no_outbox_row_and_no_generation_gap()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 1);
        await db.Core.ExecuteAsync(
            """
            CREATE FUNCTION opportunity.test_fail_outbox() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$;
            CREATE TRIGGER test_fail_outbox BEFORE INSERT ON opportunity.search_outbox FOR EACH ROW EXECUTE FUNCTION opportunity.test_fail_outbox();
            """);

        var act = () => db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, w.Documents[0], true), Ct);
        await act.Should().ThrowAsync<PostgresException>();

        (await db.Core.Documents.GetVersionAsync(w.Id, w.Documents[0], Ct)).Should().Be(1, "the coding change rolled back with its outbox row");
        (await db.CountAsync("coding_event", w.Id)).Should().Be(0);
        (await db.CountAsync("search_outbox", w.Id)).Should().Be(0);
        (await db.CountAsync("workspace_search_generation", w.Id)).Should().Be(0);

        await db.Core.ExecuteAsync("DROP TRIGGER test_fail_outbox ON opportunity.search_outbox");
        await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, w.Documents[0], true), Ct);
        (await OnlyOutboxRowAsync(db, w.Id)).SearchGeneration.Should().Be(1, "a rollback undoes the increment: no gap");
    }

    [Fact]
    public async Task A_bulk_coding_chunk_inserts_exactly_one_index_chunk_task_and_no_outbox_rows()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 50);
        var snapshot = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, 50), 50)], snapshotId: snapshot);

        var result = await db.Core.Coding.ApplyChunkAsync(chunk, BulkRequest(w, chunk, w.Documents), Ct);

        result.Committed.Should().BeTrue();
        result.Coding.EventsWritten.Should().Be(50);
        (await db.CountAsync("search_outbox", w.Id)).Should().Be(0, "bulk work never uses the per-document outbox (§21)");
        (await db.CountAsync("index_chunk_task", w.Id)).Should().Be(1);

        var task = (await db.Tasks.GetAsync(w.Id, result.IndexTaskId!.Value, Ct))!;
        task.JobId.Should().Be(chunk.Lease.JobId);
        task.ChunkId.Should().Be(chunk.Lease.ChunkId);
        task.Kind.Should().Be(IndexTaskKind.BulkCoding);
        task.Membership.Should().Be(chunk.Membership, "identifiers only: the worker resolves the chunk's documents");
        task.Lane.Should().Be(MessageLane.Bulk);
        task.ChangeMask.Should().Be(SearchChangeMask.Coding);
        task.SearchGeneration.Should().Be(1);
        task.Status.Should().Be(IndexChunkTaskStatus.Pending);
        task.IdempotencyKey.Should().Be(ChunkIdempotencyKey.ForChunk(w.Id, chunk.Lease.JobId, 1, ChunkOperationKind.IndexChunk, 0));

        var job = (await db.Jobs.GetAsync(w.Id, chunk.Lease.JobId, Ct))!;
        job.Status.Should().Be(JobStatus.Completed);
        job.Counters.ChunksCommitted.Should().Be(1);
        job.Counters.IndexTasksTotal.Should().Be(1);
        job.JobGeneration.Should().Be(1, "the generation of the job's last chunk task (ADR-001 §7.4)");
    }

    [Fact]
    public async Task A_bulk_chunk_is_audited_in_the_same_transaction_as_its_coding_task_and_commit_and_not_at_all_when_refused()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 4);
        var snapshot = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, 2), 2), new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 3, 4), 2)],
            snapshotId: snapshot);

        var result = await db.Core.Coding.ApplyChunkAsync(chunk,
            BulkRequest(w, chunk, w.Documents.Take(2)) with { Audit = ChunkAudit(w.Id, chunk) }, Ct);

        result.Committed.Should().BeTrue();
        var audit = (await CodingAuditAsync(db, w.Id)).Should().ContainSingle("one event per chunk").Subject;
        audit.Action.Should().Be(AuditTaxonomy.Coding.BulkChunkApplied);
        audit.JobId.Should().Be(chunk.Lease.JobId);
        audit.ChunkSequence.Should().Be(1);
        audit.Details.Should().Contain("Changed", "2");
        (await db.CountAsync("index_chunk_task", w.Id)).Should().Be(1);

        // The job pauses before the second chunk commits: F3 refuses, and coding, task and audit all roll back together.
        var second = (await db.Chunks.ClaimNextAsync(w.Id, chunk.Lease.JobId, "worker-1", TimeSpan.FromMinutes(1), Ct)).Chunk!;
        (await db.Jobs.PauseAsync(w.Id, chunk.Lease.JobId, "test", Ct)).Applied.Should().BeTrue();
        var refused = await db.Core.Coding.ApplyChunkAsync(second,
            BulkRequest(w, second, w.Documents.Skip(2)) with { Audit = ChunkAudit(w.Id, second) }, Ct);

        refused.Committed.Should().BeFalse();
        (await CodingAuditAsync(db, w.Id)).Should().ContainSingle();
        (await db.CountAsync("index_chunk_task", w.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_bulk_chunk_that_writes_a_security_field_goes_to_the_security_bulk_lane_and_one_that_changes_nothing_has_no_task()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 4);
        var snapshot = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, 2), 2), new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 3, 4), 2)],
            snapshotId: snapshot);

        var first = await db.Core.Coding.ApplyChunkAsync(chunk, BulkRequest(w, chunk, w.Documents.Take(2),
            CodingFieldOperation.Set(w.Privilege, JsonValue.Create(w.Privileged))), Ct);
        (await db.Tasks.GetAsync(w.Id, first.IndexTaskId!.Value, Ct))!.Lane.Should().Be(MessageLane.SecurityBulk);

        // The second chunk targets the same, already-coded documents: nothing changes, so nothing needs indexing.
        var second = (await db.Chunks.ClaimNextAsync(w.Id, chunk.Lease.JobId, "worker-1", TimeSpan.FromMinutes(1), Ct)).Chunk!;
        var noop = await db.Core.Coding.ApplyChunkAsync(second, BulkRequest(w, second, w.Documents.Take(2),
            CodingFieldOperation.Set(w.Privilege, JsonValue.Create(w.Privileged))), Ct);
        noop.Committed.Should().BeTrue();
        noop.IndexTaskId.Should().BeNull();
        (await db.CountAsync("index_chunk_task", w.Id)).Should().Be(1);
        (await db.CountAsync("search_outbox", w.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_chunk_refused_by_fence_F3_writes_neither_coding_nor_index_task()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 3);
        var snapshot = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, 3), 3)], snapshotId: snapshot);
        (await db.Jobs.PauseAsync(w.Id, chunk.Lease.JobId, "test", Ct)).Applied.Should().BeTrue();

        var result = await db.Core.Coding.ApplyChunkAsync(chunk, BulkRequest(w, chunk, w.Documents), Ct);

        result.Committed.Should().BeFalse();
        result.Commit!.Outcome.Should().Be(ChunkCommitOutcome.JobNotRunning);
        (await db.CountAsync("coding_event", w.Id)).Should().Be(0);
        (await db.CountAsync("index_chunk_task", w.Id)).Should().Be(0);
        (await db.CountAsync("workspace_search_generation", w.Id)).Should().Be(0);
        (await db.Jobs.GetChunksAsync(w.Id, chunk.Lease.JobId, cancellationToken: Ct)).Single().Status.Should().Be(JobChunkStatus.Pending);
    }

    [Fact]
    public async Task An_import_chunk_transaction_inserts_exactly_one_index_chunk_task_and_no_outbox_rows()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync();
        var batch = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.Import, ChunkOperationKind.ImportChunk,
            [new ChunkPlan(ChunkMembership.ImportRows(batch, 1, 500), 500)], importBatchId: batch);

        // The import executor's shape: the chunk's own writes, then F3 and the task, in one transaction.
        Guid? taskId;
        await using (var tx = await WorkspaceTransaction.BeginAsync(db.Core.AppDataSource, w.Id, Ct))
        {
            ChunkCommitResult commit;
            (commit, taskId) = await SearchWorkSql.CommitChunkAsync(tx, chunk.Lease, new ChunkCompletion { ItemsApplied = 500 },
                new NewIndexChunkTask
                {
                    JobId = chunk.Lease.JobId,
                    ChunkId = chunk.Lease.ChunkId,
                    Kind = IndexTaskKind.Import,
                    Membership = chunk.Membership,
                    ChangeMask = SearchChangeMask.Content | SearchChangeMask.Metadata,
                    IdempotencyKey = ChunkIdempotencyKey.ForChunk(w.Id, chunk.Lease.JobId, chunk.Sequence, ChunkOperationKind.IndexChunk, 0),
                }, Ct);
            commit.Committed.Should().BeTrue();
            await tx.CommitAsync(Ct);
        }

        (await db.CountAsync("search_outbox", w.Id)).Should().Be(0);
        (await db.CountAsync("index_chunk_task", w.Id)).Should().Be(1);
        var task = (await db.Tasks.GetAsync(w.Id, taskId!.Value, Ct))!;
        task.Kind.Should().Be(IndexTaskKind.Import);
        task.Membership.SnapshotId.Should().BeNull("import chunks have no snapshot (ADR-010 §4)");
        task.Membership.ImportBatchId.Should().Be(batch);
        task.SearchGeneration.Should().Be(1);
        (await db.Jobs.GetAsync(w.Id, chunk.Lease.JobId, Ct))!.Counters.IndexTasksTotal.Should().Be(1);
    }

    [Fact]
    public async Task Reindex_tasks_carry_no_search_generation_and_leave_the_counter_alone()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.Reindex, ChunkOperationKind.ReindexChunk,
            [new ChunkPlan(ChunkMembership.DocumentKeyRange(2, Guid.Empty, Guid.AllBitsSet), 0)]);

        await using var tx = await WorkspaceTransaction.BeginAsync(db.Core.AppDataSource, w.Id, Ct);
        var (taskId, generation) = await SearchWorkSql.AddIndexChunkTaskAsync(tx, new NewIndexChunkTask
        {
            JobId = chunk.Lease.JobId,
            ChunkId = chunk.Lease.ChunkId,
            Kind = IndexTaskKind.Reindex,
            Membership = chunk.Membership,
            ChangeMask = SearchChangeMask.Content | SearchChangeMask.Metadata | SearchChangeMask.Coding,
            IdempotencyKey = ChunkIdempotencyKey.ForChunk(w.Id, chunk.Lease.JobId, 1, ChunkOperationKind.ReindexChunk, 2),
        }, Ct);
        await tx.CommitAsync(Ct);

        generation.Should().BeNull();
        (await db.Tasks.GetAsync(w.Id, taskId, Ct))!.SearchGeneration.Should().BeNull();
        (await db.CountAsync("workspace_search_generation", w.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Committed_at_is_taken_at_the_end_of_the_transaction_not_at_its_start()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 1);

        await using (var tx = await WorkspaceTransaction.BeginAsync(db.Core.AppDataSource, w.Id, Ct))
        {
            // A long transaction: work happens between its start (created_at, the partition key) and its commit.
            await using (var sleep = tx.Command("SELECT pg_sleep(0.6)"))
            {
                await sleep.ExecuteNonQueryAsync(Ct);
            }

            await SearchWorkSql.AddOutboxRowsAsync(tx, [(w.Documents[0], 2)], SearchChangeMask.Coding, Ct);
            await tx.CommitAsync(Ct);
        }

        var afterCommit = await db.Core.ScalarAsync<DateTime>("SELECT clock_timestamp()");
        var (createdAt, committedAt) = await TimesAsync(db, w.Id);
        (committedAt - createdAt).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(600));
        committedAt.Should().BeOnOrBefore(afterCommit);
        (afterCommit - committedAt).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Generations_commit_in_order_a_later_transaction_waits_and_is_stamped_after_the_earlier_commit()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 2);

        await using var first = await WorkspaceTransaction.BeginAsync(db.Core.AppDataSource, w.Id, Ct);
        (await SearchWorkSql.AddOutboxRowsAsync(first, [(w.Documents[0], 2)], SearchChangeMask.Coding, Ct)).Should().Be(1);

        // The second transaction blocks on the counter row until the first commits.
        var second = Task.Run(async () =>
        {
            await using var tx = await WorkspaceTransaction.BeginAsync(db.Core.AppDataSource, w.Id, Ct);
            var generation = await SearchWorkSql.AddOutboxRowsAsync(tx, [(w.Documents[1], 2)], SearchChangeMask.Coding, Ct);
            await tx.CommitAsync(Ct);
            return generation;
        }, Ct);
        await Task.Delay(400, Ct);
        second.IsCompleted.Should().BeFalse("the late-lock counter serializes the commits of search-work transactions");
        await first.CommitAsync(Ct);
        var firstCommitted = await db.Core.ScalarAsync<DateTime>("SELECT clock_timestamp()");

        (await second).Should().Be(2);
        var rows = await RowsAsync(db, w.Id);
        rows[1].CommittedAt.Should().BeAfter(rows[0].CommittedAt);
        rows[1].CommittedAt.Should().BeOnOrAfter(firstCommitted.AddMilliseconds(-50), "it was stamped after the first commit");
    }

    [Fact]
    public async Task Concurrent_interactive_and_bulk_work_share_one_gap_free_commit_ordered_sequence()
    {
        await using var db = await SearchWorkDatabase.CreateAsync(postgres);
        var w = await db.WorkspaceAsync(documents: 40);
        var snapshot = Guid.CreateVersion7();
        var chunk = await db.RunningJobChunkAsync(w.Id, JobType.BulkCoding, ChunkOperationKind.BulkCodingChunk,
            [new ChunkPlan(ChunkMembership.SnapshotRange(snapshot, 1, 10), 10)], snapshotId: snapshot);

        var interactive = Parallel.ForEachAsync(w.Documents.Skip(10), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct },
            async (d, ct) => await db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, d, true), ct));
        var bulk = db.Core.Coding.ApplyChunkAsync(chunk, BulkRequest(w, chunk, w.Documents.Take(10)), Ct);
        await Task.WhenAll(interactive, bulk);

        var generations = await db.Core.ColumnAsync(
            """
            SELECT g::text FROM (
                SELECT search_generation AS g, committed_at FROM opportunity.search_outbox WHERE workspace_id = @ws
                UNION ALL
                SELECT search_generation, committed_at FROM opportunity.index_chunk_task WHERE workspace_id = @ws) w
            ORDER BY committed_at, g
            """.Replace("@ws", $"'{w.Id}'", StringComparison.Ordinal));
        generations.Select(long.Parse).Should().Equal(Enumerable.Range(1, 31).Select(i => (long)i),
            "one value per transaction, gap-free, and commit order equals generation order");
    }

    private static CodingWriteRequest BulkRequest(
        TestWorkspace w, ClaimedChunk chunk, IEnumerable<Guid> documents, params CodingFieldOperation[] operations) => new()
        {
            WorkspaceId = w.Id,
            IdempotencyKey = chunk.IdempotencyKey,
            Actor = new CodingActor(SearchWorkDatabase.Reviewer, CodingActorType.BulkHuman),
            JobId = chunk.Lease.JobId,
            Documents = [.. documents.Select(d => new CodingTarget(d, 1))],
            Operations = operations.Length > 0 ? operations : [CodingFieldOperation.Set(w.Responsive, JsonValue.Create(true))],
        };

    private static async Task<List<AuditEvent>> CodingAuditAsync(SearchWorkDatabase db, Guid ws) =>
        [.. (await AuditSamples.ReadAllAsync(db.Core, ws)).Select(e => e.Event).Where(e => e.Category == AuditTaxonomy.Coding.Category)];

    private static AuditEvent ChunkAudit(Guid ws, ClaimedChunk chunk) => new()
    {
        WorkspaceId = ws,
        OccurredAt = DateTimeOffset.UtcNow,
        Category = AuditTaxonomy.Coding.Category,
        Action = AuditTaxonomy.Coding.BulkChunkApplied,
        ActorType = AuditActorType.Service,
        ActorId = "worker:bulk-coding",
        ActorDisplay = "Bulk coding worker",
        OnBehalfOf = SearchWorkDatabase.Reviewer,
        Outcome = AuditOutcome.Success,
        CorrelationId = "corr-chunk",
        JobId = chunk.Lease.JobId,
        ChunkSequence = chunk.Sequence,
    };

    private static async Task<SearchOutboxRow> OnlyOutboxRowAsync(SearchWorkDatabase db, Guid ws)
    {
        var id = await db.Core.ScalarAsync<long>("SELECT outbox_id FROM opportunity.search_outbox WHERE workspace_id = @ws", ("ws", ws));
        return (await db.Outbox.GetAsync(ws, id, Ct))!;
    }

    private static async Task<List<SearchOutboxRow>> RowsAsync(SearchWorkDatabase db, Guid ws)
    {
        var ids = await db.Core.ColumnAsync(
            $"SELECT outbox_id::text FROM opportunity.search_outbox WHERE workspace_id = '{ws}' ORDER BY search_generation");
        var rows = new List<SearchOutboxRow>();
        foreach (var id in ids)
        {
            rows.Add((await db.Outbox.GetAsync(ws, long.Parse(id), Ct))!);
        }

        return rows;
    }

    private static async Task<(DateTime CreatedAt, DateTime CommittedAt)> TimesAsync(SearchWorkDatabase db, Guid ws)
    {
        await using var command = db.Core.DataSource.CreateCommand(
            "SELECT created_at, committed_at FROM opportunity.search_outbox WHERE workspace_id = @ws");
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetDateTime(0), reader.GetDateTime(1));
    }
}
