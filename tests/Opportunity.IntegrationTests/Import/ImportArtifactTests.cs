using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Import;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Import;
using Opportunity.Core.Jobs;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.Jobs;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T04 against PostgreSQL and a file-system object store: natives and extracted text of a generated volume are
/// stored content-addressed and registered with their documents; missing files flag rows (or fail them, configured)
/// without failing the chunk; hash mismatches are report warnings; hostile paths are rejected; text above the cap is
/// flagged while the full text is stored; and a retried chunk uploads nothing new.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ImportArtifactTests(MigrationPostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_generated_volume_links_natives_and_text_with_missing_file_flags_and_hash_warnings()
    {
        using var volume = GeneratedVolume.Create(24, new Dictionary<DefectType, double>
        {
            [DefectType.MissingNative] = 0.2,
            [DefectType.MissingText] = 0.2,
            [DefectType.HashMismatch] = 0.25,
        });
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 10, volumeRoot: volume.Share);
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, volume.Dat, GeneratedVolume.Profile(), name: "VOL001.dat");

        var job = await h.RunAsync(batch);

        job.Status.Should().Be(JobStatus.Completed);
        (await h.IssuesAsync(batch)).Should().BeEmpty("missing files only flag their rows by default");
        var warnings = await h.IssuesAsync(batch, ImportIssueSeverity.Warning);
        string[] Warned(string code) => [.. warnings.Where(w => w.Code == code).Select(w => w.ControlNumber!).Order(StringComparer.Ordinal)];
        var missingNatives = volume.Defects("missingNative");
        var missingTexts = volume.Defects("missingText");
        var mismatches = volume.Defects("hashMismatch");
        missingNatives.Should().NotBeEmpty();
        missingTexts.Should().NotBeEmpty();
        mismatches.Should().NotBeEmpty();
        Warned("native-missing").Should().Equal(missingNatives);
        Warned("text-missing").Should().Equal(missingTexts);
        Warned("hash-mismatch-md5").Should().Equal(mismatches);
        Warned("hash-mismatch-sha256").Should().Equal(mismatches);
        warnings.First(w => w.Code == "native-missing").Column.Should().Be("NativeLink");

        var documents = await ArtifactsAsync(h, ws);
        documents.Should().HaveCount(24);
        foreach (var d in documents.Values)
        {
            var native = volume.File("NATIVES", d.ControlNumber);
            if (missingNatives.Contains(d.ControlNumber))
            {
                (d.NativeMissing, d.NativeKey).Should().Be((true, null));
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(native!, Ct);
                var sha = SHA256.HashData(bytes);
                d.NativeMissing.Should().BeFalse();
                d.NativeKey.Should().Be(ObjectKeys.Native(ws, d.DocumentId, Sha256Digest.FromBytes(sha)).Value);
                d.Sha256.Should().Equal(sha, "the computed hash wins over the load file's (ADR-009 R17)");
                (await ReadAsync(h, d.NativeKey!)).Should().Equal(bytes);
            }

            if (missingTexts.Contains(d.ControlNumber))
            {
                (d.TextMissing, d.TextKey, d.TextLength).Should().Be((true, null, null));
            }
            else
            {
                var text = await File.ReadAllTextAsync(volume.File("TEXT", d.ControlNumber)!, Ct);
                d.TextMissing.Should().BeFalse();
                d.TextLength.Should().Be(text.Length);
                d.TextTruncated.Should().BeFalse();
                Encoding.UTF8.GetString(await ReadAsync(h, d.TextKey!)).Should().Be(text);
            }
        }

        // ADR-011 §2.3: one registry row per stored object, owned by its document and the import job.
        (await h.CountAsync($"SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND created_by_job_id = '{batch.JobId}'", ws))
            .Should().Be(48 - missingNatives.Length - missingTexts.Length);
    }

    [Fact]
    public async Task Retrying_a_chunk_after_its_transaction_failed_uploads_nothing_new()
    {
        using var volume = GeneratedVolume.Create(6, new Dictionary<DefectType, double>());
        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 3, volumeRoot: volume.Share);
        var counting = new CountingObjectStore(h.Store);
        h.Store = counting;
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, volume.Dat, GeneratedVolume.Profile(), name: "VOL001.dat");
        await h.PrepareAsync(batch);
        counting.Reset(); // the uploaded DAT

        // Every chunk's transaction fails after its files were stored (a transient serialization failure).
        await h.Db.ExecuteAsync(
            """
            CREATE TABLE public.artifact_crash_flag (armed boolean);
            CREATE FUNCTION public.artifact_crash() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
            BEGIN
                IF EXISTS (SELECT FROM public.artifact_crash_flag) THEN
                    RAISE EXCEPTION 'simulated failure inside the chunk transaction' USING ERRCODE = '40001';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER artifact_crash BEFORE INSERT ON opportunity.import_batch_member FOR EACH ROW EXECUTE FUNCTION public.artifact_crash();
            INSERT INTO public.artifact_crash_flag VALUES (true);
            """);
        await h.DeliverOpenChunksAsync(batch);
        (await h.Jobs.GetChunksAsync(ws, batch.JobId, cancellationToken: Ct)).Should().OnlyContain(c => c.Status == JobChunkStatus.RetryWait);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(0);
        (await h.CountAsync("SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND area IN (1, 2)", ws)).Should().Be(0);
        counting.Created.Should().Be(12, "six natives and six texts were uploaded by the failed attempts");

        await h.Db.ExecuteAsync("DELETE FROM public.artifact_crash_flag");
        await h.Db.ExecuteAsync("UPDATE opportunity.job_chunk SET available_at = now() - interval '1 minute' WHERE workspace_id = @ws", ("ws", ws));
        counting.Reset();
        await h.DeliverOpenChunksAsync(batch);

        (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!.Status.Should().Be(JobStatus.Completed);
        counting.Puts.Should().Be(0, "the retried chunks address the same content keys under the same document ids");
        var documents = await ArtifactsAsync(h, ws);
        documents.Should().HaveCount(6).And.OnlyContain(d => d.Value.NativeKey != null && d.Value.TextKey != null);
        (await h.CountAsync("SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND area IN (1, 2)", ws)).Should().Be(12);
        var listed = 0;
        await foreach (var _ in h.Store.ListPrefixAsync(ObjectPrefix.WorkspaceArea(ws, "docs"), Ct))
        {
            listed++;
        }

        listed.Should().Be(12, "no attempt left a second copy behind");
    }

    [Fact]
    public async Task Hostile_paths_are_rejected_and_missing_files_can_fail_their_rows_without_failing_the_chunk()
    {
        using var share = new HandMadeShare();
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root);
        var ws = await h.WorkspaceAsync();
        var profile = HandMadeShare.Profile() with
        {
            Paths = new PathSettings { VolumeRoot = "VOL001", MissingFiles = MissingFilePolicy.Error, TextInLoadFile = true },
        };
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "NATIVELINK", "DOCTEXT"],
            ["H-001", @"NATIVES\a.pdf", "plain text"],
            ["H-002", @"..\..\outside\secret.txt", "x"],
            ["H-003", @"link-out.pdf", "x"],
            ["H-004", @"C:\Windows\win.ini", "x"],
            ["H-005", @"\\server\share\a.pdf", "x"],
            ["H-006", @"NATIVES\missing.pdf", "x"],
            ["H-007", @".\natives\A.PDF", "y"]));

        var batch = await h.StartAsync(ws, dat, profile);
        var job = await h.RunAsync(batch);

        job.Status.Should().Be(JobStatus.CompletedWithErrors);
        job.Counters.ChunksFailed.Should().Be(0);
        (await h.IssuesAsync(batch)).Select(i => (i.ControlNumber, i.Code)).Should().Equal(
            ("H-002", "native-path-rejected"),
            ("H-003", "native-path-rejected"),
            ("H-004", "native-path-rejected"),
            ("H-005", "native-path-rejected"),
            ("H-006", "native-missing"));
        var documents = await ArtifactsAsync(h, ws);
        documents.Keys.Should().BeEquivalentTo(["H-001", "H-007"]);
        documents["H-007"].NativeKey.Should().NotBe(documents["H-001"].NativeKey, "keys live under each document");
        Encoding.UTF8.GetString(await ReadAsync(h, documents["H-007"].TextKey!)).Should().Be("y");
        var raw = await h.Db.ScalarAsync<string>("SELECT metadata_raw::text FROM opportunity.document WHERE workspace_id = @ws AND control_number = 'H-001'", ("ws", ws));
        JsonNode.Parse(raw)!["s:TextPath"].Should().BeNull("text given in the load file is stored as an object, not as a raw value");
    }

    [Fact]
    public async Task Text_above_the_cap_is_flagged_truncated_and_stored_in_full()
    {
        using var share = new HandMadeShare();
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root, textCap: 1_000);
        var ws = await h.WorkspaceAsync();
        var longText = string.Join(' ', Enumerable.Range(0, 600).Select(i => "word" + i));
        await File.WriteAllTextAsync(Path.Combine(share.Volume, "TEXT", "long.txt"), longText, Encoding.Unicode, Ct); // UTF-16LE with BOM
        var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(
            ["BEGDOC", "NATIVELINK", "DOCTEXT"],
            ["T-001", "", @"TEXT\long.txt"],
            ["T-002", "", @"TEXT\short.txt"],
            ["T-003", "", ""]));

        var batch = await h.StartAsync(ws, dat, HandMadeShare.Profile());
        (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);

        var documents = await ArtifactsAsync(h, ws);
        var full = documents["T-001"];
        (full.TextLength, full.TextTruncated, full.TextMissing).Should().Be((longText.Length, true, false));
        Encoding.UTF8.GetString(await ReadAsync(h, full.TextKey!)).Should().Be(longText, "the full text stays in storage (Q-29)");
        (documents["T-002"].TextLength, documents["T-002"].TextTruncated).Should().Be((10, false));
        (documents["T-003"].TextMissing, documents["T-003"].TextKey).Should().Be((true, null));
        documents.Values.Should().OnlyContain(d => d.NativeMissing && d.NativeKey == null, "no native link means no native");
        (await h.IssuesAsync(batch, ImportIssueSeverity.Warning)).Should().BeEmpty("a blank link is no text, not a missing file");
    }

    [Fact]
    public async Task An_overlay_stores_a_new_native_under_the_existing_document_and_keeps_the_old_object()
    {
        using var share = new HandMadeShare();
        await using var h = await ImportHarness.CreateAsync(postgres, volumeRoot: share.Root);
        var ws = await h.WorkspaceAsync();
        var first = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "NATIVELINK", "DOCTEXT"], ["O-001", @"NATIVES\a.pdf", ""])),
            HandMadeShare.Profile());
        (await h.RunAsync(first)).Status.Should().Be(JobStatus.Completed);
        var before = (await ArtifactsAsync(h, ws))["O-001"];

        var second = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "NATIVELINK", "DOCTEXT"], ["O-001", @"NATIVES\b.pdf", @"TEXT\short.txt"])),
            HandMadeShare.Profile(), mode: ImportMode.AppendOverlay);
        (await h.RunAsync(second)).Status.Should().Be(JobStatus.Completed);

        var after = (await ArtifactsAsync(h, ws))["O-001"];
        after.DocumentId.Should().Be(before.DocumentId);
        after.NativeKey.Should().NotBe(before.NativeKey).And.StartWith($"ws/{ws:N}/docs/{before.DocumentId:N}/native/");
        after.TextKey.Should().NotBeNull();
        after.TextMissing.Should().BeFalse();
        (await h.CountAsync($"SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND document_id = '{before.DocumentId}' AND area = 1", ws))
            .Should().Be(2, "originals are never replaced in place (ADR-011 §4.2)");
        (await h.BatchAsync(second)).RowsOverlaid.Should().Be(1);
    }

    [Fact]
    public async Task A_load_that_links_files_fails_preparation_when_no_import_share_is_configured()
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC", "NATIVELINK"], ["N-001", @"NATIVES\a.pdf"])));

        (await h.PrepareAsync(batch)).Should().Be(ImportPreparationOutcome.Failed);

        var job = (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!;
        job.Status.Should().Be(JobStatus.Failed);
        job.StatusReason.Should().StartWith(ImportJobPreparer.VolumeUnavailable);
    }

    private static async Task<byte[]> ReadAsync(ImportHarness h, string key)
    {
        await using var stream = await h.Store.OpenReadAsync(ObjectKey.Parse(key), null, Ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    internal sealed record Artifacts(
        string ControlNumber, Guid DocumentId, string? NativeKey, string? TextKey, bool NativeMissing, bool TextMissing, long? TextLength, bool TextTruncated,
        byte[]? Sha256);

    internal static async Task<Dictionary<string, Artifacts>> ArtifactsAsync(ImportHarness h, Guid ws)
    {
        var result = new Dictionary<string, Artifacts>(StringComparer.Ordinal);
        await using var command = h.Db.DataSource.CreateCommand(
            """
            SELECT d.control_number, d.document_id, n.logical_key, t.logical_key, d.native_missing, d.text_missing, d.text_length, d.text_truncated, d.sha256
            FROM opportunity.document d
            LEFT JOIN opportunity.stored_object n ON n.workspace_id = d.workspace_id AND n.object_id = d.native_object_id
            LEFT JOIN opportunity.stored_object t ON t.workspace_id = d.workspace_id AND t.object_id = d.text_object_id
            WHERE d.workspace_id = @ws
            """);
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetString(0)] = new Artifacts(
                reader.GetString(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8));
        }

        return result;
    }

    /// <summary>A small share with a volume, a file outside it and a symbolic link that points there.</summary>
    private sealed class HandMadeShare : IDisposable
    {
        public HandMadeShare()
        {
            Root = Path.Combine(Path.GetTempPath(), "opp-share-" + Guid.NewGuid().ToString("N"));
            Volume = Path.Combine(Root, "VOL001");
            Directory.CreateDirectory(Path.Combine(Volume, "NATIVES"));
            Directory.CreateDirectory(Path.Combine(Volume, "TEXT"));
            Directory.CreateDirectory(Path.Combine(Root, "outside"));
            File.WriteAllBytes(Path.Combine(Volume, "NATIVES", "a.pdf"), Encoding.ASCII.GetBytes("%PDF-1.7 first native"));
            File.WriteAllBytes(Path.Combine(Volume, "NATIVES", "b.pdf"), Encoding.ASCII.GetBytes("%PDF-1.7 replacement native"));
            File.WriteAllText(Path.Combine(Volume, "TEXT", "short.txt"), "short text");
            File.WriteAllText(Path.Combine(Root, "outside", "secret.txt"), "secret");
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(Volume, "link-out.pdf"), Path.Combine(Root, "outside", "secret.txt"));
            }
        }

        public string Root { get; }

        public string Volume { get; }

        public static ImportProfileDefinition Profile() => new()
        {
            Paths = new PathSettings { VolumeRoot = "VOL001" },
            Columns =
            [
                new ColumnMapping
                {
                    Column = "DOCTEXT",
                    Targets = [new MappingTarget { Kind = MappingTargetKind.Structural, Structural = StructuralTarget.TextPath }],
                },
            ],
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

/// <summary>A data-generator volume (E17-T02) with natives and text, written to a temp directory removed on dispose.</summary>
internal sealed class GeneratedVolume : IDisposable
{
    private readonly Dictionary<string, string> _files;

    private GeneratedVolume(string share)
    {
        Share = share;
        var volume = Path.Combine(share, "VOL001");
        Dat = System.IO.File.ReadAllBytes(Path.Combine(volume, "DATA", "VOL001.dat"));
        _files = Directory.EnumerateFiles(volume, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "DATA" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(f => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f)))! + "/" + Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal);
    }

    /// <summary>The import share: the volume is its <c>VOL001</c> folder.</summary>
    public string Share { get; }

    public byte[] Dat { get; }

    public static GeneratedVolume Create(int documents, IReadOnlyDictionary<DefectType, double> defects, ulong seed = 20261004)
    {
        var root = Path.Combine(Path.GetTempPath(), "opp-artifacts-" + Guid.NewGuid().ToString("N"));
        CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), documents), seed, root, new CorpusRunOptions
        {
            Threads = 1,
            WriteGroundTruth = false,
            SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions { IncludeImages = false, DefectRates = defects })],
        });
        return new GeneratedVolume(root);
    }

    /// <summary>The auto-mapped profile, reading files from the share's <c>VOL001</c> folder.</summary>
    public static ImportProfileDefinition Profile() => new() { Paths = new PathSettings { VolumeRoot = "VOL001" } };

    /// <summary>The written file of a document in <c>NATIVES</c> or <c>TEXT</c>, or null when it was not written.</summary>
    public string? File(string folder, string controlNumber) => _files.GetValueOrDefault(folder + "/" + controlNumber);

    /// <summary>Control numbers of the injected defects of one type, ordered.</summary>
    public string[] Defects(string type) =>
        [.. System.IO.File.ReadLines(Path.Combine(Share, VolumeWriter.DefectsFile))
            .Select(l => JsonNode.Parse(l)!)
            .Where(n => n["type"]!.GetValue<string>() == type)
            .Select(n => n["controlNumber"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        if (Directory.Exists(Share))
        {
            Directory.Delete(Share, recursive: true);
        }
    }
}

/// <summary>Counts puts (and created objects) passing to the inner store.</summary>
internal sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
{
    private int _puts;
    private int _created;

    public int Puts => _puts;

    public int Created => _created;

    public void Reset()
    {
        _puts = 0;
        _created = 0;
    }

    public async Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _puts);
        var result = await inner.PutAsync(key, content, options, cancellationToken);
        if (result.Outcome == PutOutcome.Created)
        {
            Interlocked.Increment(ref _created);
        }

        return result;
    }

    public Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    public Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);

    public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
        inner.ListPrefixAsync(prefix, cancellationToken);

    public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
        inner.DeletePrefixAsync(prefix, cancellationToken);
}
