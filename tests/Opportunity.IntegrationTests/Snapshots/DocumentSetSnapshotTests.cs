using AwesomeAssertions;
using Npgsql;
using Opportunity.Application.Audit;
using Opportunity.Application.Search.Indexing;
using Opportunity.Application.Snapshots;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Search.Indexing;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Snapshots;

/// <summary>
/// E10-T02 against real OpenSearch and PostgreSQL: frozen count and generation on creation, restricted and walled
/// documents never frozen, membership identical after coding / reindex / alias switch / deletion, database-enforced
/// immutability, restartable chunk iteration over a large set, background materialization, retention and workspace
/// isolation.
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class DocumentSetSnapshotTests(OpenSearchFixture openSearch, MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_query_snapshot_returns_frozen_count_and_generation_and_never_holds_documents_the_creator_cannot_see()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Search.WorkspaceAsync();
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var privilege = await h.MemberAsync(ws, WorkspaceRole.PrivilegeReviewer);

        // Family B (parent + two attachments) sorts before family A by the parent's control number.
        var a = await h.DocumentAsync(ws, "SNAP-0002", "merger memo");
        var b = await h.DocumentAsync(ws, "SNAP-0001", "merger memo");
        var b2 = await h.DocumentAsync(ws, "SNAP-0009", "merger memo attachment", parent: b, sequence: 2);
        var b1 = await h.DocumentAsync(ws, "SNAP-0010", "merger memo attachment", parent: b, sequence: 1);
        var aeo = await h.DocumentAsync(ws, "SNAP-0003", "merger memo eyes only", [RestrictionClasses.AttorneysEyesOnly]);
        // Restricted in PostgreSQL after indexing: only the PostgreSQL re-check can catch it.
        var lateAeo = await h.DocumentAsync(ws, "SNAP-0004", "merger memo late", projectSecurity: false);
        await h.Search.Db.Core.ExecuteAsync(
            "INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'AttorneysEyesOnly')",
            ("ws", ws), ("doc", lateAeo));
        var walled = await h.DocumentAsync(ws, "SNAP-0005", "merger memo walled", projectSecurity: false);
        await h.Search.Db.WallAsync(ws, [qc], [], [walled]);
        await h.DocumentAsync(ws, "SNAP-0006", "unrelated");

        var outcome = await h.CreateAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "merger"));

        outcome.Status.Should().Be(SnapshotCreateStatus.Ready);
        var snapshot = outcome.Snapshot!;
        snapshot.Status.Should().Be(SnapshotStatus.Ready);
        snapshot.DocumentCount.Should().Be(4);
        snapshot.SearchGeneration.Should().NotBeNull("the frozen generation is returned for the confirmation");
        snapshot.ProjectionGeneration.Should().NotBeNull();
        snapshot.NormalizedQuery.Should().Be("merger");
        snapshot.ExcludedNoAccess.Should().Be(2, "the AEO document never left the index filter; the late AEO and walled ones were re-checked out");
        snapshot.InclusionCounts.Should().Equal(new Dictionary<SnapshotInclusionReason, long> { [SnapshotInclusionReason.Hit] = 4 });
        snapshot.Name.Should().StartWith("Mass Edit ");

        var members = await h.MembersAsync(ws, snapshot);
        members.Select(m => m.Ordinal).Should().Equal(1, 2, 3, 4);
        members.Select(m => m.DocumentId).Should().Equal([b, b1, b2, a], "families are contiguous, parent first, then family sequence");
        members.Should().OnlyContain(m => m.BaselineVersion >= 1 && m.Reason == SnapshotInclusionReason.Hit);
        members.Select(m => m.DocumentId).Should().NotContain([aeo, lateAeo, walled]);
        (await h.ServiceAsync(s => s.VerifyAsync(ws, snapshot.SnapshotId, Ct))).Valid.Should().BeTrue();

        // The privilege reviewer holds the AEO grant and is not walled.
        var all = await h.ReadyAsync(ws, privilege, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "merger"));
        all.DocumentCount.Should().Be(7);

        // Audited from the closed taxonomy with the snapshot ID, in the freeze transaction.
        (await h.Search.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_event WHERE snapshot_id = @id AND category = 'Search' AND action = 'Executed' AND restricted_details->>'query' = 'merger'",
            ("id", snapshot.SnapshotId))).Should().Be(1);
        h.Search.Audit.Events.Should().Contain(e => e.Action == AuditTaxonomy.AuthZ.Denied && e.Details["denied"] == "2",
            "the selection's re-check drops are summarized once (Q-59)");
    }

    [Fact]
    public async Task Membership_is_identical_after_coding_deletion_reindex_and_alias_switch_and_cannot_be_changed()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws = await h.Search.WorkspaceAsync();
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var docs = new List<Guid>();
        for (var i = 1; i <= 6; i++)
        {
            docs.Add(await h.DocumentAsync(ws, $"IMM-{i:000}", "supply contract"));
        }

        var snapshot = await h.ReadyAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "contract"));
        var before = await h.MembersAsync(ws, snapshot);
        before.Should().HaveCount(6);

        // Coding (version bumps), a deletion, new matching documents and a rebuilt index with an alias switch.
        await h.Search.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET document_version = document_version + 5 WHERE workspace_id = @ws",
            ("ws", ws));
        await h.Search.Db.Core.ExecuteAsync(
            "UPDATE opportunity.document_projection_state SET is_deleted = true, deleted_at = now() WHERE workspace_id = @ws AND document_id = @doc",
            ("ws", ws), ("doc", docs[0]));
        await h.DocumentAsync(ws, "IMM-100", "supply contract amendment");
        var moving = await h.Search.Indexes.BeginRebuildAsync(ws, new IndexRebuildRequest(Kind: IndexPlacementKind.Dedicated), Ct);
        await h.Search.ProjectAsync(ws, new System.Text.Json.Nodes.JsonObject
        {
            ["workspaceId"] = ws.ToString("D"),
            ["documentId"] = docs[1].ToString("D"),
            ["controlNumber"] = "IMM-002",
            ["text"] = "supply contract",
        }, docs[1]);
        _ = moving;
        await h.Search.Indexes.CompleteRebuildAsync(ws, Ct);

        var reread = await h.Store.GetAsync(ws, snapshot.SnapshotId, Ct);
        reread!.DocumentCount.Should().Be(6);
        reread.RootSha256.Should().Equal(snapshot.RootSha256!);
        (await h.MembersAsync(ws, reread)).Should().Equal(before, "membership never follows later changes (ADR-002 §5.5)");
        (await h.ServiceAsync(s => s.VerifyAsync(ws, snapshot.SnapshotId, Ct))).Valid.Should().BeTrue();

        // A new snapshot does see the new state: only the reindexed document is in the new index, and one is deleted.
        var fresh = await h.ReadyAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "contract"));
        (await h.MembersAsync(ws, fresh)).Select(m => m.DocumentId).Should().Equal(docs[1]);

        // The database refuses every change to frozen membership, even from the application login.
        var app = h.Search.Db.Core.AppDataSource;
        foreach (var sql in new[]
        {
            "UPDATE opportunity.document_set_snapshot_page SET member_count = member_count WHERE workspace_id = @ws AND snapshot_id = @id",
            "DELETE FROM opportunity.document_set_snapshot_page WHERE workspace_id = @ws AND snapshot_id = @id",
            "UPDATE opportunity.document_set_snapshot SET document_count = 1 WHERE workspace_id = @ws AND snapshot_id = @id",
            "UPDATE opportunity.document_set_snapshot SET status = 'Materializing' WHERE workspace_id = @ws AND snapshot_id = @id",
            """
            INSERT INTO opportunity.document_set_snapshot_page (workspace_id, snapshot_id, page_no, first_ordinal, member_count, document_ids,
                baseline_versions, inclusion_reasons, sha256)
            VALUES (@ws, @id, 99, 99000, 1, ARRAY[gen_random_uuid()], ARRAY[1::bigint], ARRAY[1::smallint], sha256('x'::bytea))
            """,
            "DELETE FROM opportunity.document_set_snapshot WHERE workspace_id = @ws AND snapshot_id = @id",
        })
        {
            var change = () => InWorkspaceAsync(app, ws, sql, snapshot.SnapshotId);
            await change.Should().ThrowAsync<PostgresException>(sql);
        }

        (await h.MembersAsync(ws, snapshot)).Should().Equal(before);
    }

    [Fact]
    public async Task Chunk_iteration_over_a_large_snapshot_is_restartable_by_ordinal_with_no_gaps_or_overlaps()
    {
        const int Count = 12_345;
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres, o => o.SynchronousMaxDocuments = 20_000);
        var ws = await h.Search.WorkspaceAsync(dedicated: true);
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        var ids = await h.BulkDocumentsAsync(ws, Count, "BIG-", "quarterly ledger");

        var snapshot = await h.ReadyAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "ledger"));

        snapshot.DocumentCount.Should().Be(Count);
        snapshot.PageCount.Should().Be(13, "1,000 members per page");
        (await h.ServiceAsync(s => s.VerifyAsync(ws, snapshot.SnapshotId, Ct))).Should().BeEquivalentTo(
            new SnapshotVerification(true, Count, 13, []));

        // Chunks as a bulk job plans them, across page boundaries.
        var chunks = DocumentSetSnapshotService.PlanChunks(snapshot, 777);
        chunks.Should().HaveCount((Count + 776) / 777);
        chunks[0].RangeFrom.Should().Be(1);
        chunks[^1].RangeTo.Should().Be(Count);
        chunks.Zip(chunks.Skip(1)).Should().OnlyContain(p => p.Second.RangeFrom == p.First.RangeTo + 1);
        var byChunk = new List<SnapshotMember>();
        foreach (var chunk in chunks)
        {
            var members = await h.ServiceAsync(s => s.ReadRangeAsync(ws, snapshot.SnapshotId, chunk.RangeFrom!.Value, chunk.RangeTo!.Value, Ct));
            members.Should().HaveCount((int)chunk.KnownCount!.Value);
            byChunk.AddRange(members);
        }

        byChunk.Select(m => m.Ordinal).Should().Equal(Enumerable.Range(1, Count).Select(i => (long)i));
        byChunk.Select(m => m.DocumentId).Should().Equal(ids, "control-number order of single-document families");

        // Interrupted after ordinal 6,000 and restarted from there: the rest, exactly once.
        var restarted = new List<SnapshotMember>();
        await h.ServiceAsync(async s =>
        {
            await foreach (var batch in s.IterateAsync(ws, snapshot.SnapshotId, afterOrdinal: 6_000, batchSize: 1_500, Ct))
            {
                restarted.AddRange(batch);
            }

            return 0;
        });
        restarted.Should().Equal(byChunk.Skip(6_000));
    }

    [Fact]
    public async Task Explicit_ids_and_saved_selections_are_reauthorized_and_snapshots_stay_in_their_workspace()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres);
        var ws1 = await h.Search.WorkspaceAsync();
        var ws2 = await h.Search.WorkspaceAsync();
        var manager = await h.MemberAsync(ws1, WorkspaceRole.ProductionManager);
        await h.Search.Db.AssignAsync(ws2, WorkspaceRole.ProductionManager, manager);
        var qc = await h.MemberAsync(ws1, WorkspaceRole.QcReviewer);
        var reviewer = await h.MemberAsync(ws1, WorkspaceRole.Reviewer);
        var mine = await h.DocumentAsync(ws1, "X-001", "a");
        var restricted = await h.DocumentAsync(ws1, "X-002", "b");
        await h.Search.Db.WallAsync(ws1, [manager], [], [restricted]);
        var theirs = await h.DocumentAsync(ws2, "Y-001", "c");

        // Another workspace's document and an unknown ID are treated alike: missing.
        var snapshot = await h.ReadyAsync(ws1, manager,
            new SnapshotCreateRequest(SnapshotPurpose.Export, DocumentIds: [mine, restricted, theirs, Guid.NewGuid(), mine]));
        snapshot.RequestedCount.Should().Be(4);
        (await h.MembersAsync(ws1, snapshot)).Select(m => (m.DocumentId, m.Reason)).Should().Equal((mine, SnapshotInclusionReason.Explicit));
        snapshot.ExcludedMissing.Should().Be(2);
        snapshot.ExcludedNoAccess.Should().Be(1);

        // Workspace isolation: invisible from the other workspace's context.
        (await h.Store.GetAsync(ws2, snapshot.SnapshotId, Ct)).Should().BeNull();
        (await h.Store.ReadMembersAsync(ws2, snapshot.SnapshotId, 1, 10, Ct)).Should().BeEmpty();
        (await h.CreateAsync(ws2, manager, new SnapshotCreateRequest(SnapshotPurpose.Export, SourceSnapshotId: snapshot.SnapshotId)))
            .Status.Should().Be(SnapshotCreateStatus.NotFound);

        // A saved selection is re-authorized for its new creator (a candidate set, never a grant).
        var everything = await h.ReadyAsync(ws1, await h.MemberAsync(ws1, WorkspaceRole.WorkspaceAdmin),
            new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, DocumentIds: [mine, restricted]));
        everything.DocumentCount.Should().Be(2);
        (await h.CreateAsync(ws1, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, SourceSnapshotId: everything.SnapshotId)))
            .Status.Should().Be(SnapshotCreateStatus.NotFound, "another user's snapshot needs Job.ViewAll");
        var copied = await h.ReadyAsync(ws1, manager, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, SourceSnapshotId: everything.SnapshotId));
        (await h.MembersAsync(ws1, copied)).Select(m => m.DocumentId).Should().Equal(mine);

        // The purpose's permission is required.
        (await h.CreateAsync(ws1, reviewer, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, DocumentIds: [mine])))
            .Status.Should().Be(SnapshotCreateStatus.Forbidden);
        (await h.CreateAsync(ws1, await h.Search.Db.CreateUserAsync(), new SnapshotCreateRequest(SnapshotPurpose.Report, DocumentIds: [mine])))
            .Status.Should().Be(SnapshotCreateStatus.NotFound);
    }

    [Fact]
    public async Task Large_selections_materialize_in_the_background_and_retention_expires_unused_ones()
    {
        await using var h = await SnapshotHarness.CreateAsync(openSearch, postgres, o =>
        {
            o.SynchronousMaxDocuments = 2;
            o.MaxDocuments = 10;
        });
        var ws = await h.Search.WorkspaceAsync();
        var qc = await h.MemberAsync(ws, WorkspaceRole.QcReviewer);
        for (var i = 1; i <= 5; i++)
        {
            await h.DocumentAsync(ws, $"BG-{i:000}", "board minutes");
        }

        for (var i = 1; i <= 6; i++)
        {
            await h.DocumentAsync(ws, $"TL-{i:000}", "too large");
        }

        (await h.CreateAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "large OR minutes")))
            .Status.Should().Be(SnapshotCreateStatus.TooLarge);
        (await h.CreateAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "nothing:(")))
            .Status.Should().Be(SnapshotCreateStatus.InvalidQuery);

        var accepted = await h.CreateAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "minutes", ClientIdempotencyKey: "k-1"));
        accepted.Status.Should().Be(SnapshotCreateStatus.Accepted);
        accepted.Snapshot!.Status.Should().Be(SnapshotStatus.Materializing);
        (await h.CreateAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "minutes", ClientIdempotencyKey: "k-1")))
            .Snapshot!.SnapshotId.Should().Be(accepted.Snapshot.SnapshotId, "a retried request returns the same snapshot");

        (await h.Store.GetUnclaimedAsync(ws, 10, Ct)).Should().Equal(accepted.Snapshot.SnapshotId);
        (await h.ServiceAsync(s => s.MaterializePendingAsync(ws, accepted.Snapshot.SnapshotId, Ct))).Should().Be(SnapshotStatus.Ready);
        var ready = await h.Store.GetAsync(ws, accepted.Snapshot.SnapshotId, Ct);
        ready!.DocumentCount.Should().Be(5);
        (await h.ServiceAsync(s => s.MaterializePendingAsync(ws, accepted.Snapshot.SnapshotId, Ct))).Should().BeNull("already Ready");

        // Retention: an unreferenced snapshot expires; one a running job targets does not; an expired one cannot be targeted.
        var used = await h.ReadyAsync(ws, qc, new SnapshotCreateRequest(SnapshotPurpose.BulkCoding, Query: "BG-001"));
        var jobs = new Data.Jobs.JobRepository(h.Search.Db.Core.AppDataSource);
        await jobs.CreateAsync(new Application.Jobs.NewJob { WorkspaceId = ws, JobType = JobType.BulkCoding, InitiatedBy = qc, TargetSnapshotId = used.SnapshotId }, Ct);

        var expired = await h.Store.ExpireAsync(ws, TimeSpan.FromHours(24), TimeSpan.FromDays(90), 100, Ct);
        expired.Should().BeEmpty("nothing is older than the unreferenced lifetime yet");
        expired = await h.Store.ExpireAsync(ws, TimeSpan.Zero, TimeSpan.FromDays(90), 100, Ct);
        expired.Should().Equal(ready.SnapshotId);

        var tombstone = await h.Store.GetAsync(ws, ready.SnapshotId, Ct);
        tombstone!.Status.Should().Be(SnapshotStatus.Expired);
        tombstone.RootSha256.Should().Equal(ready.RootSha256!, "the header keeps its counts and hashes");
        (await h.Search.Db.Core.ScalarAsync<long>(
            "SELECT count(*) FROM opportunity.document_set_snapshot_page WHERE snapshot_id = @id", ("id", ready.SnapshotId))).Should().Be(0);
        (await h.Store.GetAsync(ws, used.SnapshotId, Ct))!.Status.Should().Be(SnapshotStatus.Ready);

        var target = () => jobs.CreateAsync(
            new Application.Jobs.NewJob { WorkspaceId = ws, JobType = JobType.BulkCoding, InitiatedBy = qc, TargetSnapshotId = ready.SnapshotId }, Ct);
        await target.Should().ThrowAsync<PostgresException>();
    }

    private static async Task InWorkspaceAsync(NpgsqlDataSource app, Guid ws, string sql, Guid snapshotId)
    {
        await using var connection = await app.OpenConnectionAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.workspace_id', @ws, true)", connection, tx))
        {
            context.Parameters.AddWithValue("ws", ws.ToString());
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("ws", ws);
        command.Parameters.AddWithValue("id", snapshotId);
        var affected = await command.ExecuteNonQueryAsync(Ct);
        if (affected == 0)
        {
            throw new InvalidOperationException("No row was affected: the statement did not reach the snapshot.");
        }

        await tx.CommitAsync(Ct);
    }
}
