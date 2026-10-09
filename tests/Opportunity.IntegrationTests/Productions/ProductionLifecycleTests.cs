using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Npgsql;

using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Migrations;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
using Opportunity.Contracts.Messaging.Jobs;
#endif
using Opportunity.Production.Productions;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E12-T02/T03 against PostgreSQL (app login, RLS and the V0038 triggers on): a production's lifecycle (draft →
/// allocated → finalized → voided) with family-adjacent Bates numbers, written-back ProdBeg/End/Attach, the
/// cross-reference, the manifest and its verification; the frozen state refused by the database itself; Bates
/// uniqueness per prefix (start-number overlap blocked, Q-54 reuse of discarded drafts only); and the fault-injection
/// run (crash mid-chunk, redelivery) that must assign exactly what a clean run assigns.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ProductionLifecycleTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>F1 (parent + 2 attachments), an xlsx (native), a document without pages, F4 (parent + an .exe placeholder).</summary>
    private static readonly (int Pages, string Extension)[][] Families =
    [
        [(2, "msg"), (1, "pdf"), (3, "docx")],
        [(4, "xlsx")],
        [(0, "txt")],
        [(5, "pdf"), (2, "exe")],
    ];

    private static ProductionSpecification Spec(string prefix, long start = 1) => ProductionHarness.Spec(prefix, start) with
    {
        FileTypeRules = [new(["xlsx"], ProductionOutputResource.Native), new([".EXE"], ProductionOutputResource.Placeholder)],
    };

    [Fact]
    public async Task A_production_is_allocated_family_adjacent_finalized_frozen_verified_and_superseded_by_a_new_version()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres, documentsPerChunk: 2);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = await h.FamiliesAsync(ws, "DOC", Families);
        var snapshot = await h.SnapshotAsync(ws, user, docs);

        var draft = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, Spec("ABC"));
        draft.Status.Should().Be(ProductionStatus.Draft);
        draft.SpecificationSha256.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(draft.SpecificationJson)));
        var stored = ProductionSpecificationRules.Deserialize(draft.SpecificationJson);
        stored.Images.Should().Be(new ProductionImageSettings(ProductionImageFormatResource.TiffG4, ProductionImageFormatResource.Jpeg, 300), "defaults are stored");
        stored.FileTypeRules![1].Extensions.Should().Equal("exe");
        (await h.Snapshots.ExpireAsync(ws, TimeSpan.Zero, TimeSpan.Zero, 100, Ct)).Should().BeEmpty("a production's frozen set lives as long as the production");

        var allocated = await h.AllocateAsync(ws, user, draft.ProductionId);
        allocated.BatesState.Should().Be(BatesAllocationState.Allocated, allocated.BatesReason);
        (await h.Jobs.GetChunksAsync(ws, allocated.BatesJobId!.Value, cancellationToken: Ct)).Should().HaveCount(3, "chunks close between families only");
        var integrity = BatesIntegrityReport.FromJson(allocated.IntegrityJson)!;
        integrity.Passed.Should().BeTrue(string.Join(" ", integrity.Problems));
        integrity.Should().BeEquivalentTo(new { Documents = 7L, Numbers = 14L, Placeholders = 1L, NativeSlipSheets = 1L, Gaps = 0L });
        (allocated.BatesFirst, allocated.BatesLast).Should().Be((1L, 14L));

        var rows = await h.AssignmentAsync(ws, draft.ProductionId);
        rows.Select(r => (r.ControlNumber, r.ProdBegBates, r.ProdEndBates, r.ProdBegAttach, r.ProdEndAttach)).Should().Equal(
            ("DOC0001", "ABC0000001", "ABC0000002", "ABC0000001", "ABC0000006"),
            ("DOC0002", "ABC0000003", "ABC0000003", "ABC0000001", "ABC0000006"),
            ("DOC0003", "ABC0000004", "ABC0000006", "ABC0000001", "ABC0000006"),
            ("DOC0004", "ABC0000007", "ABC0000007", "ABC0000007", "ABC0000007"),
            ("DOC0005", "ABC0000008", "ABC0000008", "ABC0000008", "ABC0000008"),
            ("DOC0006", "ABC0000009", "ABC0000013", "ABC0000009", "ABC0000014"),
            ("DOC0007", "ABC0000014", "ABC0000014", "ABC0000009", "ABC0000014"));

        // Bates → document and document → numbers.
        var service = h.Service();
        var principal = ProductionHarness.Principal(user);
        var (byNumber, restricted) = await service.LookupAsync(principal, ws, "abc0000005", null, Ct);
        byNumber.Should().ContainSingle().Which.ControlNumber.Should().Be("DOC0003");
        restricted.Should().Be(0);
        (await service.LookupAsync(principal, ws, null, docs[5], Ct)).Matches.Should().ContainSingle().Which.ProdBegBates.Should().Be("ABC0000009");
        (await service.LookupAsync(principal, ws, "XYZ0000005", null, Ct)).Matches.Should().BeEmpty();

        // Finalize: the manifest records the specification, hashes and software versions.
        var finalized = await service.FinalizeAsync(principal, ws, draft.ProductionId, allocated.RowVersion, Ct);
        finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
        var production = finalized.Production!;
        production.Status.Should().Be(ProductionStatus.Finalized);
        production.ManifestSha256.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(production.Manifest!)));
        using (var manifest = JsonDocument.Parse(production.Manifest!))
        {
            var root = manifest.RootElement;
            root.GetProperty("specification").GetRawText().Should().Be(production.SpecificationJson);
            root.GetProperty("specificationSha256").GetString().Should().Be(Convert.ToHexStringLower(production.SpecificationSha256));
            root.GetProperty("snapshot").GetProperty("rootSha256").GetString().Should().Be(Convert.ToHexStringLower(snapshot.RootSha256!));
            root.GetProperty("bates").GetProperty("first").GetString().Should().Be("ABC0000001");
            root.GetProperty("bates").GetProperty("last").GetString().Should().Be("ABC0000014");
            root.GetProperty("bates").GetProperty("assignmentsSha256").GetString().Should().Be(Convert.ToHexStringLower(production.AssignmentsSha256!));
            root.GetProperty("software").GetProperty("opportunity").GetString().Should().NotBeNullOrEmpty();
            root.GetProperty("software").GetProperty("runtime").GetString().Should().StartWith(".NET");
            root.GetProperty("frozenState").GetProperty("redactionSetVersion").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // After finalization the specification and membership cannot be edited: by the service, nor by any SQL.
        (await service.UpdateAsync(principal, ws, draft.ProductionId, production.RowVersion, new UpdateProductionRequest(Spec("ABC", 100)), Ct))
            .Status.Should().Be(ProductionOutcomeStatus.Conflict);
        (await service.DiscardAsync(principal, ws, draft.ProductionId, production.RowVersion, Ct)).Status.Should().Be(ProductionOutcomeStatus.Conflict);
        foreach (var sql in new[]
        {
            "UPDATE opportunity.production SET specification = '{}' WHERE workspace_id = @ws AND production_id = @id",
            "UPDATE opportunity.production SET snapshot_id = snapshot_id, bates_start = 2 WHERE workspace_id = @ws AND production_id = @id",
            "UPDATE opportunity.production_document SET prod_beg_attach = prod_beg_bates WHERE workspace_id = @ws AND production_id = @id",
            "DELETE FROM opportunity.production_document WHERE workspace_id = @ws AND production_id = @id",
            "UPDATE opportunity.bates_range SET state = 4 WHERE workspace_id = @ws AND production_id = @id",
        })
        {
            var act = () => AppExecuteAsync(h, ws, sql, draft.ProductionId);
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().BeOneOf(PostgresErrorCodes.IntegrityConstraintViolation, PostgresErrorCodes.InsufficientPrivilege);
        }

        // Verification: consistent; then a tampered row (bypassing the guard as the owner) is reported and audited.
        var (_, verification) = await service.VerifyAsync(principal, ws, draft.ProductionId, h.SnapshotService(), Ct);
        verification!.Consistent.Should().BeTrue(string.Join("; ", verification.Differences.Select(d => $"{d.Item}: {d.Expected} / {d.Actual}")));
        await h.Db.ExecuteAsync(
            """
            ALTER TABLE opportunity.production_document DISABLE TRIGGER production_document_frozen_guard;
            UPDATE opportunity.production_document SET prod_end_attach = 'ABC0000099' WHERE workspace_id = @ws AND production_id = @id AND sequence = 4;
            ALTER TABLE opportunity.production_document ENABLE TRIGGER production_document_frozen_guard;
            """,
            ("ws", ws), ("id", draft.ProductionId));
        (_, verification) = await service.VerifyAsync(principal, ws, draft.ProductionId, h.SnapshotService(), Ct);
        verification!.Consistent.Should().BeFalse();
        verification.Differences.Select(d => d.Item).Should().Contain(["assignmentsSha256 (stored rows)", "integrity"]).And.Contain(i => i.StartsWith("document 4", StringComparison.Ordinal));
        (await h.Db.ColumnAsync($"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Production' ORDER BY occurred_at, action"))
            .Should().BeEquivalentTo("Created", "BatesAllocated", "SpecFrozen", "Finalized", "DesignationsFrozen", "Verified", "VerificationFailed");

        // A change is a new version: same lineage and frozen set, numbering continues after the version it supersedes.
        var next = await service.CreateAsync(principal, ws, new CreateProductionRequest(PreviousVersionId: draft.ProductionId), null, Ct);
        next.Status.Should().Be(ProductionOutcomeStatus.Ok, next.Reason);
        next.Production!.Should().BeEquivalentTo(new { LineageId = draft.ProductionId, Version = 2, SnapshotId = snapshot.SnapshotId, BatesStart = 15L });
    }

    [Fact]
    public async Task Bates_numbers_are_unique_per_prefix_overlaps_are_blocked_and_only_discarded_drafts_numbers_are_reused()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var principal = ProductionHarness.Principal(user);
        var service = h.Service();
        var snapshot = await h.SnapshotAsync(ws, user, await h.FamiliesAsync(ws, "UNI", [(2, "pdf")], [(1, "pdf")], [(3, "pdf")]));

        // P1 holds ABC 1–6.
        var p1 = await h.AllocateAsync(ws, user, (await h.CreateOkAsync(ws, user, snapshot.SnapshotId, Spec("ABC"))).ProductionId);
        (p1.BatesFirst, p1.BatesLast).Should().Be((1L, 6L));

        // Overlapping start numbers of the same prefix (compared without case) are blocked; another prefix is independent.
        (await h.CreateAsync(ws, user, snapshot.SnapshotId, Spec("abc", 4))).Status.Should().Be(ProductionOutcomeStatus.BatesConflict);
        (await h.CreateAsync(ws, user, snapshot.SnapshotId, Spec("ABC", 7))).Status.Should().Be(ProductionOutcomeStatus.Ok);
        (await h.CreateAsync(ws, user, snapshot.SnapshotId, Spec("XYZ", 1))).Status.Should().Be(ProductionOutcomeStatus.Ok);

        // Q-54: a never-produced draft's numbers are free again once it is discarded.
        (await service.DiscardAsync(principal, ws, p1.ProductionId, p1.RowVersion, Ct)).Status.Should().Be(ProductionOutcomeStatus.Ok);
        (await h.Service().LookupAsync(principal, ws, "ABC0000001", null, Ct)).Matches.Should().BeEmpty("discarded numbers were never issued");
        var p3 = await h.AllocateAsync(ws, user, (await h.CreateOkAsync(ws, user, snapshot.SnapshotId, Spec("ABC"))).ProductionId);
        p3.BatesState.Should().Be(BatesAllocationState.Allocated, p3.BatesReason);
        (p3.BatesFirst, p3.BatesLast).Should().Be((1L, 6L));

        // Once produced, never reissued, even after voiding.
        var finalized = (await service.FinalizeAsync(principal, ws, p3.ProductionId, p3.RowVersion, Ct)).Production!;
        (await service.VoidAsync(principal, ws, p3.ProductionId, finalized.RowVersion, "Produced to the wrong party.", Ct)).Status.Should().Be(ProductionOutcomeStatus.Ok);
        (await h.CreateAsync(ws, user, snapshot.SnapshotId, Spec("ABC", 3))).Status.Should().Be(ProductionOutcomeStatus.BatesConflict);
        (await service.LookupAsync(principal, ws, "ABC0000002", null, Ct)).Matches.Should().ContainSingle()
            .Which.ProductionStatus.Should().Be(ProductionStatus.Voided, "the cross-reference lasts for the life of the matter");

        // Two drafts that both passed the start-number check: the first to allocate wins, the second fails the overlap
        // check inside its planning transaction (prefix lock), is audited and its job fails.
        var a = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, Spec("RACE", 10));
        var b = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, Spec("RACE", 12));
        (await h.AllocateAsync(ws, user, a.ProductionId)).BatesState.Should().Be(BatesAllocationState.Allocated);
        var lost = await h.AllocateAsync(ws, user, b.ProductionId);
        lost.BatesState.Should().Be(BatesAllocationState.Failed);
        lost.BatesReason.Should().Contain("overlaps").And.Contain("RACE0000010");
        (await h.Jobs.GetAsync(ws, lost.BatesJobId!.Value, Ct))!.Status.Should().Be(JobStatus.Failed);

        var events = await h.Db.ColumnAsync(
            $"SELECT category || '.' || action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category IN ('Production', 'Integrity') ORDER BY occurred_at");
        events.Should().Contain(["Production.Discarded", "Production.Finalized", "Production.Voided", "Integrity.BatesConflict"]);
        (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.bates_range WHERE workspace_id = @ws AND state = 4", ("ws", ws)))
            .Should().Be(1, "the discarded draft's released range stays on record");
    }

#if OPPORTUNITY_FAILPOINTS
    [Fact]
    public async Task A_crash_mid_chunk_and_redeliveries_yield_exactly_the_clean_runs_bates_assignment()
    {
        await using var h = await ProductionHarness.CreateAsync(postgres, documentsPerChunk: 3);
        var ws = await h.Db.CreateWorkspaceAsync();
        await h.Db.Fields.InitializeWorkspaceAsync(ws, Ct);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var families = Enumerable.Range(0, 12).Select(i => Enumerable.Range(0, 1 + (i % 3)).Select(j => (Pages: (i + j) % 5, Extension: j == 2 ? "exe" : "pdf")).ToArray()).ToArray();
        var snapshot = await h.SnapshotAsync(ws, user, await h.FamiliesAsync(ws, "FLT", families));
        var principal = ProductionHarness.Principal(user);

        // The clean run, then discarded so the faulty run reuses exactly the same numbers (Q-54).
        var clean = await h.AllocateAsync(ws, user, (await h.CreateOkAsync(ws, user, snapshot.SnapshotId, Spec("FLT"))).ProductionId);
        clean.BatesState.Should().Be(BatesAllocationState.Allocated);
        var expected = Comparable(await h.AssignmentAsync(ws, clean.ProductionId));
        (await h.Service().DiscardAsync(principal, ws, clean.ProductionId, clean.RowVersion, Ct)).Status.Should().Be(ProductionOutcomeStatus.Ok);

        var faulty = await h.CreateOkAsync(ws, user, snapshot.SnapshotId, Spec("FLT"));
        var job = (await h.Service().AllocateAsync(principal, ws, faulty.ProductionId, null, Ct)).Job!;
        await h.CoordinateAsync(ws);
        var chunks = await h.Jobs.GetChunksAsync(ws, job.JobId, cancellationToken: Ct);
        chunks.Should().HaveCountGreaterThan(3);
        var replanned = await h.Store.PlanAllocationAsync(ws, faulty.ProductionId, job.JobId,
            new BatesPlanRequest(Core.Productions.BatesNumberingLevel.Page, _ => Core.Productions.ProductionOutputKind.Image, 1, 1, 9_999_999), Ct);
        replanned.Chunks.Select(c => (c.SequenceFrom, c.SequenceTo)).Should().Equal(
            chunks.Select(c => (c.Membership.RangeFrom!.Value, c.Membership.RangeTo!.Value)), "planning again returns the stored plan (a coordinator crash is harmless)");

        // Chunk 2 dies mid-chunk (numbers computed, nothing written); chunk 3 dies after its commit, before the ack.
        var faults = new Crashes(new() { [(chunks[1].ChunkId, Failpoints.BatesBeforeWrite)] = 1, [(chunks[2].ChunkId, Failpoints.AfterCommit)] = 1 });
        foreach (var chunk in chunks)
        {
            try
            {
                await h.Consumer(faults).HandleAsync(ProductionHarness.Payload(chunk), ProductionHarness.Received(chunk), Ct);
            }
            catch (SimulatedCrashException)
            {
            }
        }

        faults.Crashed.Should().Be(2);
        (await h.Jobs.GetChunksAsync(ws, job.JobId, cancellationToken: Ct)).Single(c => c.Sequence == 2).Status.Should().Be(JobChunkStatus.Running, "it died holding the lease");

        // Redelivery of the committed chunk is a no-op; the expired lease of the dead one returns it to Pending and it runs again.
        await h.Consumer().HandleAsync(ProductionHarness.Payload(chunks[2]), ProductionHarness.Received(chunks[2]), Ct);
        await h.Db.ExecuteAsync("UPDATE opportunity.job_chunk SET lease_expires_at = now() - interval '1 minute' WHERE workspace_id = @ws AND status = 3", ("ws", ws));
        (await h.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).ReturnedToPending.Should().Be(1);
        (await h.DeliverOpenChunksAsync(ws, job.JobId)).Should().Be(1);
        await h.CoordinateAsync(ws);

        var resumed = (await h.Store.GetAsync(ws, faulty.ProductionId, Ct))!;
        resumed.BatesState.Should().Be(BatesAllocationState.Allocated, resumed.BatesReason);
        Comparable(await h.AssignmentAsync(ws, faulty.ProductionId)).Should().Equal(expected, "identical Bates assignment after the crash and redeliveries");
        resumed.AssignmentsSha256.Should().Equal(clean.AssignmentsSha256);
    }

    private static List<string> Comparable(IEnumerable<ProductionDocumentRow> rows) =>
        [.. rows.Select(r => $"{r.Sequence}|{r.DocumentId}|{r.BegNumber}-{r.EndNumber}|{r.ProdBegBates}|{r.ProdEndBates}|{r.ProdBegAttach}|{r.ProdEndAttach}")];

    /// <summary>Crashes the given chunk at the given failpoint the given number of times.</summary>
    private sealed class Crashes(Dictionary<(Guid Chunk, string Failpoint), int> plan) : IFaultInjector
    {
        public int Crashed { get; private set; }

        public ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken)
        {
            var key = (((JobChunkMessage)context.Message!.Payload).ChunkId, failpoint);
            if (plan.TryGetValue(key, out var left) && left > 0)
            {
                plan[key] = left - 1;
                Crashed++;
                throw new SimulatedCrashException($"Crash at {failpoint}.");
            }

            return ValueTask.CompletedTask;
        }
    }
#endif

    private static async Task AppExecuteAsync(ProductionHarness h, Guid ws, string sql, Guid productionId)
    {
        await using var connection = await h.Db.AppDataSource.OpenConnectionAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.workspace_id', @ws, true)", connection, tx))
        {
            context.Parameters.AddWithValue("ws", ws.ToString());
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("ws", ws);
        command.Parameters.AddWithValue("id", productionId);
        await command.ExecuteNonQueryAsync(Ct);
        await tx.CommitAsync(Ct);
    }
}
