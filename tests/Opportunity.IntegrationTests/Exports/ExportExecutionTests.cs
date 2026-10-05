using System.Diagnostics;
using System.Globalization;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.Volumes;
using Opportunity.IntegrationTests.Import;
using Opportunity.IntegrationTests.Migrations;
#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif
using Opportunity.Production.Exports;

namespace Opportunity.IntegrationTests.Exports;

/// <summary>
/// E12-T01 acceptance against PostgreSQL (app login, RLS on) and a file-system object store: identical manifests on a
/// re-run (whatever the chunking), the Q-15 re-check per chunk (restriction classes, walls; excluded, reported and
/// audited), cancellation when the initiator loses Export.Create, and resumption after a worker crash mid-chunk.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ExportExecutionTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Re-running an export of the same frozen set yields identical manifest checksums. The default size keeps CI fast;
    /// set <c>OPPORTUNITY_EXPORT_SCALE_DOCS=100000</c> for the acceptance run of a 100K-document frozen set.
    /// </summary>
    [Fact]
    public async Task Re_running_an_export_of_a_frozen_set_yields_identical_manifest_checksums_whatever_the_chunking()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("OPPORTUNITY_EXPORT_SCALE_DOCS"), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : 1_200;
        await using var h = await ExportHarness.CreateAsync(postgres, documentsPerChunk: 250);
        var ws = await h.Import.WorkspaceAsync();
        var custodian = (await h.Db.Fields.CreateFieldAsync(new NewField(ws, "Custodian", FieldType.Keyword, FieldStorage.Metadata), Ct)).Value!.FieldId;
        var ids = await BulkDocumentsAsync(h, ws, count, custodian);
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);

        // A frozen set of every document (explicit IDs are capped at 10,000 per request).
        var snapshot = count <= 10_000 ? await h.SnapshotAsync(ws, user, ids) : await FreezeAllAsync(h, ws, user, ids);

        snapshot.DocumentCount.Should().Be(count);
        var request = new CreateExportRequest(snapshot.SnapshotId,
            [new(FieldId: SystemFields.ControlNumber), new(FieldId: custodian), new(FieldId: SystemFields.BegAttach), new(FieldId: SystemFields.EndAttach),
             new(Column: ExportColumnResource.FamilyId), new(Column: ExportColumnResource.ParentId)]);

        var clock = Stopwatch.StartNew();
        var first = await h.RunAsync(await h.CreateAsync(ws, user, request));
        var elapsed = clock.Elapsed;
        h.Options = new ExportJobOptions { DocumentsPerChunk = 97, WorkerId = "export-test-worker-2" };
        var second = await h.RunAsync(await h.CreateAsync(ws, user, request));

        first.Status.Should().Be(ExportStatus.Completed);
        second.Status.Should().Be(ExportStatus.Completed);
        first.Report!.DocumentsExported.Should().Be(count);
        second.Report!.ManifestSha256.Should().Equal(first.Report.ManifestSha256, "identical manifests on a re-run");
        var a = await h.FilesAsync(first);
        var b = await h.FilesAsync(second);
        b.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(f.Value.File.Sha256))
            .Should().BeEquivalentTo(a.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(f.Value.File.Sha256)));
        ExportRoundTripTests.AssertManifest(a, first);
        (await h.Import.Jobs.GetChunksAsync(ws, first.JobId, cancellationToken: Ct)).Should().HaveCount((count + 249) / 250);

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Exported {count} documents in {elapsed.TotalSeconds:F1} s ({count / elapsed.TotalSeconds:F0} documents/s, metadata-only, 250 per chunk); manifests identical."));
    }

    [Fact]
    public async Task Documents_restricted_or_walled_after_the_freeze_are_excluded_reported_and_audited()
    {
        await using var h = await ExportHarness.CreateAsync(postgres, documentsPerChunk: 4);
        var ws = await h.Import.WorkspaceAsync();
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = new List<Guid>();
        for (var i = 1; i <= 10; i++)
        {
            docs.Add((await h.Db.InsertDocumentAsync(ws, $"EXP{i:D4}")).DocumentId);
        }

        var snapshot = await h.SnapshotAsync(ws, user, docs);

        // After the freeze: a restriction class nobody is granted on two documents, an ethical wall around one.
        await h.Db.ExecuteAsync("INSERT INTO opportunity.restriction_class (workspace_id, class_key, display_name) VALUES (@ws, 'Hot', 'Hot documents')", ("ws", ws));
        foreach (var doc in new[] { docs[1], docs[4] })
        {
            await h.Db.ExecuteAsync("INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key) VALUES (@ws, @doc, 'Hot')", ("ws", ws), ("doc", doc));
        }

        var wall = Guid.CreateVersion7();
        await h.Db.ExecuteAsync("INSERT INTO opportunity.ethical_wall (workspace_id, wall_id, name) VALUES (@ws, @wall, 'Matter wall')", ("ws", ws), ("wall", wall));
        await h.Db.ExecuteAsync(
            "INSERT INTO opportunity.ethical_wall_member (workspace_id, wall_id, member_id, user_id) VALUES (@ws, @wall, @id, @user)",
            ("ws", ws), ("wall", wall), ("id", Guid.CreateVersion7()), ("user", user));
        await h.Db.ExecuteAsync("INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) VALUES (@ws, @doc, @wall)",
            ("ws", ws), ("doc", docs[6]), ("wall", wall));

        var creation = await h.CreateAsync(ws, user, new CreateExportRequest(snapshot.SnapshotId, [new(FieldId: SystemFields.ControlNumber)]));
        var export = await h.RunAsync(creation);

        export.Status.Should().Be(ExportStatus.Completed);
        export.Report!.DocumentsExported.Should().Be(7);
        export.Report.DocumentsExcluded.Should().Be(3);
        var job = (await h.Import.Jobs.GetAsync(ws, creation.Job.JobId, Ct))!;
        job.Status.Should().Be(JobStatus.Completed);
        job.Counters.ItemsExcludedNoAccess.Should().Be(3);
        job.Counters.ItemsApplied.Should().Be(7);

        var excluded = await h.Exports.GetExclusionsAsync(ws, export.ExportId, 0, 100, Ct);
        excluded.Select(e => (e.ControlNumber, e.Reason)).Should().Equal(("EXP0002", "AccessChanged"), ("EXP0005", "AccessChanged"), ("EXP0007", "AccessChanged"));
        var items = await h.Import.Jobs.GetItemResultsAsync(new Application.Jobs.JobItemResultQuery(ws, creation.Job.JobId), Ct);
        items.Select(i => i.Result.DocumentId).Should().BeEquivalentTo([docs[1], docs[4], docs[6]]);
        items.Should().OnlyContain(i => i.Result.Kind == JobItemResultKind.ExcludedNoAccess && i.Result.ReasonCode == "AccessChanged");

        // The volume reflects its actual contents; the report lists the exclusions by control number.
        var files = await h.FilesAsync(export);
        ExportRoundTripTests.AssertManifest(files, export);
        var dat = await ExportRoundTripTests.ParseDatAsync(files["VOL001/DATA/VOL001.dat"].Bytes);
        dat.Rows.Select(r => r[0]).Should().Equal("EXP0001", "EXP0003", "EXP0004", "EXP0006", "EXP0008", "EXP0009", "EXP0010");
        var report = Encoding.UTF8.GetString(files[ExportLayout.ExclusionsCsv].Bytes).TrimStart('﻿');
        report.Should().Be(
            "Control Number,Document ID,Reason\r\n"
            + $"EXP0002,{docs[1]},AccessChanged\r\nEXP0005,{docs[4]},AccessChanged\r\nEXP0007,{docs[6]},AccessChanged\r\n");

        // Audit: created, the exclusions with the precise reason (never shown to the requester), completed.
        var events = await h.Db.ColumnAsync(
            $"SELECT action || '|' || coalesce(details->>'Documents', '') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Export' ORDER BY occurred_at, action");
        events.Should().Contain(e => e.StartsWith("Created|", StringComparison.Ordinal));
        events.Should().Contain(e => e.StartsWith("Completed|", StringComparison.Ordinal));
        var exclusions = string.Join(',', events.Where(e => e.StartsWith("DocumentsExcluded|", StringComparison.Ordinal)).Select(e => e.Split('|')[1]));
        exclusions.Should().Contain(docs[1].ToString("N") + ":RestrictionClass").And.Contain(docs[4].ToString("N") + ":RestrictionClass")
            .And.Contain(docs[6].ToString("N") + ":EthicalWall");
    }

    [Fact]
    public async Task An_initiator_who_loses_Export_Create_has_the_remaining_chunks_cancelled()
    {
        await using var h = await ExportHarness.CreateAsync(postgres, documentsPerChunk: 2);
        var ws = await h.Import.WorkspaceAsync();
        var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = new List<Guid>();
        for (var i = 1; i <= 6; i++)
        {
            docs.Add((await h.Db.InsertDocumentAsync(ws, $"REV{i:D4}")).DocumentId);
        }

        var snapshot = await h.SnapshotAsync(ws, user, docs);
        var creation = await h.CreateAsync(ws, user, new CreateExportRequest(snapshot.SnapshotId, [new(FieldId: SystemFields.ControlNumber)]));
        await h.CoordinateAsync(ws);
        await h.Db.ExecuteAsync("DELETE FROM opportunity.workspace_role_assignment WHERE workspace_id = @ws AND user_id = @user", ("ws", ws), ("user", user));
        await h.Db.ExecuteAsync(
            "INSERT INTO opportunity.workspace_role_assignment (workspace_id, assignment_id, role, user_id) VALUES (@ws, @id, 'Reviewer', @user)",
            ("ws", ws), ("id", Guid.CreateVersion7()), ("user", user));

        await h.DeliverOpenChunksAsync(ws, creation.Job.JobId);
        await h.CoordinateAsync(ws);

        (await h.Import.Jobs.GetAsync(ws, creation.Job.JobId, Ct))!.Status.Should().Be(JobStatus.Cancelled);
        (await h.Exports.GetAsync(ws, creation.Export.ExportId, Ct))!.Status.Should().Be(ExportStatus.Cancelled);
        (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.export_document WHERE workspace_id = @ws", ("ws", ws))).Should().Be(0);
    }

#if OPPORTUNITY_FAILPOINTS
    [Fact]
    public async Task A_worker_crash_mid_chunk_resumes_from_the_last_committed_chunk_and_yields_the_same_volume()
    {
        var root = ExportRoundTripTests.TempDirectory("opp-export-crash-");
        try
        {
            CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), 14), 20261006, root, new CorpusRunOptions
            {
                Threads = 1,
                WriteGroundTruth = false,
                SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions())],
            });
            await using var h = await ExportHarness.CreateAsync(postgres, documentsPerChunk: 4);
            h.Import.Volumes = new ImportVolumeOptions { VolumeShareRoot = root };
            var ws = await h.Import.WorkspaceAsync();
            var batch = await h.Import.StartAsync(ws, await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.dat"), Ct),
                new() { Paths = new() { VolumeRoot = "VOL001" } }, name: "VOL001.dat",
                opt: await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.opt"), Ct));
            (await h.Import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
            var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
            var snapshot = await h.SnapshotAsync(ws, user, await ExportRoundTripTests.DocumentIdsAsync(h, ws));
            var request = new CreateExportRequest(snapshot.SnapshotId,
                [new(FieldId: SystemFields.ControlNumber), new(FieldId: SystemFields.FileName), new(Column: ExportColumnResource.ParentId)]);

            var clean = await h.RunAsync(await h.CreateAsync(ws, user, request));
            clean.Status.Should().Be(ExportStatus.Completed);

            // The crashing run: chunk 1 commits; the worker dies in chunk 2 after some of its files were written.
            var creation = await h.CreateAsync(ws, user, request);
            await h.CoordinateAsync(ws);
            var chunks = await h.Import.Jobs.GetChunksAsync(ws, creation.Job.JobId, cancellationToken: Ct);
            chunks.Should().HaveCount(4);
            var crashing = new CrashingStore(h.Store) { CrashAfterPuts = int.MaxValue };
            await h.Consumer(store: crashing).HandleAsync(ExportHarness.Payload(chunks[0]), ExportHarness.Received(chunks[0]), Ct);
            crashing.CrashAfterPuts = 3;
            var crash = () => h.Consumer(store: crashing).HandleAsync(ExportHarness.Payload(chunks[1]), ExportHarness.Received(chunks[1]), Ct);
            await crash.Should().ThrowAsync<SimulatedCrashException>();
            var states = (await h.Import.Jobs.GetChunksAsync(ws, creation.Job.JobId, cancellationToken: Ct)).ToDictionary(c => c.Sequence);
            states[1].Status.Should().Be(JobChunkStatus.Committed);
            states[2].Status.Should().Be(JobChunkStatus.Running, "the worker died holding the lease");
            (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.export_document WHERE export_id = @id", ("id", creation.Export.ExportId)))
                .Should().Be(4, "only chunk 1 committed");

            // Recovery: the lease expires, the sweeper returns chunk 2 to Pending, and the remaining chunks run.
            await h.Db.ExecuteAsync(
                "UPDATE opportunity.job_chunk SET lease_expires_at = now() - interval '1 minute' WHERE workspace_id = @ws AND status = 3", ("ws", ws));
            (await h.Import.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).ReturnedToPending.Should().Be(1);
            (await h.DeliverOpenChunksAsync(ws, creation.Job.JobId)).Should().Be(3, "chunk 1 is not redone");
            await h.CoordinateAsync(ws);

            var resumed = (await h.Exports.GetAsync(ws, creation.Export.ExportId, Ct))!;
            resumed.Status.Should().Be(ExportStatus.Completed);
            resumed.Report!.ManifestSha256.Should().Equal(clean.Report!.ManifestSha256, "the resumed volume is the clean run's, byte for byte");
            (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.export_file WHERE export_id = @id AND chunk_sequence = 2 AND kind = 8", ("id", creation.Export.ExportId)))
                .Should().Be(1, "only the committed attempt of chunk 2 is registered");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An object store that "kills the worker" (a simulated crash) after a number of puts.</summary>
    private sealed class CrashingStore(IObjectStore inner) : IObjectStore
    {
        private int _puts;

        public int CrashAfterPuts { get; set; }

        public async Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _puts) > CrashAfterPuts)
            {
                throw new SimulatedCrashException("The export worker died mid-chunk.");
            }

            return await inner.PutAsync(key, content, options, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);

        public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            inner.ListPrefixAsync(prefix, cancellationToken);

        public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            inner.DeletePrefixAsync(prefix, cancellationToken);
    }
#endif

    /// <summary>
    /// <paramref name="count"/> metadata-only documents in families of three (a parent and two attachments), with a
    /// custodian, inserted in bulk; returns their IDs in control-number order.
    /// </summary>
    private static async Task<List<Guid>> BulkDocumentsAsync(ExportHarness h, Guid ws, int count, int custodianField)
    {
        await h.Db.ExecuteAsync(
            $$"""
            WITH n AS (SELECT g AS n, gen_random_uuid() AS id FROM generate_series(1, @count) AS g),
                 f AS (SELECT n.n, n.id, first_value(n.id) OVER (PARTITION BY (n.n - 1) / 3 ORDER BY n.n) AS family FROM n)
            INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id, parent_document_id,
                                              family_sequence, metadata)
            SELECT @ws, f.id, 'SCALE' || lpad(f.n::text, 7, '0'), 'SCALE' || lpad(f.n::text, 7, '0'), f.family,
                   CASE WHEN f.id = f.family THEN NULL ELSE f.family END, (f.n - 1) % 3,
                   jsonb_build_object('f{{custodianField}}', 'Custodian ' || (f.n % 17))
            FROM f
            """,
            ("ws", ws), ("count", count));
        await h.Db.ExecuteAsync(
            "INSERT INTO opportunity.document_projection_state (workspace_id, document_id) SELECT workspace_id, document_id FROM opportunity.document WHERE workspace_id = @ws",
            ("ws", ws));
        return [.. (await h.Db.ColumnAsync($"SELECT document_id::text FROM opportunity.document WHERE workspace_id = '{ws}' ORDER BY control_number")).Select(Guid.Parse)];
    }

    /// <summary>A frozen set of more documents than one request may name: header, staging and freeze through the store.</summary>
    private static async Task<Application.Snapshots.SnapshotRecord> FreezeAllAsync(ExportHarness h, Guid ws, Guid user, List<Guid> ids)
    {
        const string Owner = "export-scale-test";
        var created = await h.Snapshots.CreateAsync(new Application.Snapshots.NewSnapshot
        {
            WorkspaceId = ws,
            Name = "All documents",
            Purpose = Core.Snapshots.SnapshotPurpose.Export,
            SourceKind = Core.Snapshots.SnapshotSourceKind.DocumentIds,
            RequestedCount = ids.Count,
            CreatedBy = user,
            CreatedByDisplay = "Export Tester",
            ClaimOwner = Owner,
            ClaimLease = TimeSpan.FromMinutes(30),
        }, Ct);
        foreach (var batch in ids.Chunk(10_000))
        {
            await h.Snapshots.StageAsync(ws, created.Snapshot.SnapshotId, batch, Core.Snapshots.SnapshotInclusionReason.Explicit, Ct);
        }

        var frozen = await h.Snapshots.FreezeAsync(
            new Application.Snapshots.SnapshotFreezeRequest { WorkspaceId = ws, SnapshotId = created.Snapshot.SnapshotId, ClaimOwner = Owner },
            (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()), Ct);
        return frozen.Snapshot!;
    }
}
