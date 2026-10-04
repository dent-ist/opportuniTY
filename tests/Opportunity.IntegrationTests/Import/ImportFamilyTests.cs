using System.Globalization;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Documents;
using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Jobs;
using Opportunity.Data.Relationships;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.IntegrationTests.Api;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E09-T01 acceptance: families are rebuilt from BegAttach/EndAttach (A), ParentID (B) and GroupIdentifier (C) by each
/// import chunk's own transaction, converge when a family spans chunks and imports delivered in any order, are reindexed
/// through IndexChunkTasks when they change after their chunk, report orphans, gaps and conflicts, and match the
/// generator's ground truth on the 1:3 family corpus.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportFamilyTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] FamilyHeader = ["BEGDOC", "BegAttach", "EndAttach", "ParentID", "GroupIdentifier"];

    [Fact]
    public async Task The_demo_load_file_rebuilds_the_email_families_across_chunks()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 3);
        var ws = await h.WorkspaceAsync();
        var dat = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "deploy", "docker-compose", "seed", "demo-documents.dat"), Ct);
        var batch = await h.StartAsync(ws, dat, name: "demo-documents.dat");

        (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
        var families = await FamiliesAsync(h, ws);
        Members(families, "OPP0000000001").Should().Equal("OPP0000000001", "OPP0000000002", "OPP0000000003", "OPP0000000004");
        Members(families, "OPP0000000005").Should().Equal("OPP0000000005", "OPP0000000006", "OPP0000000007", "OPP0000000008");
        families["OPP0000000003"].Should().Match<FamilyRow>(f => f.Parent == "OPP0000000001" && f.Sequence == 2);
        families["OPP0000000009"].Should().Match<FamilyRow>(f => f.Family == "OPP0000000009" && f.Parent == null && f.Sequence == 0);
        families.Values.Should().OnlyContain(f => f.Status == FamilyStatus.Resolved);
        (await h.Batches.GetFamilyIssuesAsync(ws, batch.ImportBatchId, null, 100, Ct)).Should().BeEmpty();

        // Duplicate primaries are family-level: the 0001 family is primary for the groups it shares with 0005's.
        families["OPP0000000002"].IsDuplicatePrimary.Should().BeTrue();
        families["OPP0000000006"].IsDuplicatePrimary.Should().BeFalse();

        // Resolution is a pure function of the documents: re-resolving the whole workspace changes nothing.
        var full = await new DocumentRelationshipRepository(h.Db.AppDataSource).ResolveFamiliesAsync(ws, Ct);
        full.Should().Be(new FamilyResolutionSummary(10, 0, 0));
    }

    [Theory]
    [InlineData("range", false)]
    [InlineData("pointer", false)]
    [InlineData("group", false)]
    [InlineData("range", true)]
    [InlineData("pointer", true)]
    public async Task A_family_of_25_spanning_chunks_of_10_resolves_whatever_the_delivery_order(string mode, bool splitAcrossImports)
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 10);
        var ws = await h.WorkspaceAsync();
        var rows = Enumerable.Range(1, 25).Select(i => FamilyRow25(mode, i)).Append(["X0001", "X0001", "X0001", "", ""]).ToList();

        // The tail of the family arrives first: the last chunk (or the second import) is delivered before the first.
        var batches = new List<ImportBatchRecord>();
        if (splitAcrossImports)
        {
            var second = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat([FamilyHeader, .. rows.Skip(12)])), name: "vol002.dat");
            (await h.RunAsync(second)).Status.Should().Be(JobStatus.Completed);
            var interim = await FamiliesAsync(h, ws);
            interim["F0025"].Family.Should().NotBe("F0001");
            var first = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat([FamilyHeader, .. rows.Take(12)])), name: "vol001.dat");
            (await h.RunAsync(first)).Status.Should().Be(JobStatus.Completed);
            batches.AddRange([first, second]);
        }
        else
        {
            var batch = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat([FamilyHeader, .. rows])));
            await h.PrepareAsync(batch);
            var chunks = await h.Jobs.GetChunksAsync(ws, batch.JobId, cancellationToken: Ct);
            chunks.Should().HaveCount(3);
            var consumer = h.Consumer();
            await consumer.HandleAsync(ImportHarness.Payload(chunks[2]), ImportHarness.Received(chunks[2]), Ct);
            if (mode != "group")
            {
                (await FamiliesAsync(h, ws))["F0025"].Status.Should().Be(FamilyStatus.ParentMissing);
                (await h.Batches.GetFamilyIssuesAsync(ws, batch.ImportBatchId, null, 100, Ct))
                    .Should().Contain(i => i.ControlNumber == "F0025" && i.Kind == FamilyIssueKind.ParentMissing);
            }

            await consumer.HandleAsync(ImportHarness.Payload(chunks[1]), ImportHarness.Received(chunks[1]), Ct);
            await consumer.HandleAsync(ImportHarness.Payload(chunks[0]), ImportHarness.Received(chunks[0]), Ct);
            (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!.Status.Should().Be(JobStatus.Completed);
            batches.Add(batch);
        }

        var families = await FamiliesAsync(h, ws);
        Members(families, "F0001").Should().Equal(Enumerable.Range(1, 25).Select(i => $"F{i:D4}"));
        families.Values.Where(f => f.Family == "F0001").Select(f => f.Sequence).Order().Should().Equal(Enumerable.Range(0, 25));
        families["F0013"].Parent.Should().Be(mode == "pointer" ? "F0012" : "F0001", "ParentID names the immediate parent; other modes flatten");
        families["X0001"].Should().Match<FamilyRow>(f => f.Family == "X0001" && f.Sequence == 0);
        families.Values.Should().OnlyContain(f => f.Status == FamilyStatus.Resolved);
        foreach (var batch in batches)
        {
            (await h.Batches.GetFamilyIssuesAsync(ws, batch.ImportBatchId, null, 100, Ct)).Should().BeEmpty("the parent arrived");
        }

        // Every document whose family changed after its own chunk committed got a new version and a Relationship task.
        families.Values.Where(f => f.Version > 1).Should().NotBeEmpty();
        (await h.Db.ColumnAsync(
            $"""
            SELECT d.control_number FROM opportunity.document d
            JOIN opportunity.document_projection_state s USING (workspace_id, document_id)
            WHERE d.workspace_id = '{ws}' AND s.document_version > 1
              AND NOT EXISTS (SELECT FROM opportunity.index_chunk_task t
                              WHERE t.workspace_id = d.workspace_id AND t.task_kind = 3 AND t.change_mask & 16 = 16
                                AND d.document_id = ANY(t.document_ids))
            """)).Should().BeEmpty();

        (await new DocumentRelationshipRepository(h.Db.AppDataSource).ResolveFamiliesAsync(ws, Ct)).ChangedDocuments.Should().Be(0);
    }

    [Fact]
    public async Task The_family_report_lists_orphans_gaps_and_double_claims_until_later_loads_complete_them()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 4);
        var ws = await h.WorkspaceAsync();
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            FamilyHeader,
            ["O-1", "", "", "MISSING-9", ""],
            ["G0001", "G0001", "G0005", "", ""],
            ["G0002", "G0001", "G0005", "", ""],
            ["G0004", "G0001", "G0005", "", ""],
            ["G0005", "G0001", "G0005", "", ""],
            ["K0001", "K0001", "K0005", "", ""],
            ["K0002", "K0001", "K0005", "", ""],
            ["K0004", "K0004", "K0006", "", ""],
            ["K0005", "K0001", "K0005", "", ""],
            ["K0006", "K0004", "K0006", "", ""],
            ["V0001", "V0001", "W0003", "", ""],
            ["C1", "", "", "C2", ""],
            ["C2", "", "", "C1", ""]));
        var batch = await h.StartAsync(ws, dat);
        (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);

        var issues = await h.Batches.GetFamilyIssuesAsync(ws, batch.ImportBatchId, null, 100, Ct);
        issues.Select(i => (i.ControlNumber, i.Kind)).Should().BeEquivalentTo(
        [
            ("O-1", FamilyIssueKind.ParentMissing),
            ("G0001", FamilyIssueKind.RangeGap),
            ("K0001", FamilyIssueKind.RangeGap),
            ("K0004", FamilyIssueKind.ClaimedByTwoFamilies),
            ("K0005", FamilyIssueKind.ClaimedByTwoFamilies),
            ("V0001", FamilyIssueKind.InvalidRange),
            ("C1", FamilyIssueKind.Cycle),
            ("C2", FamilyIssueKind.Cycle),
        ]);
        issues.Single(i => i.ControlNumber == "G0001").MissingCount.Should().Be(1);
        issues.Single(i => i.ControlNumber == "K0005").Related.Should().Equal("K0001", "K0004");
        var families = await FamiliesAsync(h, ws);
        families["G0004"].Should().Match<FamilyRow>(f => f.Family == "G0001" && f.Status == FamilyStatus.Gap);
        families["K0006"].Family.Should().Be("K0001");
        families["C2"].Should().Match<FamilyRow>(f => f.Family == "C1" && f.Status == FamilyStatus.Conflict);

        // Over the API, in row order.
        await using (var factory = new ApiFactory())
        {
            using var client = factory.WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
                b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(h.Store)));
            }).CreateClient();
            var page = JsonDocument.Parse(await client.GetStringAsync(
                new Uri($"/api/v1/workspaces/{ws}/imports/{batch.ImportBatchId}/family-issues?limit=2", UriKind.Relative), Ct)).RootElement;
            page.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("row").GetInt64()}:{i.GetProperty("controlNumber").GetString()}:{i.GetProperty("kind").GetString()}:{i.GetProperty("familyStatus").GetString()}")
                .Should().Equal("1:O-1:parentMissing:parentMissing", "2:G0001:rangeGap:gap");
            var cursor = page.GetProperty("nextCursor").GetString()!;
            var rest = JsonDocument.Parse(await client.GetStringAsync(
                new Uri($"/api/v1/workspaces/{ws}/imports/{batch.ImportBatchId}/family-issues?limit=50&cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), Ct)).RootElement;
            rest.GetProperty("items").GetArrayLength().Should().Be(6);
            (await client.GetAsync(new Uri($"/api/v1/workspaces/{ws}/imports/{Guid.NewGuid()}/family-issues", UriKind.Relative), Ct))
                .StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        }

        // A later load brings the missing parent and the missing range member: those lines disappear.
        var later = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(FamilyHeader, ["MISSING-9", "", "", "", ""], ["G0003", "G0001", "G0005", "", ""])));
        (await h.RunAsync(later)).Status.Should().Be(JobStatus.Completed);
        (await h.Batches.GetFamilyIssuesAsync(ws, batch.ImportBatchId, null, 100, Ct)).Select(i => i.ControlNumber)
            .Should().BeEquivalentTo(["K0001", "K0004", "K0005", "V0001", "C1", "C2"]);
        families = await FamiliesAsync(h, ws);
        families["O-1"].Should().Match<FamilyRow>(f => f.Family == "MISSING-9" && f.Status == FamilyStatus.Resolved && f.Version > 1);
        Members(families, "G0001").Should().Equal("G0001", "G0002", "G0003", "G0004", "G0005");
        families["G0004"].Status.Should().Be(FamilyStatus.Resolved);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("range")]
    [InlineData("group")]
    public async Task Families_match_the_generator_ground_truth_on_the_1_to_3_family_corpus(string sources)
    {
        const int documents = 3_000;
        var root = Path.Combine(Path.GetTempPath(), "opp-family-" + Guid.NewGuid().ToString("N"));
        try
        {
            var truth = new GroundTruthSink();
            CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), documents), 20261004, root, new CorpusRunOptions
            {
                Threads = 2,
                WriteGroundTruth = false,
                ExtraSinks = [truth],
                SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions { IncludeNatives = false, IncludeText = false, IncludeImages = false })],
            });
            var dat = await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.dat"), Ct);
            truth.Documents.Should().HaveCount(documents);
            ((double)documents / truth.Documents.Values.Count(d => d.ParentControlNumber is null)).Should().BeInRange(2.0, 4.0, "the 1:3 family corpus");

            await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 97);
            var ws = await h.WorkspaceAsync();
            string[] ignored = sources switch
            {
                "range" => ["ParentID", "GroupIdentifier"],
                "group" => ["ParentID", "BegAttach", "EndAttach"],
                _ => [],
            };
            var profile = new ImportProfileDefinition { Columns = [.. ignored.Select(c => new ColumnMapping { Column = c, Ignore = true })] };
            var batch = await h.StartAsync(ws, dat, profile, name: "VOL001.dat");
            (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);

            var families = await FamiliesAsync(h, ws);
            families.Should().HaveCount(documents);
            var mismatches = truth.Documents.Values
                .Where(t => families[t.ControlNumber] is var f
                    && (f.Family != t.FamilyId || f.Sequence != t.FamilySequence || f.Status != FamilyStatus.Resolved
                        || f.Parent != (t.ParentControlNumber is null ? null : sources == "all" ? t.ParentControlNumber : t.FamilyId)))
                .Select(t => $"{t.ControlNumber}: expected {t.FamilyId}/{t.ParentControlNumber}/{t.FamilySequence}, got {families[t.ControlNumber]}")
                .Take(10)
                .ToList();
            mismatches.Should().BeEmpty();
            (await h.Batches.GetFamilyIssuesAsync(ws, batch.ImportBatchId, null, 10, Ct)).Should().BeEmpty();
            (await new DocumentRelationshipRepository(h.Db.AppDataSource).ResolveFamiliesAsync(ws, Ct)).ChangedDocuments.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>F0001 is the parent; F0012 holds F0013-F0015 as nested attachments (ParentID only).</summary>
    private static string[] FamilyRow25(string mode, int i)
    {
        var cn = $"F{i:D4}";
        return mode switch
        {
            "range" => [cn, "F0001", "F0025", "", ""],
            "pointer" => [cn, "", "", i == 1 ? "" : i is >= 13 and <= 15 ? "F0012" : "F0001", ""],
            _ => [cn, "", "", "", "FAMILY-F"],
        };
    }

    private sealed record FamilyRow(Guid DocumentId, string Family, string? Parent, int Sequence, FamilyStatus Status, long Version, bool IsDuplicatePrimary);

    private static List<string> Members(Dictionary<string, FamilyRow> families, string familyRoot) =>
        [.. families.Where(f => f.Value.Family == familyRoot).OrderBy(f => f.Value.Sequence).Select(f => f.Key)];

    private static async Task<Dictionary<string, FamilyRow>> FamiliesAsync(ImportHarness h, Guid ws)
    {
        var result = new Dictionary<string, FamilyRow>(StringComparer.Ordinal);
        await using var command = h.Db.DataSource.CreateCommand(
            """
            SELECT d.control_number, d.document_id, f.control_number, p.control_number, d.family_sequence, d.family_status,
                   s.document_version, d.is_duplicate_primary
            FROM opportunity.document d
            JOIN opportunity.document f ON f.workspace_id = d.workspace_id AND f.document_id = d.family_id
            LEFT JOIN opportunity.document p ON p.workspace_id = d.workspace_id AND p.document_id = d.parent_document_id
            JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
            WHERE d.workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetString(0)] = new FamilyRow(
                reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                (FamilyStatus)reader.GetInt16(5), reader.GetInt64(6), reader.GetBoolean(7));
        }

        return result;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }

    /// <summary>The generator's family ground truth, collected in load order.</summary>
    private sealed class GroundTruthSink : ICorpusSink
    {
        public Dictionary<string, (string ControlNumber, string FamilyId, string? ParentControlNumber, int FamilySequence)> Documents { get; } =
            new(StringComparer.Ordinal);

        public void Write(GeneratedChunk chunk)
        {
            foreach (var document in chunk.Families.SelectMany(f => f.Documents))
            {
                Documents[document.ControlNumber.ToUpperInvariant()] =
                    (document.ControlNumber, document.FamilyId, document.ParentControlNumber, document.FamilySequence);
            }
        }

        public IReadOnlyList<CorpusOutputFile> Complete() => [];

        public void Dispose()
        {
        }
    }
}
