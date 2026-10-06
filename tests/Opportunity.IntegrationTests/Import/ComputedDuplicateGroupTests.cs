using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Opportunity.Application.Authorization;
using Opportunity.Application.Documents.Dedupe;
using Opportunity.Application.Jobs;
using Opportunity.Application.Messaging;
using Opportunity.Application.Telemetry;
using Opportunity.Contracts.Import;
using Opportunity.Contracts.Messaging;
using Opportunity.Contracts.Messaging.Jobs;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Data.Relationships;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Jobs;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E09-T04 acceptance: computed duplicate groups are family-level (parents compared, attachments follow), keyed on the
/// selected hash with that hash recorded on each group, Global or Custodial, never rewrite upstream groups, match the
/// generator's ground truth on the 20%-duplicate corpus, and are stable on re-runs and re-imports. Runs go through the
/// real job path: the API's <see cref="DedupeService"/>, then the chunk consumer with <see cref="DedupeChunkExecutor"/>.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ComputedDuplicateGroupTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Small for CI; set OPPORTUNITY_DEDUPE_CORPUS (e.g. 20000) for a larger opt-in run.</summary>
    private static int CorpusSize =>
        int.TryParse(Environment.GetEnvironmentVariable("OPPORTUNITY_DEDUPE_CORPUS"), out var n) && n > 0 ? n : 600;

    [Fact]
    public async Task Groups_match_the_generator_ground_truth_on_the_20_percent_duplicate_corpus_and_are_stable()
    {
        var root = Path.Combine(Path.GetTempPath(), "opp-dedupe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var truth = new FamilyTruthSink();
            var profile = ProfileSerializer.WithDocumentCount(new CorpusProfile(), CorpusSize);
            profile.Duplicates.Rate.Should().Be(0.20, "the 20%-duplicate corpus is the default profile");
            CorpusRunner.Run(profile, 20261006, root, new CorpusRunOptions
            {
                Threads = 2,
                WriteGroundTruth = false,
                ExtraSinks = [truth],
                SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions { IncludeNatives = false, IncludeText = false, IncludeImages = false })],
            });
            var dat = await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.dat"), Ct);
            truth.Families.Should().Contain(f => f.DuplicateGroup != null, "the corpus has whole-family duplicates");

            await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 250);
            var dedupe = new DedupeHarness(h);
            var ws = await h.WorkspaceAsync();

            // Upstream DuplicateGroupID is left out so every family is computed (upstream groups are tested below).
            var noUpstream = new ImportProfileDefinition { Columns = [new ColumnMapping { Column = "DuplicateGroupID", Ignore = true }] };
            (await h.RunAsync(await h.StartAsync(ws, dat, noUpstream, name: "VOL001.dat"))).Status.Should().Be(JobStatus.Completed);

            // Global, SHA-256: one group per duplicated family set; the hash used is on every group.
            await dedupe.SaveAsync(ws, new DedupePolicy(true, DedupeHashSource.Sha256));
            var first = await dedupe.RunAsync(ws);
            first.Status.Should().Be(JobStatus.Completed);
            var global = await GroupsAsync(h, ws);
            Partition(global).Should().BeEquivalentTo(truth.Expected(byCustodian: false));
            await AssertGroupsAsync(h, ws, global, DuplicateHashKind.Sha256Native, DuplicateGroupScope.Global);
            first.Counters.ItemsApplied.Should().Be(global.Count(d => d.Value.Group is not null));
            (await IndexedDocumentsAsync(h, ws, first.JobId)).Should().Be(first.Counters.ItemsApplied, "every changed document is reindexed");
            var summary = (await dedupe.Store.GetAsync(ws, Ct)).LastRun!;
            summary.Groups.Should().Be(truth.Expected(byCustodian: false).Count);
            summary.DocumentsChanged.Should().Be(first.Counters.ItemsApplied);

            // Re-running changes nothing and creates no index work.
            var again = await dedupe.RunAsync(ws);
            again.Counters.ItemsApplied.Should().Be(0);
            (await IndexedDocumentsAsync(h, ws, again.JobId)).Should().Be(0);

            // Stable on re-import: overlaying the same volume and re-running keeps every id and flag.
            var overlay = await h.StartAsync(ws, dat, noUpstream, ImportMode.Overlay, name: "VOL001-again.dat");
            (await h.RunAsync(overlay)).Status.Should().Be(JobStatus.Completed);
            (await dedupe.RunAsync(ws)).Counters.ItemsApplied.Should().Be(0);
            (await GroupsAsync(h, ws)).Should().BeEquivalentTo(global);

            // The load file's MD5 groups the same families, recorded as Md5.
            await dedupe.SaveAsync(ws, new DedupePolicy(true, DedupeHashSource.Md5));
            await dedupe.RunAsync(ws);
            var md5 = await GroupsAsync(h, ws);
            Partition(md5).Should().BeEquivalentTo(truth.Expected(byCustodian: false));
            await AssertGroupsAsync(h, ws, md5, DuplicateHashKind.Md5, DuplicateGroupScope.Global);

            // Custodial: copies held by different custodians stay apart; the global groups are gone.
            await dedupe.SaveAsync(ws, new DedupePolicy(true, DedupeHashSource.Sha256, DuplicateGroupScope.Custodial));
            (await dedupe.RunAsync(ws)).Status.Should().Be(JobStatus.Completed);
            var custodial = await GroupsAsync(h, ws);
            Partition(custodial).Should().BeEquivalentTo(truth.Expected(byCustodian: true));
            await AssertGroupsAsync(h, ws, custodial, DuplicateHashKind.Sha256Native, DuplicateGroupScope.Custodial);
            (await h.CountAsync("SELECT count(*) FROM opportunity.duplicate_group WHERE workspace_id = @ws AND scope = 1", ws)).Should().Be(0);

            // Disabled: a run removes every computed group and primary flag.
            await dedupe.SaveAsync(ws, new DedupePolicy(false));
            await dedupe.RunAsync(ws);
            (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND (duplicate_group_id IS NOT NULL OR is_duplicate_primary)", ws))
                .Should().Be(0);
            (await h.CountAsync("SELECT count(*) FROM opportunity.duplicate_group WHERE workspace_id = @ws", ws)).Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Auto_prefers_the_upstream_hash_attachments_follow_their_parent_and_upstream_groups_are_never_rewritten()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 4);
        var dedupe = new DedupeHarness(h);
        var ws = await h.WorkspaceAsync();
        string[] header = ["ControlNumber", "BegAttach", "EndAttach", "DedupeHash", "SHA256Hash", "DuplicateGroupID", "Custodian"];
        var x = new string('a', 64);
        var y = new string('b', 64);
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            header,
            ["A0001", "A0001", "A0002", "0f0f", x, "", "Smith"],
            ["A0002", "A0001", "A0002", "", new string('1', 64), "", "Smith"],
            ["B0001", "B0001", "B0002", "0f0f", y, "", "Jones"],
            ["B0002", "B0001", "B0002", "", new string('2', 64), "", "Jones"],
            ["C0001", "", "", "", x, "", "Smith"],
            ["D0001", "", "", "", x, "", "Jones"],
            ["E0001", "", "", "0f0f", x, "UP-1", "Smith"],
            ["E0002", "", "", "", "", "UP-1", "Smith"],
            ["F0001", "", "", "", "", "", "Smith"]));
        (await h.RunAsync(await h.StartAsync(ws, dat))).Status.Should().Be(JobStatus.Completed);
        var upstream = RelationshipIds.DuplicateGroup(ws, DuplicateHashKind.UpstreamGroup, "UP-1");

        await dedupe.SaveAsync(ws, new DedupePolicy(true));
        var run = await dedupe.RunAsync(ws);
        run.Status.Should().Be(JobStatus.Completed);
        var groups = await GroupsAsync(h, ws);

        // The dedupe hash wins over SHA-256 (Q-09/Q-63): families A and B group on it, their attachments with them.
        var byDedupe = RelationshipIds.DuplicateGroup(ws, DuplicateHashKind.UpstreamDedupeHash, "0f0f");
        Docs(groups, "A0001", "A0002", "B0001", "B0002").Select(r => r.Group).Should().OnlyContain(g => g == byDedupe);
        Docs(groups, "A0001", "A0002").Select(r => r.Primary).Should().OnlyContain(p => p, "the A family has the lowest control number");
        Docs(groups, "B0001", "B0002").Select(r => r.Primary).Should().OnlyContain(p => !p);

        // Without a dedupe hash, SHA-256 of the native: C and D (A shares the SHA-256 but is keyed on its dedupe hash).
        var bySha = RelationshipIds.DuplicateGroup(ws, DuplicateHashKind.Sha256Native, x);
        groups["C0001"].Group.Should().Be(bySha);
        groups["D0001"].Group.Should().Be(bySha);

        // Upstream groups are left as imported, and a document without any hash is not grouped.
        groups["E0001"].Group.Should().Be(upstream);
        groups["E0002"].Group.Should().Be(upstream);
        groups["F0001"].Group.Should().BeNull();
        var summary = (await dedupe.Store.GetAsync(ws, Ct)).LastRun!;
        summary.Should().Match<DedupeRunSummary>(s => s.Groups == 2 && s.FamiliesCompared == 4 && s.FamiliesWithoutHash == 1
            && s.FamiliesWithUpstreamGroup == 2 && s.DocumentsGrouped == 6 && s.DocumentsChanged == 6);
        await AssertConsistentAsync(h, ws);

        // Custodial: Smith's C and Jones's D part; A (Smith) and B (Jones) part too, so nothing is grouped.
        await dedupe.SaveAsync(ws, new DedupePolicy(true, Scope: DuplicateGroupScope.Custodial));
        await dedupe.RunAsync(ws);
        groups = await GroupsAsync(h, ws);
        groups.Where(g => g.Value.Group is not null).Select(g => g.Key).Should().BeEquivalentTo(["E0001", "E0002"]);
        groups.Values.Where(g => g.Group is null).Should().OnlyContain(g => !g.Primary);

        // A second Smith copy of A's content in a later load joins A's custodial group; the existing primary stays.
        var later = ImportHarness.Utf8Bom(ImportHarness.Dat(header, ["G0001", "", "", "0f0f", y, "", "Smith"]));
        (await h.RunAsync(await h.StartAsync(ws, later, name: "later.dat"))).Status.Should().Be(JobStatus.Completed);
        var joined = await dedupe.RunAsync(ws);
        groups = await GroupsAsync(h, ws);
        var smith = RelationshipIds.CustodialDuplicateGroup(ws, DuplicateHashKind.UpstreamDedupeHash, "0f0f", "Smith");
        Docs(groups, "A0001", "A0002", "G0001").Select(r => r.Group).Should().OnlyContain(g => g == smith);
        groups["A0001"].Primary.Should().BeTrue();
        groups["G0001"].Primary.Should().BeFalse();
        joined.Counters.ItemsApplied.Should().Be(3, "A's two documents and G joined the new group");
        var stored = (await new DocumentRelationshipRepository(h.Db.AppDataSource).GetDuplicateGroupAsync(ws, smith, Ct))!;
        stored.Should().Match<Application.Documents.DuplicateGroupInfo>(g =>
            g.Source == DuplicateGroupSource.Computed && g.HashKind == DuplicateHashKind.UpstreamDedupeHash && g.HashValue == "0f0f" && g.MemberCount == 3);
        await AssertConsistentAsync(h, ws);
    }

    [Fact]
    public async Task A_custodial_policy_needs_a_custodian_field_and_a_retried_run_returns_the_same_job()
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var dedupe = new DedupeHarness(h);
        var ws = await h.WorkspaceAsync();

        var invalid = await dedupe.Service.SaveAsync(ws, 0, new DedupePolicy(true, Scope: DuplicateGroupScope.Custodial), DedupeHarness.Admin, Ct);
        invalid.Status.Should().Be(DedupeOutcomeStatus.Invalid);
        invalid.Errors.Should().ContainKey("custodianFieldId");
        (await dedupe.Service.SaveAsync(ws, 0, new DedupePolicy(true, CustodianFieldId: 1), DedupeHarness.Admin, Ct))
            .Errors.Should().ContainKey("custodianFieldId", "Control Number cannot hold a custodian");

        var saved = await dedupe.Service.SaveAsync(ws, 0, new DedupePolicy(true), DedupeHarness.Admin, Ct);
        saved.Record!.Version.Should().Be(1);
        (await dedupe.Service.SaveAsync(ws, 0, new DedupePolicy(false), DedupeHarness.Admin, Ct)).Status.Should().Be(DedupeOutcomeStatus.VersionConflict);
        (await h.CountAsync("SELECT count(*) FROM audit.audit_event WHERE workspace_id = @ws AND resource_type = 'DedupePolicy'", ws)).Should().Be(1);

        var a = await dedupe.Service.StartRunAsync(ws, DedupeHarness.Admin, "run-1", Ct);
        var b = await dedupe.Service.StartRunAsync(ws, DedupeHarness.Admin, "run-1", Ct);
        b.Job!.JobId.Should().Be(a.Job!.JobId);
        a.Job.JobType.Should().Be(JobType.RelationshipFixup);
        (await h.Jobs.GetChunksAsync(ws, a.Job.JobId, cancellationToken: Ct)).Should().ContainSingle()
            .Which.Membership.Should().Be(DedupeService.WholeWorkspace);
        await dedupe.Service.SaveAsync(ws, 1, new DedupePolicy(true, DedupeHashSource.Md5), DedupeHarness.Admin, Ct);
        (await dedupe.Service.StartRunAsync(ws, DedupeHarness.Admin, "run-1", Ct)).Status.Should().Be(DedupeOutcomeStatus.IdempotencyKeyReuse);
    }

    private sealed record Row(string ControlNumber, string Family, Guid? Group, bool Primary);

    /// <summary>Every document by upper-case control number: its family root, group and primary flag (superuser read).</summary>
    private static async Task<Dictionary<string, Row>> GroupsAsync(ImportHarness h, Guid ws)
    {
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        await using var command = h.Db.DataSource.CreateCommand(
            """
            SELECT d.control_number, f.control_number, d.duplicate_group_id, d.is_duplicate_primary
            FROM opportunity.document d
            JOIN opportunity.document f ON f.workspace_id = d.workspace_id AND f.document_id = d.family_id
            WHERE d.workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var cn = reader.GetString(0).ToUpperInvariant();
            rows[cn] = new Row(cn, reader.GetString(1).ToUpperInvariant(), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetBoolean(3));
        }

        return rows;
    }

    private static IEnumerable<Row> Docs(Dictionary<string, Row> rows, params string[] controlNumbers) => controlNumbers.Select(c => rows[c]);

    /// <summary>The groups as sets of family roots.</summary>
    private static List<List<string>> Partition(Dictionary<string, Row> rows) =>
        [.. rows.Values.Where(r => r.Group is not null).GroupBy(r => r.Group).Select(g => g.Select(r => r.Family).Distinct().Order(StringComparer.Ordinal).ToList())];

    /// <summary>Every group: computed with the expected hash and scope, all of a family inside, the R15 primary family flagged.</summary>
    private static async Task AssertGroupsAsync(ImportHarness h, Guid ws, Dictionary<string, Row> rows, DuplicateHashKind kind, DuplicateGroupScope scope)
    {
        rows.Values.GroupBy(r => r.Family).Should().OnlyContain(f => f.Select(r => r.Group).Distinct().Count() == 1, "attachments follow their parent");
        (await h.CountAsync(
            $"SELECT count(*) FROM opportunity.duplicate_group WHERE workspace_id = @ws AND NOT (source = 2 AND hash_kind = {(short)kind} AND scope = {(short)scope})", ws))
            .Should().Be(0, "the hash used and the scope are recorded on each group");
        (await h.CountAsync(
            """
            SELECT count(*) FROM opportunity.duplicate_group g
            JOIN opportunity.document p ON p.workspace_id = g.workspace_id AND p.document_id = g.primary_family_id
            WHERE g.workspace_id = @ws
              AND (g.hash_value <> CASE g.hash_kind WHEN 4 THEN encode(p.sha256, 'hex') WHEN 5 THEN encode(p.md5, 'hex') ELSE g.hash_value END
                   OR EXISTS (SELECT FROM opportunity.document o
                              WHERE o.workspace_id = g.workspace_id AND o.duplicate_group_id = g.duplicate_group_id AND o.family_sequence = 0
                                AND (o.family_date, o.control_number_sort_key) < (p.family_date, p.control_number_sort_key)))
            """, ws)).Should().Be(0, "the primary is the earliest family date, then the lowest control number, and its hash is the group's");
        foreach (var group in rows.Values.Where(r => r.Group is not null).GroupBy(r => r.Group))
        {
            group.Where(r => r.Primary).Select(r => r.Family).Distinct().Should().ContainSingle("one primary family per group");
        }

        await AssertConsistentAsync(h, ws);
    }

    private static async Task AssertConsistentAsync(ImportHarness h, Guid ws)
    {
        var report = await new DocumentRelationshipRepository(h.Db.AppDataSource).CheckConsistencyAsync(ws, cancellationToken: Ct);
        report.IsConsistent.Should().BeTrue(string.Join("; ", report.Findings.Where(f => f.Count > 0).Select(f => $"{f.Kind}: {string.Join(", ", f.Sample)}")));
    }

    private static Task<long> IndexedDocumentsAsync(ImportHarness h, Guid ws, Guid jobId) => h.Db.ScalarAsync<long>(
        """
        SELECT count(DISTINCT d) FROM opportunity.index_chunk_task t, unnest(t.document_ids) AS d
        WHERE t.workspace_id = @ws AND t.job_id = @job AND t.task_kind = 3 AND t.change_mask & 16 = 16
        """, ("ws", ws), ("job", jobId));

    /// <summary>The API's use case and the import worker's consumer for RelationshipChunks.</summary>
    private sealed class DedupeHarness(ImportHarness h)
    {
        public static readonly SecurityPrincipal Admin = new() { UserId = ImportHarness.User, DisplayName = "Workspace admin" };

        public DedupeStore Store { get; } = new(h.Db.AppDataSource);

        public DedupeService Service => new(Store, h.Jobs, h.Db.Fields);

        public async Task SaveAsync(Guid ws, DedupePolicy policy)
        {
            var version = (await Store.GetAsync(ws, Ct)).Version;
            (await Service.SaveAsync(ws, version, policy, Admin, Ct)).Status.Should().Be(DedupeOutcomeStatus.Ok);
        }

        /// <summary>Submits a run and delivers its chunk once, like the dispatcher and the import worker.</summary>
        public async Task<JobInfo> RunAsync(Guid ws)
        {
            var started = await Service.StartRunAsync(ws, Admin, Guid.NewGuid().ToString("N"), Ct);
            started.Status.Should().Be(DedupeOutcomeStatus.Ok);
            var consumer = new JobChunkConsumer(
                h.Chunks, [new DedupeChunkExecutor(Store)], h.RejectionAudit, new JobLeaseOptions(), new JobChunkConsumerOptions { WorkerId = "dedupe-test-worker" },
                NullMessageProcessingMeter.Instance, TimeProvider.System, NullLogger<JobChunkConsumer>.Instance);
            foreach (var chunk in await h.Jobs.GetChunksAsync(ws, started.Job!.JobId, cancellationToken: Ct))
            {
                var payload = new JobChunkMessage { ChunkId = chunk.ChunkId, Sequence = chunk.Sequence, Operation = JobChunkOperation.RelationshipChunk };
                await consumer.HandleAsync(payload, new ReceivedMessage(
                    new MessageEnvelope
                    {
                        MessageId = Guid.CreateVersion7(),
                        MessageType = MessageTypes.JobChunk,
                        SchemaVersion = new SchemaVersion(1, 0),
                        WorkspaceId = chunk.WorkspaceId,
                        JobId = chunk.JobId,
                        CorrelationId = $"corr-{chunk.JobId:N}",
                        IdempotencyKey = chunk.IdempotencyKey,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Attempt = chunk.AttemptCount,
                        Payload = JsonSerializer.SerializeToElement(payload, MessageJson.PayloadOptions),
                    },
                    payload, WorkQueues.Import, Redelivered: false, DeliveryCount: 0, TransportRetry: 0), Ct);
            }

            return (await h.Jobs.GetAsync(ws, started.Job.JobId, Ct))!;
        }
    }

    /// <summary>The generator's families in load order: root, custodian, documents and whole-family duplicate group.</summary>
    private sealed class FamilyTruthSink : ICorpusSink
    {
        public List<(string Root, string Custodian, string? DuplicateGroup)> Families { get; } = [];

        /// <summary>Ground-truth groups as sets of family roots: whole-family copies, per custodian when asked.</summary>
        public List<List<string>> Expected(bool byCustodian) =>
        [
            .. Families.Where(f => f.DuplicateGroup is not null)
                .GroupBy(f => byCustodian ? f.DuplicateGroup + "|" + f.Custodian : f.DuplicateGroup)
                .Where(g => g.Count() > 1)
                .Select(g => g.Select(f => f.Root).Order(StringComparer.Ordinal).ToList()),
        ];

        public void Write(GeneratedChunk chunk)
        {
            foreach (var family in chunk.Families)
            {
                Families.Add((family.Parent.ControlNumber.ToUpperInvariant(), family.Custodian, family.Parent.FamilyDuplicateGroupId));
            }
        }

        public IReadOnlyList<CorpusOutputFile> Complete() => [];

        public void Dispose()
        {
        }
    }
}
