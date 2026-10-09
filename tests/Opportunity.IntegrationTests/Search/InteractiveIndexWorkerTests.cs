using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Messaging;
using Opportunity.Application.Search.Projection;
using Opportunity.Contracts.Messaging.Indexing;
using Opportunity.Core.SearchWork;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.IntegrationTests.SearchWork;
using Opportunity.Search.Projection;
using Opportunity.Search.Workers;
using Opportunity.Search.Writing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E07-T03 against real PostgreSQL and OpenSearch: the version-safe interactive index worker and the shared projection
/// writer (ADR-001 §3–§5). Messages come from the real relay; the tests choose their order, delay and duplication.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class InteractiveIndexWorkerTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private const string StaleRejections = "opportunity.search.stale_version_rejections";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delivering_v5_then_a_delayed_v3_leaves_v5()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: 1);
        var doc = w.Documents[0];
        await CodeAsync(h, w, doc, true);
        await CodeAsync(h, w, doc, false);
        var v3 = await BuildAsync(h, w.Id, doc);
        await CodeAsync(h, w, doc, true);
        await CodeAsync(h, w, doc, false);
        var v5 = await BuildAsync(h, w.Id, doc);
        v3.Version.Should().Be(3);
        v5.Version.Should().Be(5);

        (await WriteAsync(h, w.Id, v5)).Status.Should().Be(ProjectionWriteStatus.Applied);
        var late = await WriteAsync(h, w.Id, v3);

        late.Status.Should().Be(ProjectionWriteStatus.StaleNoOp);
        late.Succeeded.Should().BeTrue("a newer version already in the index is success (ADR-001 §3)");
        var stored = (await h.GetAsync(w.Id, doc))!.Value;
        stored.Version.Should().Be(5);
        JsonNode.DeepEquals(stored.Source, v5.Writes[0].Body).Should().BeTrue("the index holds P(5)");
    }

    /// <summary>
    /// A worker wrote version 2 and died before marking its row: the redelivered message rewrites version 2, gets a
    /// 409, counts it on <c>opportunity.search.stale_version_rejections{lane}</c> and marks the row Applied.
    /// </summary>
    [Fact]
    public async Task A_redelivered_message_whose_version_is_already_indexed_is_a_counted_no_op_and_marks_the_row_applied()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: 1);
        var doc = w.Documents[0];
        await CodeAsync(h, w, doc, true);
        var message = (await h.DispatchAsync(w.Id)).Single();
        (await WriteAsync(h, w.Id, await BuildAsync(h, w.Id, doc))).Status.Should().Be(ProjectionWriteStatus.Applied);

        await h.HandleAsync(message);

        (await h.Db.Outbox.GetAsync(w.Id, ((SearchOutboxMessage)message.Payload).OutboxId, Ct))!.Status.Should().Be(SearchOutboxStatus.Applied);
        h.Meters.Sum(StaleRejections, "interactive").Should().Be(1);
        (await h.GetAsync(w.Id, doc))!.Value.Version.Should().Be(2);
    }

    /// <summary>
    /// The E07 exit criterion: 10,000 versioned writes (index and delete) of 200 documents, every version of every
    /// document, plus random duplicates, sent in a random order in random-sized bulk requests. A shadow ledger of the
    /// highest version OpenSearch accepted per document flags any accepted write that is not newer: there must be none,
    /// and every document must end at its highest version (or deleted, when that version is a delete).
    /// </summary>
    [Fact]
    public async Task Ten_thousand_randomized_reorderings_and_duplicates_cause_no_stale_version_overwrite()
    {
        const int Documents = 200;
        const int Versions = 50;
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: Documents);
        var live = await h.Projections.BuildAsync(w.Id, w.Documents, Ct);
        var seed = Random.Shared.Next();
        TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}");
        var random = new Random(seed);

        var deletedAtEnd = w.Documents.Where((_, i) => i % 4 == 0).ToHashSet();
        var writes = new List<ProjectionDocument>();
        foreach (var document in live)
        {
            for (var v = 1; v <= Versions; v++)
            {
                writes.Add(Synthesize(document, v, delete: v == Versions && deletedAtEnd.Contains(document.DocumentId)));
            }
        }

        writes.AddRange([.. writes.Where(_ => random.NextDouble() < 0.1)]);
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(writes));

        var ledger = new Dictionary<Guid, long>();
        var staleOverwrites = 0;
        var conflicts = 0;
        for (var offset = 0; offset < writes.Count;)
        {
            var size = Math.Min(random.Next(1, 500), writes.Count - offset);
            var batch = writes.GetRange(offset, size);
            offset += size;
            var result = await h.Writer.WriteAsync(w.Id, batch, Ct);
            foreach (var item in result.Documents)
            {
                item.Succeeded.Should().BeTrue(item.Error);
                if (item.Status == ProjectionWriteStatus.StaleNoOp)
                {
                    conflicts++;
                    continue;
                }

                if (item.Version <= ledger.GetValueOrDefault(item.DocumentId))
                {
                    staleOverwrites++;
                }

                ledger[item.DocumentId] = Math.Max(ledger.GetValueOrDefault(item.DocumentId), item.Version!.Value);
            }
        }

        writes.Count.Should().BeGreaterThanOrEqualTo(10_000);
        staleOverwrites.Should().Be(0);
        foreach (var document in live)
        {
            var stored = await h.GetAsync(w.Id, document.DocumentId);
            if (deletedAtEnd.Contains(document.DocumentId))
            {
                stored.Should().BeNull("the highest version is a delete, and lower writes must not resurrect it");
                continue;
            }

            stored!.Value.Version.Should().Be(Versions);
            stored.Value.Source["projectionVersion"]!.GetValue<long>().Should().Be(Versions);
        }

        conflicts.Should().BeGreaterThan(writes.Count / 2, "most writes in a random order arrive after a newer version");
    }

    /// <summary>
    /// The whole worker under concurrent coding: each round codes documents (and deletes some) while the previous
    /// round's messages, plus random late duplicates of older ones, are delivered shuffled to 8 concurrent handlers.
    /// At the end every outbox row is Applied and the index equals the current PostgreSQL projection.
    /// </summary>
    [Fact]
    public async Task Reordered_duplicated_and_concurrent_deliveries_converge_on_the_current_postgres_state()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: 30);
        var seed = Random.Shared.Next();
        TestContext.Current.TestOutputHelper?.WriteLine($"seed {seed}");
        var random = new Random(seed);
        var values = w.Documents.ToDictionary(d => d, _ => false);
        var deleted = new HashSet<Guid>();
        var delivered = new List<ReceivedMessage>();

        async Task CodeRoundAsync(int round)
        {
            foreach (var doc in w.Documents.Where(d => !deleted.Contains(d)).Where(_ => random.NextDouble() < 0.6).ToList())
            {
                values[doc] = !values[doc];
                await CodeAsync(h, w, doc, values[doc]);
            }

            if (round == 4)
            {
                foreach (var doc in w.Documents.Take(5))
                {
                    deleted.Add(doc);
                    await h.SoftDeleteAsync(w.Id, doc);
                }
            }
        }

        async Task DeliverAsync(IReadOnlyList<ReceivedMessage> messages)
        {
            await Parallel.ForEachAsync(messages, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct },
                async (message, ct) => await h.HandleUntilAsync(message, ct));
        }

        await CodeRoundAsync(0);
        for (var round = 1; round <= 8; round++)
        {
            var current = await h.DispatchAsync(w.Id);
            var late = delivered.Where(_ => random.NextDouble() < 0.2).ToList();
            delivered.AddRange(current);
            var deliveries = current.Concat(current.Where(_ => random.NextDouble() < 0.3)).Concat(late).ToList();
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(deliveries));
            await Task.WhenAll(CodeRoundAsync(round), DeliverAsync(deliveries));
        }

        await DeliverAsync(await h.DispatchAsync(w.Id));
        await DeliverAsync([.. delivered.Where(_ => random.NextDouble() < 0.5)]);

        var statuses = await h.Db.OutboxStatusesAsync(w.Id);
        statuses.Where(s => s.Key != SearchOutboxStatus.Applied).Sum(s => s.Value).Should().Be(0);
        statuses[SearchOutboxStatus.Applied].Should().BeGreaterThan(30);
        await AssertIndexMatchesPostgresAsync(h, w.Id, w.Documents);
    }

    [Fact]
    public async Task Applying_a_newer_row_coalesces_older_ones_and_late_duplicates_write_nothing()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: 1);
        var doc = w.Documents[0];
        await CodeAsync(h, w, doc, true);
        await CodeAsync(h, w, doc, false);
        var messages = (await h.DispatchAsync(w.Id)).OrderBy(m => ((SearchOutboxMessage)m.Payload).DocumentVersion).ToList();
        messages.Should().HaveCount(2);

        await h.HandleAsync(messages[1]);

        foreach (var message in messages)
        {
            (await h.Db.Outbox.GetAsync(w.Id, ((SearchOutboxMessage)message.Payload).OutboxId, Ct))!.Status.Should().Be(SearchOutboxStatus.Applied);
        }

        await h.HandleAsync(messages[0]);
        await h.HandleAsync(messages[1]);
        h.Meters.Sum(StaleRejections).Should().Be(0, "duplicates of applied rows are dropped before any write");
        (await h.GetAsync(w.Id, doc))!.Value.Version.Should().Be(3);
    }

    [Fact]
    public async Task A_document_without_a_postgres_row_is_deleted_unconditionally()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync();
        await h.Db.AddOutboxRowsAsync(w.Id, 1, SearchChangeMask.Content);
        var message = (await h.DispatchAsync(w.Id)).Single();
        var payload = (SearchOutboxMessage)message.Payload;
        var target = await h.TargetAsync(w.Id);
        using (var stray = await h.Http.PutAsync(
            $"{target.Index}/_doc/{payload.DocumentId:D}?version=7&version_type=external" + (target.Routing is { } r ? $"&routing={r}" : string.Empty),
            new StringContent(new JsonObject { ["workspaceId"] = w.Id.ToString("D"), ["documentId"] = payload.DocumentId.ToString("D") }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json"),
            Ct))
        {
            stray.EnsureSuccessStatusCode();
        }

        await h.HandleAsync(message);

        (await h.GetAsync(w.Id, payload.DocumentId)).Should().BeNull("a missing row is a delete, never a skip (ADR-001 §4 R1)");
        (await h.Db.Outbox.GetAsync(w.Id, payload.OutboxId, Ct))!.Status.Should().Be(SearchOutboxStatus.Applied);
    }

    [Fact]
    public async Task A_stale_write_inside_gc_deletes_is_rejected_by_the_delete_tombstone()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: 1);
        var doc = w.Documents[0];
        await CodeAsync(h, w, doc, true);
        foreach (var message in await h.DispatchAsync(w.Id))
        {
            await h.HandleAsync(message);
        }

        var stale = await BuildAsync(h, w.Id, doc);
        await h.SoftDeleteAsync(w.Id, doc);
        foreach (var message in await h.DispatchAsync(w.Id))
        {
            await h.HandleAsync(message);
        }

        (await h.GetAsync(w.Id, doc)).Should().BeNull();
        (await WriteAsync(h, w.Id, stale)).Status.Should().Be(ProjectionWriteStatus.StaleNoOp);
        (await h.GetAsync(w.Id, doc)).Should().BeNull("the tombstone (version 3) outranks the stale version 2");
    }

    /// <summary>
    /// ADR-001 §4 R2–R3 with <c>gc_deletes</c> shortened to 1 s: a retried task that read version 3 stalls; meanwhile the
    /// document is deleted (version 4) and the delete is applied; the stall outlives the tombstone. The read-to-write
    /// bound (1 s here) stops the stale write: the worker reads PostgreSQL again, sees the delete and the document stays
    /// gone.
    /// </summary>
    [Fact]
    public async Task A_delete_followed_by_a_delayed_retried_task_does_not_resurrect_the_document_beyond_gc_deletes()
    {
        StallingProjections? stalling = null;
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres,
            worker: o => o.MaxReadToWriteAge = TimeSpan.FromSeconds(1),
            configure: s => s.Replace(ServiceDescriptor.Singleton<IProjectionService>(sp => stalling = new StallingProjections(new ProjectionService(
                sp.GetRequiredService<IProjectionSourceReader>(), sp.GetRequiredService<IProjectionTextLoader>(), sp.GetRequiredService<IProjectionBuilder>())))));
        var w = await h.Db.WorkspaceAsync(documents: 1);
        var doc = w.Documents[0];
        await CodeAsync(h, w, doc, true);
        foreach (var message in await h.DispatchAsync(w.Id))
        {
            await h.HandleAsync(message);
        }

        await h.PutSettingsAsync(w.Id, new JsonObject { ["gc_deletes"] = "1s" });
        await CodeAsync(h, w, doc, false);
        var retried = (await h.DispatchAsync(w.Id)).Single();
        stalling!.StallNext();
        var stalled = h.HandleAsync(retried);
        await stalling.Stalled.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        (await h.SoftDeleteAsync(w.Id, doc)).Should().Be(4);
        foreach (var message in await h.DispatchAsync(w.Id))
        {
            await h.HandleAsync(message);
        }

        (await h.GetAsync(w.Id, doc)).Should().BeNull();
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        await h.RefreshAsync(w.Id);
        stalling.Release();
        await stalled.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        stalling.Reads.Should().Equal(
            [(ProjectionSourceState.Live, 2L), (ProjectionSourceState.Live, 3L), (ProjectionSourceState.Deleted, 4L), (ProjectionSourceState.Deleted, 4L)],
            "the stalled read aged out and was read again instead of being written");
        (await h.GetAsync(w.Id, doc)).Should().BeNull("the document must not come back");
        (await h.Db.OutboxStatusesAsync(w.Id))[SearchOutboxStatus.Applied].Should().Be(3);
    }

    [Fact]
    public async Task Failed_writes_keep_their_retry_state_in_postgres()
    {
        var forced = new ForcedOutcome();
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres,
            configure: s => s.Replace(ServiceDescriptor.Singleton<IProjectionIndexWriter>(sp =>
                new ForcingWriter(ActivatorUtilities.CreateInstance<ProjectionIndexWriter>(sp), forced))));
        var w = await h.Db.WorkspaceAsync(documents: 2);
        foreach (var doc in w.Documents)
        {
            await CodeAsync(h, w, doc, true);
        }

        var messages = (await h.DispatchAsync(w.Id)).ToDictionary(m => ((SearchOutboxMessage)m.Payload).DocumentId);
        long OutboxId(Guid doc) => ((SearchOutboxMessage)messages[doc].Payload).OutboxId;

        forced.Outcome = ProjectionWriteStatus.Transient;
        await h.HandleAsync(messages[w.Documents[0]]);
        forced.Outcome = ProjectionWriteStatus.Permanent;
        await h.HandleAsync(messages[w.Documents[1]]);

        var transient = (await h.Db.Outbox.GetAsync(w.Id, OutboxId(w.Documents[0]), Ct))!;
        transient.Status.Should().Be(SearchOutboxStatus.Pending);
        transient.LastError.Should().Contain("forced");
        (await h.Db.Outbox.GetAsync(w.Id, OutboxId(w.Documents[1]), Ct))!.Status.Should().Be(SearchOutboxStatus.Failed);

        // The dispatcher offers the transient rows again after their backoff; this time the write goes through.
        forced.Outcome = null;
        await Task.Delay(SearchOutboxRetryPolicy.Backoff(1) + TimeSpan.FromMilliseconds(200), Ct);
        var retried = await h.DispatchAsync(w.Id);
        retried.Select(m => ((SearchOutboxMessage)m.Payload).DocumentId).Should().Equal(w.Documents[0]);
        foreach (var message in retried)
        {
            await h.HandleAsync(message);
        }

        (await h.Db.Outbox.GetAsync(w.Id, OutboxId(w.Documents[0]), Ct))!.Status.Should().Be(SearchOutboxStatus.Applied);
        (await h.GetAsync(w.Id, w.Documents[0]))!.Value.Version.Should().Be(2);
    }

    [Fact]
    public async Task A_read_that_always_ages_past_the_bound_is_never_written_and_the_row_is_retried()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres, worker: o => o.MaxReadToWriteAge = TimeSpan.FromTicks(1));
        var w = await h.Db.WorkspaceAsync(documents: 1);
        await CodeAsync(h, w, w.Documents[0], true);
        var message = (await h.DispatchAsync(w.Id)).Single();

        await h.HandleAsync(message);

        var row = (await h.Db.Outbox.GetAsync(w.Id, ((SearchOutboxMessage)message.Payload).OutboxId, Ct))!;
        row.Status.Should().Be(SearchOutboxStatus.Pending);
        row.LastError.Should().Contain("read-to-write");
        (await h.GetAsync(w.Id, w.Documents[0])).Should().BeNull();
    }

    [Fact]
    public async Task Work_of_a_workspace_that_is_not_active_is_dropped_unwritten()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: 1);
        await CodeAsync(h, w, w.Documents[0], true);
        var message = (await h.DispatchAsync(w.Id)).Single();
        // Placed first: a workspace being deleted gets no new placement (E20-T02 fence).
        await h.TargetAsync(w.Id);
        await h.Db.Core.ExecuteAsync("UPDATE opportunity.workspace SET status = 'Deleting', closed_at = now() WHERE workspace_id = @ws", ("ws", w.Id));

        await h.HandleAsync(message);

        (await h.Db.Outbox.GetAsync(w.Id, ((SearchOutboxMessage)message.Payload).OutboxId, Ct))!.Status.Should().Be(SearchOutboxStatus.Dispatched);
        (await h.GetAsync(w.Id, w.Documents[0])).Should().BeNull();
    }

    [Fact]
    public async Task Unknown_rows_are_dropped_and_messages_without_a_workspace_are_dead_lettered()
    {
        await using var h = await IndexWorkerHarness.CreateAsync(openSearch, postgres);
        var w = await h.Db.WorkspaceAsync(documents: 1);

        await h.HandleAsync(IndexWorkerHarness.Message(w.Id, 987_654, w.Documents[0], 1));
        var orphan = () => h.HandleAsync(IndexWorkerHarness.Message(null, 1, w.Documents[0], 1));

        await orphan.Should().ThrowAsync<PermanentMessageException>();
        (await h.GetAsync(w.Id, w.Documents[0])).Should().BeNull();
    }

    internal static Task CodeAsync(IndexWorkerHarness h, TestWorkspace w, Guid doc, bool value) =>
        h.Db.Core.Coding.ApplyAsync(SearchWorkDatabase.SetResponsive(w, doc, value), Ct);

    /// <summary>Every document's stored state equals a fresh build from PostgreSQL (absent when deleted).</summary>
    internal static async Task AssertIndexMatchesPostgresAsync(IndexWorkerHarness h, Guid workspaceId, IReadOnlyList<Guid> documents)
    {
        var expected = await h.Projections.BuildAsync(workspaceId, documents, Ct);
        foreach (var document in expected)
        {
            var stored = await h.GetAsync(workspaceId, document.DocumentId);
            var write = document.Writes.Single();
            if (write.Kind != ProjectionWriteKind.Index)
            {
                stored.Should().BeNull("document {0} is deleted in PostgreSQL", document.DocumentId);
                continue;
            }

            stored.Should().NotBeNull("document {0} is live in PostgreSQL", document.DocumentId);
            stored!.Value.Version.Should().Be(document.Version!.Value);
            JsonNode.DeepEquals(stored.Value.Source, write.Body).Should().BeTrue("document {0} holds P({1})", document.DocumentId, document.Version);
        }
    }

    private static async Task<ProjectionDocument> BuildAsync(IndexWorkerHarness h, Guid workspaceId, Guid doc) =>
        (await h.Projections.BuildAsync(workspaceId, [doc], Ct)).Single();

    private static async Task<ProjectionWriteResult> WriteAsync(IndexWorkerHarness h, Guid workspaceId, ProjectionDocument document) =>
        (await h.Writer.WriteAsync(workspaceId, [document], Ct)).Documents.Single();

    /// <summary>Version <paramref name="version"/> of a built projection: its body with that projectionVersion, or a delete.</summary>
    private static ProjectionDocument Synthesize(ProjectionDocument built, long version, bool delete)
    {
        var id = built.DocumentId.ToString("D");
        if (delete)
        {
            return built with { Version = version, Writes = [new ProjectionWrite(id, ProjectionWriteKind.Delete, version, null)] };
        }

        var body = built.Writes.Single().Body!.DeepClone().AsObject();
        body["projectionVersion"] = version;
        return built with { Version = version, Writes = [new ProjectionWrite(id, ProjectionWriteKind.Index, version, body)] };
    }

    /// <summary>Lets one build read PostgreSQL and then hold its result until released (a stalled worker).</summary>
    private sealed class StallingProjections(IProjectionService inner) : IProjectionService
    {
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stallNext;

        public int Generation => inner.Generation;

        public Task Stalled => _stalled.Task;

        public List<(ProjectionSourceState State, long Version)> Reads { get; } = [];

        public void StallNext() => Volatile.Write(ref _stallNext, 1);

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<ProjectionDocument>> BuildAsync(
            Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
        {
            var documents = await inner.BuildAsync(workspaceId, documentIds, cancellationToken);
            lock (Reads)
            {
                Reads.AddRange(documents.Select(d => (
                    d.Writes[0].Kind == ProjectionWriteKind.Index ? ProjectionSourceState.Live : ProjectionSourceState.Deleted, d.Version ?? 0)));
            }

            if (Interlocked.Exchange(ref _stallNext, 0) == 1)
            {
                _stalled.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return documents;
        }

        public IAsyncEnumerable<ProjectionDocument> BuildEachAsync(
            Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default) =>
            inner.BuildEachAsync(workspaceId, documentIds, cancellationToken);
    }

    private sealed class ForcedOutcome
    {
        public ProjectionWriteStatus? Outcome { get; set; }
    }

    /// <summary>Reports <see cref="ForcedOutcome.Outcome"/> instead of writing, when one is set.</summary>
    private sealed class ForcingWriter(IProjectionIndexWriter inner, ForcedOutcome forced) : IProjectionIndexWriter
    {
        public ProjectionWriterOptions Options => inner.Options;

        public Task<ProjectionWriteReport> WriteAsync(
            Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, ProjectionWriteScope scope, CancellationToken cancellationToken = default) =>
            WriteAsync(workspaceId, documents, cancellationToken);

        public Task<ProjectionWriteReport> WriteAsync(Guid workspaceId, IReadOnlyList<ProjectionDocument> documents, CancellationToken cancellationToken = default) =>
            forced.Outcome is { } outcome
                ? Task.FromResult(new ProjectionWriteReport(
                    [.. documents.Select(d => new ProjectionWriteResult(d.DocumentId, d.Version, outcome, "forced"))], false, []))
                : inner.WriteAsync(workspaceId, documents, cancellationToken);
    }
}
