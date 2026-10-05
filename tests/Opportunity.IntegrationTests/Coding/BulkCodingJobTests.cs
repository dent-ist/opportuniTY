using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Core.Coding;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.SearchWork;
using Opportunity.Data.SearchWork;
using Opportunity.IntegrationTests.Migrations;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif

namespace Opportunity.IntegrationTests.Coding;

/// <summary>
/// E10-T04 acceptance against PostgreSQL: a bulk coding job over a frozen snapshot (ADR-002) runs in chunks through the
/// idempotent consumer; interactive edits made after the snapshot are skipped and reported, never overwritten (Q-07);
/// a crash and redelivery never applies a chunk twice (ADR-010); losing Coding.Bulk stops the remaining chunks and a
/// document the initiator can no longer see is skipped as hidden (ADR-015 D9.4); security-affecting bulk coding needs
/// Coding.WritePrivilege and recomputes restriction classes per document. Searchability is covered by
/// <see cref="BulkCodingSearchabilityTests"/>.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class BulkCodingJobTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CodingChange Set(int fieldId, JsonNode? value) => new(fieldId, CodingOperationKind.Set, value);

    private static CodingChange Add(int fieldId, params int[] choices) =>
        new(fieldId, CodingOperationKind.AddChoices, new JsonArray([.. choices.Select(c => (JsonNode)JsonValue.Create(c))]));

    [Fact]
    public async Task A_job_codes_every_member_in_chunks_with_one_index_task_per_chunk_and_job_level_audit()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var user = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var docs = await h.DocumentsAsync(w.Id, 2_500);
        var snapshot = await h.SnapshotAsync(w.Id, user, docs);

        var job = await h.StartAsync(w.Id, user, snapshot.SnapshotId, Set(w.Responsive, true), Add(w.Issues, w.IssueA));
        job.Status.Should().Be(JobStatus.Running);
        job.JobType.Should().Be(JobType.BulkCoding);
        job.TargetSnapshotId.Should().Be(snapshot.SnapshotId);
        job.Counters.ChunksTotal.Should().Be(3, "1,000 members per chunk (ADR-010 §6)");
        (await h.ChunksAsync(w.Id, job.JobId)).Select(c => (c.Membership.RangeFrom, c.Membership.RangeTo))
            .Should().Equal((1L, 1_000L), (1_001L, 2_000L), (2_001L, 2_500L));

        var done = await h.RunAsync(w.Id, job.JobId);
        done.Status.Should().Be(JobStatus.Completed);
        done.Counters.ItemsApplied.Should().Be(2_500);
        done.Counters.IndexTasksTotal.Should().Be(3);

        (await h.ValuesAsync(w.Id, docs, w.Responsive)).Values.Should().HaveCount(2_500).And.OnlyContain(v => v!.GetValue<bool>());
        (await h.ValuesAsync(w.Id, docs, w.Issues)).Values.Select(v => v!.ToJsonString()).Should().OnlyContain(v => v == $"[{w.IssueA}]");
        var events = await h.EventsAsync(w.Id, job.JobId);
        events.Should().HaveCount(5_000).And.OnlyContain(e => e.Kind == CodingEventKind.ValueChanged);
        events.Select(e => (e.DocumentId, e.FieldId)).Distinct().Should().HaveCount(5_000, "one CodingEvent per changed field");
        (await h.Coding.GetEventsAsync(new CodingEventQuery(w.Id) { JobId = job.JobId, Limit = 1 }, Ct)).Events.Single().ActorType
            .Should().Be(CodingActorType.BulkHuman);

        // One IndexChunkTask per chunk, naming the chunk's snapshot range, which resolves to exactly its members.
        var tasks = await new IndexChunkTaskRepository(h.Db.Core.AppDataSource).GetByJobAsync(w.Id, job.JobId, Ct);
        tasks.Should().HaveCount(3).And.OnlyContain(t => t.Kind == IndexTaskKind.BulkCoding
            && t.Membership.Kind == ChunkMembershipKind.SnapshotRange && t.Membership.SnapshotId == snapshot.SnapshotId);
        var reader = new IndexTaskMembershipReader(h.Db.Core.AppDataSource);
        var resolved = new List<Guid>();
        foreach (var task in tasks.OrderBy(t => t.Membership.RangeFrom))
        {
            Guid? after = null;
            while (await reader.ReadPageAsync(w.Id, task.Membership, after, 700, Ct) is { Count: > 0 } page)
            {
                resolved.AddRange(page);
                after = page[^1];
            }
        }

        resolved.Order().Should().Equal(docs.Order());

        // Audit: one submission and one completion event per job, one event per chunk, never one per document.
        var audit = await h.AuditActionsAsync(w.Id, job.JobId);
        audit.Should().Contain("Coding.BulkSubmitted", 1).And.Contain("Coding.BulkChunkApplied", 3).And.Contain("Coding.BulkCompleted", 1)
            .And.Contain("Job.Created", 1).And.NotContainKey("Coding.Changed");

        (await h.ReportAsync(w.Id, user, job.JobId, BulkCodingOutcome.Applied, limit: 900)).Items.Select(i => i.DocumentId)
            .Should().Equal(docs.Order(), "applied documents are listed in DocumentId order");
        (await h.ReportAsync(w.Id, user, job.JobId, BulkCodingOutcome.SkippedChanged)).Items.Should().BeEmpty();

        // The old snapshot's baselines predate this job's changes, so a second job over it skips every document (Q-07);
        // over a fresh snapshot the same operation is state-based (ADR-010 §5.4): nothing changes, no event is written.
        var stale = await h.RunAsync(w.Id, (await h.StartAsync(w.Id, user, snapshot.SnapshotId, Set(w.Responsive, false))).JobId);
        stale.Counters.ItemsSkippedConcurrentEdit.Should().Be(2_500);
        var fresh = await h.SnapshotAsync(w.Id, user, docs);
        var again = await h.RunAsync(w.Id, (await h.StartAsync(w.Id, user, fresh.SnapshotId, Set(w.Responsive, true))).JobId);
        again.Status.Should().Be(JobStatus.Completed);
        again.Counters.ItemsUnchanged.Should().Be(2_500);
        again.Counters.IndexTasksTotal.Should().Be(0);
        (await h.EventsAsync(w.Id, again.JobId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Interactive_edits_after_the_snapshot_are_skipped_and_reported_never_overwritten()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var bulkUser = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 1_500);
        var snapshot = await h.SnapshotAsync(w.Id, bulkUser, docs);
        var job = await h.StartAsync(w.Id, bulkUser, snapshot.SnapshotId, Set(w.Responsive, true));

        // After the snapshot, before any chunk: a reviewer codes the same field on doc 5 and another field on doc 7, and
        // doc 9 is deleted.
        await h.InteractiveAsync(w.Id, reviewer, docs[5], CodingFieldOperation.Set(w.Responsive, false));
        await h.InteractiveAsync(w.Id, reviewer, docs[7], CodingFieldOperation.Set(w.Notes, "Looked at it"));
        await h.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET is_deleted = true, deleted_at = now(), document_version = document_version + 1 WHERE document_id = @id",
            ("id", docs[9]));

        var done = await h.RunAsync(w.Id, job.JobId, async chunk =>
        {
            if (chunk.Sequence == 2)
            {
                // While the job runs: one edit in the chunk still to come, one in the chunk already committed.
                await h.InteractiveAsync(w.Id, reviewer, docs[1_100], CodingFieldOperation.Set(w.Responsive, false));
                await h.InteractiveAsync(w.Id, reviewer, docs[3], CodingFieldOperation.Set(w.Responsive, false));
            }
        });

        done.Status.Should().Be(JobStatus.Completed, "skips are reported outcomes, not errors");
        var values = await h.ValuesAsync(w.Id, docs, w.Responsive);
        values[docs[5]]!.GetValue<bool>().Should().BeFalse("the interactive edit after the snapshot wins (Q-07)");
        values[docs[1_100]]!.GetValue<bool>().Should().BeFalse("an edit during the job is not overwritten by a later chunk");
        values[docs[3]]!.GetValue<bool>().Should().BeFalse("an edit after the chunk committed is simply newer");
        values[docs[7]]!.GetValue<bool>().Should().BeTrue("an edit of another field does not block the bulk field (per-field rule, ADR-010 §8.3)");
        values.Should().NotContainKey(docs[9]);
        values.Where(v => v.Key != docs[5] && v.Key != docs[1_100] && v.Key != docs[3]).Should().OnlyContain(v => v.Value!.GetValue<bool>());

        done.Counters.ItemsSkippedConcurrentEdit.Should().Be(3);
        done.Counters.ItemsApplied.Should().Be(1_500 - 3);
        var skipped = await h.ReportAsync(w.Id, bulkUser, job.JobId, BulkCodingOutcome.SkippedChanged);
        skipped.Items.Select(i => (i.DocumentId, i.ReasonCode)).Should().BeEquivalentTo(
            [(docs[5], "ConcurrentEdit"), (docs[1_100], "ConcurrentEdit"), (docs[9], "DocumentDeleted")]);
        skipped.Items.Where(i => i.ReasonCode == "ConcurrentEdit").Should().OnlyContain(i => i.FieldIds.SequenceEqual(new[] { w.Responsive }));

        // The skips are in coding history (provenance) without a value change or version bump.
        var events = await h.EventsAsync(w.Id, job.JobId);
        events.Where(e => e.Kind == CodingEventKind.BulkSkippedConcurrentEdit).Select(e => e.DocumentId)
            .Should().BeEquivalentTo([docs[5], docs[1_100]]);
        (await h.ReportAsync(w.Id, bulkUser, job.JobId, BulkCodingOutcome.Applied)).Items.Should().HaveCount(1_500 - 3);
    }

#if OPPORTUNITY_FAILPOINTS
    [Fact]
    public async Task A_crash_and_redelivery_never_apply_a_chunk_twice()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var user = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var docs = await h.DocumentsAsync(w.Id, 2_200);
        var snapshot = await h.SnapshotAsync(w.Id, user, docs);
        var job = await h.StartAsync(w.Id, user, snapshot.SnapshotId, Set(w.Responsive, true), Add(w.Issues, w.IssueB));
        var chunks = await h.ChunksAsync(w.Id, job.JobId);

        // Chunk 1: the process dies after the commit, before the ack; the broker redelivers (twice).
        var afterCommit = new CrashOnce(Failpoints.AfterCommit);
        await FluentActions.Awaiting(() => h.DeliverAsync(chunks[0], afterCommit)).Should().ThrowAsync<SimulatedCrashException>();
        await h.DeliverAsync(chunks[0], redelivered: true);
        await h.DeliverAsync(chunks[0], redelivered: true);

        // Chunk 2: the process dies right after claiming; the redelivery finds the lease held and is dropped, and once
        // the lease has expired the next delivery reclaims the chunk and applies it.
        var afterClaim = new CrashOnce(Failpoints.AfterClaim);
        await FluentActions.Awaiting(() => h.DeliverAsync(chunks[1], afterClaim)).Should().ThrowAsync<SimulatedCrashException>();
        await h.DeliverAsync(chunks[1], redelivered: true);
        (await h.ChunksAsync(w.Id, job.JobId))[1].Status.Should().Be(JobChunkStatus.Running);
        await h.Db.Core.ExecuteAsync(
            "UPDATE opportunity.job_chunk SET lease_expires_at = now() - interval '1 minute' WHERE chunk_id = @chunk", ("chunk", chunks[1].ChunkId));
        await h.DeliverAsync(chunks[1], redelivered: true);

        // Chunk 3: a stale copy of the message arrives after the chunk committed.
        await h.DeliverAsync(chunks[2]);
        await h.DeliverAsync(chunks[2], redelivered: true);

        var done = await h.JobAsync(w.Id, job.JobId);
        done.Status.Should().Be(JobStatus.Completed);
        done.Counters.ItemsApplied.Should().Be(2_200);
        done.Counters.ChunksCommitted.Should().Be(3);
        done.Counters.IndexTasksTotal.Should().Be(3);
        (await h.ChunksAsync(w.Id, job.JobId)).Select(c => c.AttemptCount).Should().Equal(1, 2, 1);

        var events = await h.EventsAsync(w.Id, job.JobId);
        events.Should().HaveCount(2 * 2_200, "exactly one CodingEvent per document and field, whatever the deliveries");
        events.Select(e => (e.DocumentId, e.FieldId)).Distinct().Should().HaveCount(2 * 2_200);
        (await h.Db.Core.ScalarAsync<long>("SELECT count(*) FROM opportunity.coding_write WHERE workspace_id = @ws AND job_id = @job",
            ("ws", w.Id), ("job", job.JobId))).Should().Be(3);
        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_projection_state WHERE workspace_id = @ws AND document_version <> 2", ("ws", w.Id)))
            .Should().Be(0, "every document was bumped exactly once");
        (await h.AuditActionsAsync(w.Id, job.JobId)).Should().Contain("Coding.BulkChunkApplied", 3);
    }

    private sealed class CrashOnce(string failpoint) : IFaultInjector
    {
        private int _hits;

        public ValueTask HitAsync(string name, FailpointContext context, CancellationToken cancellationToken) =>
            name == failpoint && Interlocked.Increment(ref _hits) == 1 ? throw new SimulatedCrashException() : ValueTask.CompletedTask;
    }
#endif

    [Fact]
    public async Task Revoking_the_initiators_permission_stops_the_remaining_chunks()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var user = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var docs = await h.DocumentsAsync(w.Id, 3_000);
        var snapshot = await h.SnapshotAsync(w.Id, user, docs);
        var job = await h.StartAsync(w.Id, user, snapshot.SnapshotId, Set(w.Responsive, true));

        var done = await h.RunAsync(w.Id, job.JobId, async chunk =>
        {
            if (chunk.Sequence == 2)
            {
                // The QC role (and with it Coding.Bulk) is taken away after the first chunk committed.
                await h.Db.Core.ExecuteAsync("DELETE FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND user_id = @user",
                    ("ws", w.Id), ("user", user));
            }
        });

        done.Status.Should().Be(JobStatus.Failed);
        done.StatusReason.Should().Contain("Coding.Bulk");
        (await h.ChunksAsync(w.Id, job.JobId)).Select(c => c.Status)
            .Should().Equal(JobChunkStatus.Committed, JobChunkStatus.Cancelled, JobChunkStatus.Cancelled);
        done.Counters.ItemsApplied.Should().Be(1_000, "committed chunks stay (no undo, Q-34)");

        var values = await h.ValuesAsync(w.Id, docs, w.Responsive);
        docs.Take(1_000).Should().OnlyContain(d => values[d]!.GetValue<bool>());
        docs.Skip(1_000).Should().OnlyContain(d => values[d] == null, "no chunk runs after the permission is gone");
        (await h.EventsAsync(w.Id, job.JobId)).Should().HaveCount(1_000);
        var audit = await h.AuditActionsAsync(w.Id, job.JobId);
        audit.Should().Contain("Job.Failed", 1).And.Contain("Coding.BulkChunkApplied", 1).And.NotContainKey("Coding.BulkCompleted");
        h.Audit.Events.Should().Contain(e => e.Category == "AuthZ" && e.Action == "Denied", "the denied re-authorization is audited");
    }

    [Fact]
    public async Task Documents_the_initiator_can_no_longer_see_are_skipped_as_hidden()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var user = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var docs = await h.DocumentsAsync(w.Id, 40);
        var snapshot = await h.SnapshotAsync(w.Id, user, docs);
        var job = await h.StartAsync(w.Id, user, snapshot.SnapshotId, Set(w.Responsive, true));

        // After the snapshot two documents become Attorneys' Eyes Only, which a QC reviewer may not see.
        foreach (var doc in new[] { docs[2], docs[30] })
        {
            await h.Db.Core.ExecuteAsync(
                "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, @class)",
                ("ws", w.Id), ("doc", doc), ("class", RestrictionClasses.AttorneysEyesOnly));
        }

        var done = await h.RunAsync(w.Id, job.JobId);
        done.Status.Should().Be(JobStatus.Completed);
        done.Counters.ItemsExcludedNoAccess.Should().Be(2);
        done.Counters.ItemsApplied.Should().Be(38);
        var values = await h.ValuesAsync(w.Id, docs, w.Responsive);
        values[docs[2]].Should().BeNull();
        values[docs[30]].Should().BeNull();

        // The requester sees the generic reason; the precise one is only in the per-document AuthZ.Denied audit.
        var hidden = await h.ReportAsync(w.Id, user, job.JobId, BulkCodingOutcome.SkippedHidden);
        hidden.Items.Should().BeEquivalentTo(
            [new BulkCodingReportItem(docs[2], BulkCodingOutcome.SkippedHidden, "AccessChanged", []),
             new BulkCodingReportItem(docs[30], BulkCodingOutcome.SkippedHidden, "AccessChanged", [])]);
        h.Audit.Events.Where(e => e.Action == "Denied").Select(e => (e.ResourceId, e.ReasonCode))
            .Should().BeEquivalentTo([(docs[2].ToString(), "RestrictionClass"), (docs[30].ToString(), "RestrictionClass")]);

        // A chunk whose every member is hidden still commits, with its outcomes and its chunk audit, and no index task.
        var allHidden = await h.SnapshotAsync(w.Id, user, [docs[2], docs[30]]);
        var second = await h.RunAsync(w.Id, (await h.StartAsync(w.Id, user, allHidden.SnapshotId, Set(w.Responsive, false))).JobId);
        second.Status.Should().Be(JobStatus.Completed);
        second.Counters.ItemsExcludedNoAccess.Should().Be(2);
        second.Counters.IndexTasksTotal.Should().Be(0);
        (await h.AuditActionsAsync(w.Id, second.JobId)).Should().Contain("Coding.BulkChunkApplied", 1).And.Contain("Coding.BulkCompleted", 1);
    }

    [Fact]
    public async Task Security_affecting_bulk_coding_needs_WritePrivilege_and_recomputes_restriction_classes()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres, new ConfidentialityBinding());
        var w = await h.WorkspaceAsync();
        var qc = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var privilege = await h.MemberAsync(w.Id, WorkspaceRole.PrivilegeReviewer);
        var docs = await h.DocumentsAsync(w.Id, 1_200);
        var confidential = Set(w.Confidentiality, w.Confidential);

        var qcSnapshot = await h.SnapshotAsync(w.Id, qc, docs);
        (await h.SubmitAsync(w.Id, qc, qcSnapshot.SnapshotId, [confidential])).Status.Should().Be(BulkCodingSubmitStatus.Forbidden);

        var snapshot = await h.SnapshotAsync(w.Id, privilege, docs);
        var job = await h.StartAsync(w.Id, privilege, snapshot.SnapshotId, confidential);
        job.Counters.ChunksTotal.Should().Be(3, "500 members per chunk when a security-affecting field is written");
        var done = await h.RunAsync(w.Id, job.JobId);
        done.Status.Should().Be(JobStatus.Completed);

        (await h.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_restriction WHERE workspace_id = @ws AND class_key = 'Confidential'", ("ws", w.Id)))
            .Should().Be(1_200, "each document's restriction classes are recomputed in its chunk transaction (§24 rule 1)");
        var tasks = await new IndexChunkTaskRepository(h.Db.Core.AppDataSource).GetByJobAsync(w.Id, job.JobId, Ct);
        tasks.Should().HaveCount(3).And.OnlyContain(t => t.ChangeMask.HasFlag(SearchChangeMask.Security) && t.Lane == MessageLane.SecurityBulk);
    }

    [Fact]
    public async Task Submission_is_validated_authorized_and_idempotent()
    {
        await using var h = await BulkCodingHarness.CreateAsync(postgres);
        var w = await h.WorkspaceAsync();
        var user = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var other = await h.MemberAsync(w.Id, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(w.Id, WorkspaceRole.Reviewer);
        var docs = await h.DocumentsAsync(w.Id, 10);
        var snapshot = await h.SnapshotAsync(w.Id, user, docs);

        (await h.SubmitAsync(w.Id, reviewer, snapshot.SnapshotId, [Set(w.Responsive, true)])).Status
            .Should().Be(BulkCodingSubmitStatus.Forbidden, "a reviewer has no Coding.Bulk");
        (await h.SubmitAsync(w.Id, other, snapshot.SnapshotId, [Set(w.Responsive, true)])).Status
            .Should().Be(BulkCodingSubmitStatus.NotFound, "another user's frozen set is not visible without Job.ViewAll");
        (await h.SubmitAsync(w.Id, user, Guid.CreateVersion7(), [Set(w.Responsive, true)])).Status.Should().Be(BulkCodingSubmitStatus.NotFound);

        var invalid = await h.SubmitAsync(w.Id, user, snapshot.SnapshotId,
            [Set(w.Responsive, "yes"), Add(w.Issues, w.IssueRetired), Set(999_999, true)]);
        invalid.Status.Should().Be(BulkCodingSubmitStatus.Invalid);
        invalid.Errors.Should().HaveCount(3);
        (await h.SubmitAsync(w.Id, user, snapshot.SnapshotId, [Set(w.Responsive, true), Set(w.Responsive, false)])).Status
            .Should().Be(BulkCodingSubmitStatus.Invalid);

        var first = await h.SubmitAsync(w.Id, user, snapshot.SnapshotId, [Set(w.Responsive, true)], "key-1");
        first.Status.Should().Be(BulkCodingSubmitStatus.Accepted);
        first.Created.Should().BeTrue();
        var retry = await h.SubmitAsync(w.Id, user, snapshot.SnapshotId, [Set(w.Responsive, true)], "key-1");
        retry.Status.Should().Be(BulkCodingSubmitStatus.Accepted);
        retry.Created.Should().BeFalse();
        retry.Job!.JobId.Should().Be(first.Job!.JobId);
        retry.Job.Counters.ChunksTotal.Should().Be(1);
        (await h.SubmitAsync(w.Id, user, snapshot.SnapshotId, [Set(w.Responsive, false)], "key-1")).Status
            .Should().Be(BulkCodingSubmitStatus.IdempotencyKeyReuse);
        (await h.AuditActionsAsync(w.Id, first.Job.JobId)).Should().Contain("Coding.BulkSubmitted", 1);

        // Clear (set to null) is an operation too; an empty frozen set completes at once.
        var empty = await h.SnapshotAsync(w.Id, user, []);
        var cleared = await h.StartAsync(w.Id, user, empty.SnapshotId, Set(w.Notes, null));
        cleared.Status.Should().Be(JobStatus.Completed);
        cleared.Counters.ChunksTotal.Should().Be(0);
    }
}
