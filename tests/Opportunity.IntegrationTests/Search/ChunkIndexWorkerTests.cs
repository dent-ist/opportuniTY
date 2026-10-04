using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Fields;
using Opportunity.Application.Jobs;
using Opportunity.Application.SearchWork;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.SearchWork;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Writing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T04 acceptance against real PostgreSQL and OpenSearch: import chunks are indexed without a SnapshotId, duplicate
/// deliveries and crashes leave each task applied exactly once, partial bulk failures retry only the failed items,
/// bulk requests stay within the byte limit with 10 MB texts, Relationship tasks project documents outside the chunk,
/// and a stale chunk write never overwrites a newer interactive edit.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class ChunkIndexWorkerTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static readonly Guid Reviewer = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Dat(int count, string prefix = "DOC") =>
        ImportHarness.Utf8Bom(ImportHarness.Dat(
            [["BEGDOC", "CUSTODIAN"], .. Enumerable.Range(1, count).Select(i => new[] { $"{prefix}-{i:D6}", i % 2 == 0 ? "Smith" : "Doe" })]));

    private async Task<(ChunkIndexHarness H, Guid Ws, ImportBatchRecordInfo Batch)> ImportAsync(
        int documents, int rowsPerChunk, Action<Opportunity.Search.Workers.ChunkIndexWorkerOptions>? configure = null, ProjectionWriterOptions? writer = null)
    {
        var h = await ChunkIndexHarness.CreateAsync(openSearch, postgres, rowsPerChunk, configure, writer);
        var ws = await h.Import.WorkspaceAsync();
        var batch = await h.Import.StartAsync(ws, Dat(documents));
        var job = await h.Import.RunAsync(batch);
        job.Status.Should().Be(JobStatus.Completed);
        return (h, ws, new ImportBatchRecordInfo(batch.JobId, batch.ImportBatchId));
    }

    private sealed record ImportBatchRecordInfo(Guid JobId, Guid ImportBatchId);

    private static async Task<List<Guid>> DocumentIdsAsync(ChunkIndexHarness h, Guid ws) =>
        [.. (await h.Import.DocumentsAsync(ws)).Values.Select(d => d.DocumentId)];

    [Fact]
    public async Task A_10k_document_import_is_fully_indexed_without_a_snapshot_and_every_task_applied()
    {
        var (h, ws, batch) = await ImportAsync(10_000, 500);
        await using var harness = h;
        var tasks = await h.TasksAsync(ws, batch.JobId);
        tasks.Should().HaveCount(20).And.OnlyContain(t => t.Kind == IndexTaskKind.Import && t.Membership.Kind == ChunkMembershipKind.ImportRows
            && t.Membership.SnapshotId == null);

        await h.DeliverAllAsync(ws, batch.JobId, parallelism: 4);

        (await h.TasksAsync(ws, batch.JobId)).Should().OnlyContain(t => t.Status == IndexChunkTaskStatus.Applied && t.AttemptCount == 1);
        (await h.Import.CountAsync("SELECT index_tasks_applied FROM opportunity.job WHERE workspace_id = @ws", ws)).Should().Be(20);
        (await h.CountAsync(ws)).Should().Be(10_000);
        var ids = await DocumentIdsAsync(h, ws);
        (await h.DriftAsync(ws, [.. ids.Where((_, i) => i % 97 == 0)])).Should().BeEmpty();
        h.Hook.Bulks.Should().OnlyContain(b => b.Actions <= 500 && b.Bytes <= 10L * 1024 * 1024);
    }

    [Fact]
    public async Task Duplicate_deliveries_apply_a_task_once()
    {
        var (h, ws, batch) = await ImportAsync(30, 500);
        await using var harness = h;
        var task = (await h.TasksAsync(ws, batch.JobId)).Single();

        // Concurrent duplicates: one leases, the others find the lease held and ack; a late duplicate finds it settled.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => h.DeliverAsync(task)));
        await h.DeliverAsync(task);

        var settled = (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!;
        settled.Status.Should().Be(IndexChunkTaskStatus.Applied);
        settled.AttemptCount.Should().Be(1);
        (await h.Import.CountAsync("SELECT index_tasks_applied FROM opportunity.job WHERE workspace_id = @ws", ws)).Should().Be(1);
        h.Audit.Events.Should().BeEmpty();
        (await h.CountAsync(ws)).Should().Be(30);
    }

    [Fact]
    public async Task A_worker_that_crashes_mid_task_is_replaced_after_its_lease_expires()
    {
        var (h, ws, batch) = await ImportAsync(40, 500,
            o => { o.LeaseDuration = TimeSpan.FromSeconds(2); o.HeartbeatInterval = TimeSpan.FromSeconds(1); },
            new ProjectionWriterOptions { MaxRequestActions = 10 });
        await using var harness = h;
        var task = (await h.TasksAsync(ws, batch.JobId)).Single();

        // The process dies after its second bulk request: nothing more is written or recorded.
        h.Hook.OnBulk = (n, _, _) => n == 2 ? throw new SimulatedCrashException() : Task.FromResult<HttpResponseMessage?>(null);
        var crashing = h.Consumer(new CrashingTasks(h.Tasks));
        await FluentActions.Awaiting(() => h.DeliverAsync(task, crashing)).Should().ThrowAsync<SimulatedCrashException>();
        h.Hook.OnBulk = null;
        (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!.Status.Should().Be(IndexChunkTaskStatus.Running);
        (await h.CountAsync(ws)).Should().BeInRange(1, 39, "the crash happened mid-task");

        // An early redelivery finds the lease live and is dropped; after expiry another worker finishes the task.
        await h.DeliverAsync(task);
        (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!.Status.Should().Be(IndexChunkTaskStatus.Running);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        await h.DeliverAsync(task);

        var settled = (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!;
        settled.Status.Should().Be(IndexChunkTaskStatus.Applied);
        settled.AttemptCount.Should().Be(2);
        (await h.CountAsync(ws)).Should().Be(40);
        (await h.DriftAsync(ws, await DocumentIdsAsync(h, ws))).Should().BeEmpty();
    }

    [Fact]
    public async Task Partial_bulk_failures_retry_only_the_failed_items_and_complete_idempotently()
    {
        var (h, ws, batch) = await ImportAsync(12, 500);
        await using var harness = h;
        var task = (await h.TasksAsync(ws, batch.JobId)).Single();

        // The first request is applied by OpenSearch, but the client sees two items rejected with 429.
        h.Hook.OnBulk = async (n, _, response) =>
        {
            if (n != 1)
            {
                return null;
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
            foreach (var index in new[] { 3, 7 })
            {
                var item = body["items"]![index]!.AsObject().First().Value!.AsObject();
                item["status"] = 429;
                item["error"] = new JsonObject { ["type"] = "es_rejected_execution_exception", ["reason"] = "simulated" };
            }

            body["errors"] = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        };

        await h.DeliverAsync(task);

        var bulks = h.Hook.Bulks.ToList();
        bulks.Should().HaveCount(2);
        bulks[0].Actions.Should().Be(12);
        bulks[1].Actions.Should().Be(2, "only the two failed documents are re-read and re-sent");
        var settled = (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!;
        settled.Status.Should().Be(IndexChunkTaskStatus.Applied, "the retried items are 409 no-ops: OpenSearch had applied them");
        settled.AttemptCount.Should().Be(1);
        (await h.DriftAsync(ws, await DocumentIdsAsync(h, ws))).Should().BeEmpty();
    }

    [Fact]
    public async Task Bulk_requests_never_exceed_the_byte_limit_with_10mb_texts()
    {
        const long limit = 10L * 1024 * 1024;
        var (h, ws, batch) = await ImportAsync(8, 500, writer: new ProjectionWriterOptions { MaxRequestBytes = limit });
        await using var harness = h;
        var big = (await h.Import.DocumentsAsync(ws)).Where(d => d.Key is "DOC-000002" or "DOC-000005").Select(d => d.Value.DocumentId).ToHashSet();
        var tenMegabytes = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 10_000_000 / 18));
        h.Texts.Override = s => big.Contains(s.DocumentId) ? tenMegabytes : "short text " + s.DocumentId;

        await h.DeliverAsync((await h.TasksAsync(ws, batch.JobId)).Single());

        (await h.TasksAsync(ws, batch.JobId)).Single().Status.Should().Be(IndexChunkTaskStatus.Applied);
        var bulks = h.Hook.Bulks.ToList();
        bulks.Should().OnlyContain(b => b.Bytes <= limit || b.Actions == 1, "a request with more than one action stays within the limit");
        bulks.Count(b => b.Bytes > 9L * 1024 * 1024).Should().Be(2, "each 10 MB document travels alone");
        (await h.CountAsync(ws)).Should().Be(8);
        var indexed = (await h.IndexedAsync(ws, big.First()))!.Value;
        indexed.Source["textLength"]!.GetValue<int>().Should().BeGreaterThan(9_000_000, "text itself is stored outside _source");
    }

    [Fact]
    public async Task Relationship_tasks_index_documents_outside_the_chunk()
    {
        await using var h = await ChunkIndexHarness.CreateAsync(openSearch, postgres, rowsPerChunk: 1);
        var ws = await h.Import.WorkspaceAsync();
        // B sorts after A: when chunk 2 imports A into the group, B (chunk 1) stops being the primary.
        var batch = await h.Import.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "DuplicateGroupID"], ["B-0002", "G1"], ["A-0001", "G1"])));
        await h.Import.RunAsync(batch);
        var tasks = await h.TasksAsync(ws, batch.JobId);
        var relationship = tasks.Should().ContainSingle(t => t.Kind == IndexTaskKind.Relationship).Which;
        relationship.Membership.Kind.Should().Be(ChunkMembershipKind.ExplicitIds);

        // Relationship task first: it projects current state, whatever order the lanes deliver in.
        await h.DeliverAsync(relationship);
        await h.DeliverAllAsync(ws, batch.JobId);

        (await h.TasksAsync(ws, batch.JobId)).Should().HaveCount(3).And.OnlyContain(t => t.Status == IndexChunkTaskStatus.Applied);
        var docs = await h.Import.DocumentsAsync(ws);
        (await h.IndexedAsync(ws, docs["A-0001"].DocumentId))!.Value.Source["isDuplicatePrimary"]!.GetValue<bool>().Should().BeTrue();
        var b = (await h.IndexedAsync(ws, docs["B-0002"].DocumentId))!.Value;
        b.Source["isDuplicatePrimary"]!.GetValue<bool>().Should().BeFalse();
        b.Version.Should().Be(docs["B-0002"].Version).And.BeGreaterThan(1);
        (await h.DriftAsync(ws, [.. docs.Values.Select(d => d.DocumentId)])).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_chunk_write_never_overwrites_a_newer_interactive_edit()
    {
        var (h, ws, batch) = await ImportAsync(5, 500);
        await using var harness = h;
        var notes = await NotesFieldAsync(h, ws);
        var target = (await h.Import.DocumentsAsync(ws))["DOC-000003"].DocumentId;
        var task = (await h.TasksAsync(ws, batch.JobId)).Single();

        // Fault injection: the chunk worker stalls after its PostgreSQL read of the target (version 1).
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Texts.Gate = async (source, ct) =>
        {
            if (source.DocumentId == target)
            {
                read.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var chunk = h.DeliverAsync(task);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Meanwhile a reviewer edits the document and the interactive path indexes version 2.
        h.Texts.Gate = null;
        await EditAsync(h, ws, target, notes, "interactive edit");
        var interactive = await h.Projections.BuildAsync(ws, [target], Ct);
        (await h.Writer.WriteAsync(ws, interactive, Ct)).Documents.Single().Status.Should().Be(ProjectionWriteStatus.Applied);

        release.SetResult();
        await chunk;

        (await h.Tasks.GetAsync(ws, task.TaskId, Ct))!.Status.Should().Be(IndexChunkTaskStatus.Applied, "a 409 is a success no-op");
        var indexed = (await h.IndexedAsync(ws, target))!.Value;
        indexed.Version.Should().Be(2);
        indexed.Source.ToJsonString().Should().Contain("interactive edit");
        (await h.DriftAsync(ws, await DocumentIdsAsync(h, ws))).Should().BeEmpty();
    }

    [Fact]
    public async Task Chunk_tasks_running_alongside_interactive_writes_leave_every_document_at_its_latest_version()
    {
        var (h, ws, batch) = await ImportAsync(200, 25, writer: new ProjectionWriterOptions { MaxRequestActions = 10 });
        await using var harness = h;
        var notes = await NotesFieldAsync(h, ws);
        var ids = await DocumentIdsAsync(h, ws);

        // Small random stalls between read and write widen every race window.
        h.Texts.Gate = (_, ct) => Random.Shared.Next(4) == 0 ? Task.Delay(Random.Shared.Next(1, 15), ct) : Task.CompletedTask;
        using var stop = new CancellationTokenSource();
        var edits = 0;
        var reviewers = Enumerable.Range(0, 3).Select(r => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var doc = ids[Random.Shared.Next(ids.Count)];
                await EditAsync(h, ws, doc, notes, $"edit {Interlocked.Increment(ref edits)} by {r}");
                await h.Writer.WriteAsync(ws, await h.Projections.BuildAsync(ws, [doc], Ct), Ct);
            }
        }, Ct)).ToList();

        await h.DeliverAllAsync(ws, batch.JobId, parallelism: 4);
        await stop.CancelAsync();
        await Task.WhenAll(reviewers);

        edits.Should().BeGreaterThan(0);
        (await h.TasksAsync(ws, batch.JobId)).Should().HaveCount(8).And.OnlyContain(t => t.Status == IndexChunkTaskStatus.Applied);
        (await h.DriftAsync(ws, ids)).Should().BeEmpty("the index holds every document's latest PostgreSQL version and state");
    }

    private static async Task<int> NotesFieldAsync(ChunkIndexHarness h, Guid ws) =>
        (await h.Import.Db.Fields.CreateFieldAsync(new NewField(ws, "Review Notes", FieldType.Text, FieldStorage.Coding), Ct)).Value!.FieldId;

    private static async Task EditAsync(ChunkIndexHarness h, Guid ws, Guid documentId, int field, string value)
    {
        var result = await h.Import.Db.Coding.ApplyAsync(new CodingWriteRequest
        {
            WorkspaceId = ws,
            IdempotencyKey = "ui-" + Guid.CreateVersion7().ToString("N"),
            Actor = new CodingActor(Reviewer, CodingActorType.Human),
            Documents = [new CodingTarget(documentId)],
            Operations = [CodingFieldOperation.Set(field, JsonValue.Create(value))],
        }, Ct);
        result.Outcome.Should().Be(CodingWriteOutcome.Applied);
    }

    private sealed class SimulatedCrashException : Exception
    {
        public SimulatedCrashException()
            : base("simulated process crash")
        {
        }
    }

    /// <summary>A dead process records nothing: every outcome write fails once the work stopped.</summary>
    private sealed class CrashingTasks(IIndexChunkTaskRepository inner) : IIndexChunkTaskRepository
    {
        public Task<IReadOnlyList<ClaimedIndexTask>> ClaimForDispatchAsync(Guid workspaceId, string owner, int limit, TimeSpan claimDuration, CancellationToken cancellationToken = default) =>
            inner.ClaimForDispatchAsync(workspaceId, owner, limit, claimDuration, cancellationToken);

        public Task<int> MarkDispatchedAsync(Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default) =>
            inner.MarkDispatchedAsync(workspaceId, owner, taskIds, cancellationToken);

        public Task<int> ReleaseClaimAsync(Guid workspaceId, string owner, IReadOnlyCollection<Guid> taskIds, TimeSpan retryAfter, CancellationToken cancellationToken = default) =>
            inner.ReleaseClaimAsync(workspaceId, owner, taskIds, retryAfter, cancellationToken);

        public Task<IndexTaskLeaseResult> LeaseAsync(Guid workspaceId, Guid taskId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            inner.LeaseAsync(workspaceId, taskId, workerId, leaseDuration, cancellationToken);

        public Task<IndexTaskRenewal> RenewLeaseAsync(IndexTaskLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            inner.RenewLeaseAsync(lease, leaseDuration, cancellationToken);

        public Task<bool> CompleteAsync(IndexTaskLease lease, CancellationToken cancellationToken = default) => throw new SimulatedCrashException();

        public Task<IndexTaskFailureResult> FailAsync(IndexTaskLease lease, ChunkError failure, CancellationToken cancellationToken = default) =>
            throw new SimulatedCrashException();

        public Task<bool> ReleaseAsync(IndexTaskLease lease, CancellationToken cancellationToken = default) => throw new SimulatedCrashException();

        public Task<IndexChunkTaskInfo?> GetAsync(Guid workspaceId, Guid taskId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(workspaceId, taskId, cancellationToken);

        public Task<IReadOnlyList<IndexChunkTaskInfo>> GetByJobAsync(Guid workspaceId, Guid jobId, CancellationToken cancellationToken = default) =>
            inner.GetByJobAsync(workspaceId, jobId, cancellationToken);
    }
}
