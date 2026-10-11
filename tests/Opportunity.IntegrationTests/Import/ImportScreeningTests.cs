using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Import;
using Opportunity.Application.Rendering;
using Opportunity.Contracts.Import;
using Opportunity.Core.Jobs;
using Opportunity.Data.Import;
using Opportunity.Data.Rendering;
using Opportunity.Import.Jobs;
using Opportunity.Import.Scanning;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T09 against PostgreSQL and a file-system object store, with the deterministic scanner double: an EICAR native
/// is registered Quarantined with its signature, never offered to the renderer, counted in the import report and
/// audited (<c>Import.MalwareDetected</c> with its object id); a decompression bomb and an encrypted container are
/// quarantined as Unscannable; a native over the size limit fails its row; a disguised executable is flagged; a
/// load-file hash mismatch flags the document in the report and is audited; DAT record and field limits come from
/// configuration and are frozen with the preparation. The quarantined download itself (Document.ViewQuarantined,
/// audited) is covered by <c>ProtectedContentGatewayTests.Quarantined_natives_need_view_quarantined</c>.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportScreeningTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Malware_bombs_and_oversized_natives_are_quarantined_or_refused_reported_audited_and_never_rendered()
    {
        using var share = new Share();
        share.Native("clean.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 a clean native"));
        share.Native("eicar.pdf", [.. Encoding.ASCII.GetBytes("%PDF-1.7\n"), .. SignatureMalwareScanner.Eicar]);
        share.Native("bomb.docx", ZipOfZeros(17 * 1024 * 1024));
        share.Native("invoice.pdf", [0x4D, 0x5A, 0x90, 0x00, .. Encoding.ASCII.GetBytes("program code")]);
        share.Native("big.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 " + new string('x', 70_000)));
        share.Native("hashed.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 another clean native"));
        share.Native("locked.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 " + SignatureMalwareScanner.UnscannableMarker));
        var scanner = new SignatureMalwareScanner();
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 4, volumeRoot: share.Root);
        h.Screening = new IngestScreening(new ImportLimits { MaxNativeBytes = 64 * 1024 }, scanner);
        var ws = await h.WorkspaceAsync();
#pragma warning disable CA5351 // the load-file MD5 the import verifies (ADR-009 R17), not a security control
        var cleanMd5 = Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes("%PDF-1.7 a clean native")));
#pragma warning restore CA5351
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "NATIVELINK", "MD5HASH"],
            ["S-001", @"NATIVES\clean.pdf", cleanMd5],
            ["S-002", @"NATIVES\eicar.pdf", ""],
            ["S-003", @"NATIVES\bomb.docx", ""],
            ["S-004", @"NATIVES\invoice.pdf", ""],
            ["S-005", @"NATIVES\big.pdf", ""],
            ["S-006", @"NATIVES\hashed.pdf", "00000000000000000000000000000000"],
            ["S-007", @"NATIVES\locked.pdf", ""]));

        var batch = await h.StartAsync(ws, dat, new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL001" } });
        var job = await h.RunAsync(batch);

        job.Status.Should().Be(JobStatus.CompletedWithErrors);
        job.Counters.ChunksFailed.Should().Be(0, "no limit or verdict fails a chunk");
        (await h.IssuesAsync(batch)).Select(i => (i.ControlNumber, i.Code)).Should().Equal([("S-005", "native-too-large")]);
        (await h.IssuesAsync(batch, ImportIssueSeverity.Warning)).Select(i => (i.ControlNumber, i.Code)).Order().Should().Equal(
            ("S-002", "native-quarantined"),
            ("S-003", "native-quarantined"),
            ("S-004", "native-type-mismatch"),
            ("S-006", "hash-mismatch-md5"),
            ("S-007", "native-quarantined"));
        scanner.Scans.Should().Be(5, "the bomb is refused by the archive limits before the scanner, the oversized native is never stored");

        // Registry: state and screening result per native (ADR-015 D16.2).
        var natives = await NativesAsync(h, ws);
        natives["S-001"].Key.Should().Be(((short)1, (short?)1, (string?)null));
        natives["S-002"].Key.Should().Be(((short)2, (short?)2, (string?)"Eicar-Test-Signature"));
        natives["S-003"].State.Should().Be((short)2);
        natives["S-003"].Verdict.Should().Be((short)3);
        natives["S-003"].Detail.Should().StartWith("archive-ratio:");
        natives["S-004"].Key.Should().Be(((short)1, (short?)1, (string?)null));
        natives["S-006"].Key.Should().Be(((short)1, (short?)1, (string?)null));
        natives["S-007"].Key.Should().Be(((short)2, (short?)3, (string?)"encrypted: test container"));
        natives.Should().NotContainKey("S-005");

        // Never rendered: the render planner only sees committed natives.
        var candidates = await new RenderRepository(h.Db.AppDataSource).GetCandidatesAsync(
            ws, new RenderScope(batch.JobId, batch.ImportBatchId), new RenderRendererKey("test", "1", [1]), Ct);
        var ids = await DocumentIdsAsync(h, ws);
        candidates.Select(c => c.DocumentId).Should().BeEquivalentTo([ids["S-001"], ids["S-006"]]);

        // The report counts them.
        var report = (await new ImportReportRepository(h.Db.AppDataSource).GetAsync(ws, batch.ImportBatchId, Ct))!;
        (report.NativesLinked, report.NativesQuarantined, report.NativesHashMismatch).Should().Be((6L, 3L, 1L));
        report.Final.Should().BeTrue();

        // Audit: one MalwareDetected per chunk with the quarantined object ids, one HashMismatch.
        var malware = await h.Db.ColumnAsync(
            $"SELECT details->>'ObjectIds' FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'MalwareDetected' ORDER BY chunk_sequence");
        string.Join(',', malware).Split(',').Should().BeEquivalentTo(
            [.. natives.Where(n => n.Value.State == 2).Select(n => n.Value.ObjectId.ToString())]);
        (await h.Db.ColumnAsync(
            $"SELECT details->>'Documents' || ':' || (details->>'Algorithms') FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'HashMismatch'"))
            .Should().Equal("1:md5");
        (await h.Db.ColumnAsync(
            $"SELECT details->>'NativesQuarantined' FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Import' AND action = 'Completed'"))
            .Should().Equal("3");
    }

    [Fact]
    public async Task Dat_record_and_field_limits_come_from_configuration_and_are_frozen_with_the_preparation()
    {
        await using var h = await ImportHarness.CreateAsync(postgres, limits: new ImportLimits { MaxRecordBytes = 200, MaxFieldCount = 3 });
        var ws = await h.WorkspaceAsync();
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "CUSTODIAN", "TITLE"],
            ["L-001", "Smith", "short"],
            ["L-002", "Smith", new string('x', 300)],
            ["L-003", "Smith", "a", "b"],
            ["L-004", "Jones", "fine"]));

        var batch = await h.StartAsync(ws, dat);
        var job = await h.RunAsync(batch);

        job.Status.Should().Be(JobStatus.CompletedWithErrors);
        var prepared = (await h.BatchAsync(batch)).Preparation!;
        (prepared.MaxRecordBytes, prepared.MaxFieldCount).Should().Be((200, 3));
        var errors = await h.IssuesAsync(batch);
        errors.Select(e => e.RowNo).Should().Equal(2, 3);
        errors[0].Message.Should().Contain("200");
        errors[1].Message.Should().Contain("3");
        (await h.DocumentsAsync(ws)).Keys.Should().BeEquivalentTo(["L-001", "L-004"]);
    }

    private static byte[] ZipOfZeros(int bytes)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open();
            entry.Write(new byte[bytes]);
        }

        return buffer.ToArray();
    }

    private sealed record Native(short State, short? Verdict, string? Detail)
    {
        public Guid ObjectId { get; init; }

        public (short State, short? Verdict, string? Detail) Key => (State, Verdict, Detail);
    }

    private static async Task<Dictionary<string, Native>> NativesAsync(ImportHarness h, Guid ws)
    {
        var result = new Dictionary<string, Native>(StringComparer.Ordinal);
        await using var command = h.Db.DataSource.CreateCommand(
            """
            SELECT d.control_number, o.state, o.scan_verdict, o.scan_detail, o.object_id
            FROM opportunity.document d
            JOIN opportunity.stored_object o ON o.workspace_id = d.workspace_id AND o.object_id = d.native_object_id
            WHERE d.workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetString(0)] = new Native(reader.GetInt16(1), reader.IsDBNull(2) ? null : reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetString(3))
            {
                ObjectId = reader.GetGuid(4),
            };
        }

        return result;
    }

    private static async Task<Dictionary<string, Guid>> DocumentIdsAsync(ImportHarness h, Guid ws) =>
        (await h.DocumentsAsync(ws)).ToDictionary(d => d.Key, d => d.Value.DocumentId, StringComparer.Ordinal);

    /// <summary>An import share with one volume, removed on dispose.</summary>
    private sealed class Share : IDisposable
    {
        public Share()
        {
            Root = Path.Combine(Path.GetTempPath(), "opp-screen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "VOL001", "NATIVES"));
        }

        public string Root { get; }

        public void Native(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(Root, "VOL001", "NATIVES", name), bytes);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
