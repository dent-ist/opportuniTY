using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.Jobs;
using Opportunity.Import.Volumes;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Import;

/// <summary>
/// E08-T05 against PostgreSQL (app login, RLS on) and a file-system object store: an OPT loaded with its DAT from a
/// generator-made volume (E17-T02) with injected OPT defects, a multi-page TIFF volume, Beg Bates matching and an
/// OPT-only page re-load of existing documents. Volumes live in a temp directory removed afterwards.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class OptImageImportTests(MigrationPostgresFixture postgres)
{
    private const ulong Seed = 20261004;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_generated_volume_links_its_pages_and_reports_every_injected_OPT_defect()
    {
        using var volume = GeneratedVolume.Create(48, new VolumeOptions
        {
            IncludeNatives = false,
            IncludeText = false,
            DefectRates = new Dictionary<DefectType, double> { [DefectType.MissingImage] = 0.15, [DefectType.OptPageCountMismatch] = 0.15 },
        });
        var defects = volume.Defects();
        var missing = defects.Where(d => d.Type == "missingImage").ToList();
        var mismatched = defects.Where(d => d.Type == "optPageCountMismatch").ToList();
        missing.Should().NotBeEmpty();
        mismatched.Should().NotBeEmpty();

        // Three hand-made defects on top: a row before the first document break, an orphan document and a traversal path
        // in a document without other defects.
        var lines = volume.OptLines();
        var defective = defects.Select(d => d.ControlNumber).ToHashSet(StringComparer.Ordinal);
        var traversalIndex = lines.FindIndex(l => l.Split(',') is [var key, _, _, "Y", ..] && !defective.Contains(key));
        var traversalKey = lines[traversalIndex].Split(',')[0];
        var parts = lines[traversalIndex].Split(',');
        parts[2] = @"..\..\..\outside.tif";
        lines[traversalIndex] = string.Join(',', parts);
        lines.Insert(0, @"STRAY0001,VOL001,IMAGES\IMG0001\STRAY0001.tif,,,,");
        lines.Add(@"ORPHAN0001,VOL001,IMAGES\IMG0001\ORPHAN0001.tif,Y,,,1");
        var opt = Encoding.ASCII.GetBytes(string.Concat(lines.Select(l => l + "\r\n")));
        var rowsPerDocument = PagesPerDocument(lines);
        var declared = lines.Select(l => l.Split(',')).Where(p => p[3] == "Y")
            .ToDictionary(p => p[0], p => int.Parse(p[6], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var missingDocuments = missing.Select(d => d.ControlNumber).ToHashSet(StringComparer.Ordinal);

        // A one-row document whose (multi-page) file is missing keeps the pages its break row declares, all missing.
        int ExpectedPages(string cn) =>
            missingDocuments.Contains(cn) && rowsPerDocument[cn] == 1 && declared[cn] > 1 ? declared[cn] : rowsPerDocument[cn];
        int ExpectedMissing(string cn) => cn == traversalKey ? 1 : missingDocuments.Contains(cn) ? (rowsPerDocument[cn] == 1 ? ExpectedPages(cn) : 1) : 0;

        await using var h = await ImportHarness.CreateAsync(postgres, rowsPerChunk: 10);
        h.Volumes = new ImportVolumeOptions { VolumeShareRoot = volume.Root };
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, volume.Dat(), name: "VOL001.dat", opt: opt,
            profile: new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL001" } });
        var job = await h.RunAsync(batch);

        job.Status.Should().Be(JobStatus.Completed);
        (await h.IssuesAsync(batch)).Should().BeEmpty();
        var report = await h.BatchAsync(batch);
        report.RowsImported.Should().Be(48);
        report.Preparation!.OptRowsTotal.Should().Be(lines.Count);

        // One Imported page set per imaged document, one page per OPT row, active, Incomplete exactly when a page is missing.
        var sets = await PageSetsAsync(h, ws);
        sets.Keys.Should().BeEquivalentTo(rowsPerDocument.Keys.Where(k => k != "ORPHAN0001"));
        var incomplete = missingDocuments.Append(traversalKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (controlNumber, set) in sets)
        {
            set.PageCount.Should().Be(ExpectedPages(controlNumber), controlNumber);
            set.Pages.Should().Be(ExpectedPages(controlNumber), controlNumber);
            set.Active.Should().BeTrue(controlNumber);
            set.ImagesIncomplete.Should().Be(incomplete.Contains(controlNumber), controlNumber);
            set.Status.Should().Be(incomplete.Contains(controlNumber) ? (short)2 : (short)1, controlNumber);
            set.MissingPages.Should().Be(ExpectedMissing(controlNumber), controlNumber);
        }

        // Every present page has its original raster, stored and registered: a G4 TIFF at 300 DPI, US Letter
        var pages = sets.Values.Sum(s => s.Pages);
        var present = pages - sets.Keys.Sum(ExpectedMissing);
        // (image natives are imaged as 100 DPI JPG pages by the generator; both are US Letter).
        (await h.CountAsync("SELECT count(*) FROM opportunity.page_image WHERE workspace_id = @ws AND purpose = 1", ws)).Should().Be(present);
        (await h.CountAsync(
                "SELECT count(*) FROM opportunity.page_image WHERE workspace_id = @ws AND ((format = 1 AND dpi_x = 300 AND width_px = 2550) OR (format = 2 AND dpi_x = 100 AND width_px = 850))",
                ws))
            .Should().Be(present);
        (await h.CountAsync("SELECT count(*) FROM opportunity.page WHERE workspace_id = @ws AND width_pt = 612 AND height_pt = 792", ws)).Should().Be(pages);
        (await h.CountAsync("SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND area = 3 AND content_type IN ('image/tiff', 'image/jpeg')", ws))
            .Should().Be(present);

        // OPT problems are warnings on OPT rows: per missing page, per mismatch (both counts), orphans, traversal.
        var warnings = (await h.IssuesAsync(batch, ImportIssueSeverity.Warning)).Where(i => i.Source == ImportIssueSource.Opt).ToList();
        warnings.Where(i => i.Code == "opt-image-missing").Select(i => i.ControlNumber).Should()
            .BeEquivalentTo(missing.Select(d => PageKey(d.ControlNumber, d.Page)));
        var counted = warnings.Where(i => i.Code == "opt-page-count-mismatch").ToList();
        var reconciled = mismatched.Where(d => ExpectedPages(d.ControlNumber) != d.DeclaredPages).ToList();
        counted.Select(i => i.ControlNumber).Should().BeEquivalentTo(reconciled.Select(d => d.ControlNumber));
        foreach (var d in reconciled)
        {
            var issue = counted.Single(i => i.ControlNumber == d.ControlNumber);
            issue.Message.Should().Contain($"declares {d.DeclaredPages} page(s)").And.Contain($"has {d.ActualPages} page(s)");
            issue.RowNo.Should().Be(d.OptLine + 1, "the stray row was inserted above it");
        }

        warnings.Should().ContainSingle(i => i.Code == "opt-path-rejected").Which.ControlNumber.Should().Be(traversalKey);
        warnings.Should().ContainSingle(i => i.Code == "opt-missing-document-break").Which.RowNo.Should().Be(1);
        warnings.Should().ContainSingle(i => i.Code == "opt-orphan-document").Which.RowNo.Should().Be(lines.Count);
        warnings.Should().OnlyContain(i => i.Code.StartsWith("opt-", StringComparison.Ordinal));

        // DAT documents without images are warned on their DAT row.
        var datWarnings = (await h.IssuesAsync(batch, ImportIssueSeverity.Warning)).Where(i => i.Source == ImportIssueSource.Dat).ToList();
        datWarnings.Where(i => i.Code == ImportImageLinker.Codes.NoImages).Should().HaveCount(48 - sets.Count);

        // Searchable flag: the documents' ImagesIncomplete column (projected into the index).
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws AND images_incomplete", ws)).Should().Be(incomplete.Count);

        // Re-storing a document's images (a retried chunk: same document id, same bytes) uploads nothing new.
        var files = Directory.EnumerateFiles(h.StoreRoot, "*", SearchOption.AllDirectories).Count();
        var someone = sets.First(s => !s.Value.ImagesIncomplete);
        var staged = await h.Batches.GetImageRowsAsync(ws, batch.ImportBatchId, someone.Value.RowNo, someone.Value.RowNo, Ct);
        ImportVolume.TryOpen(h.Volumes, "VOL001", out var opened, out _).Should().BeTrue();
        var (again, _) = await ImportImageLinker.LinkAsync(h.Store, ws, someone.Value.DocumentId, staged, opened, null, Ct);
        again.Pages.Should().HaveCount(someone.Value.Pages);
        Directory.EnumerateFiles(h.StoreRoot, "*", SearchOption.AllDirectories).Count().Should().Be(files);
    }

    [Fact]
    public async Task A_multi_page_TIFF_volume_gives_one_page_per_frame_and_a_missing_file_all_its_declared_pages()
    {
        using var volume = GeneratedVolume.Create(24, new VolumeOptions
        {
            IncludeNatives = false,
            IncludeText = false,
            ImageFormat = Opportunity.DataGenerator.Corpus.Volumes.PageImageFormat.MultiPageTiff,
            DefectRates = new Dictionary<DefectType, double> { [DefectType.MissingImage] = 0.2 },
        });
        var missing = volume.Defects().Where(d => d.Type == "missingImage").Select(d => d.ControlNumber).ToHashSet(StringComparer.Ordinal);
        missing.Should().NotBeEmpty();
        var declared = volume.OptLines().Select(l => l.Split(',')).ToDictionary(p => p[0], p => int.Parse(p[6], System.Globalization.CultureInfo.InvariantCulture));

        await using var h = await ImportHarness.CreateAsync(postgres);
        h.Volumes = new ImportVolumeOptions { VolumeShareRoot = Path.Combine(volume.Root, "VOL001") };
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, volume.Dat(), opt: volume.Opt());
        (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);

        var sets = await PageSetsAsync(h, ws);
        sets.Keys.Should().BeEquivalentTo(declared.Keys);
        foreach (var (controlNumber, set) in sets)
        {
            set.Pages.Should().Be(declared[controlNumber], controlNumber);
            set.MissingPages.Should().Be(missing.Contains(controlNumber) ? declared[controlNumber] : 0, controlNumber);
            set.ImagesIncomplete.Should().Be(missing.Contains(controlNumber));
        }

        // Frames in order, all from one stored object per document.
        (await h.CountAsync("SELECT count(*) FROM opportunity.page WHERE workspace_id = @ws AND source_frame <> ordinal - 1", ws)).Should().Be(0);
        (await h.CountAsync("SELECT count(*) FROM opportunity.stored_object WHERE workspace_id = @ws AND area = 3", ws)).Should().Be(declared.Count - missing.Count);
        (await h.IssuesAsync(batch, ImportIssueSeverity.Warning)).Where(i => i.Code == "opt-page-count-mismatch").Should().BeEmpty();
    }

    [Fact]
    public async Task OPT_documents_match_by_Beg_Bates_and_an_OPT_only_load_replaces_pages_of_existing_documents()
    {
        var root = Path.Combine(Path.GetTempPath(), "opp-optvol-" + Guid.NewGuid().ToString("N"));
        try
        {
            var images = Directory.CreateDirectory(Path.Combine(root, "VOL9", "IMAGES"));
            foreach (var key in new[] { "PROD0001", "PROD0002", "PROD0003" })
            {
                await File.WriteAllBytesAsync(Path.Combine(images.FullName, key + ".tif"), PageImages.Tiff([key]), Ct);
            }

            foreach (var key in new[] { "DOC-1-A", "DOC-1-B" })
            {
                await File.WriteAllBytesAsync(Path.Combine(images.FullName, key + ".jpg"), PageImages.Jpeg(key), Ct);
            }

            await using var h = await ImportHarness.CreateAsync(postgres);
            h.Volumes = new ImportVolumeOptions { VolumeShareRoot = root };
            var ws = await h.WorkspaceAsync();
            var profile = new ImportProfileDefinition
            {
                Paths = new PathSettings { VolumeRoot = "VOL9", StripPrefix = @"\\fileserver\exports\VOL9\" },
                Images = new ImageSettings { MatchBy = ImageMatchField.BegBates },
                Columns =
                [
                    new ColumnMapping { Column = "DOCID", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = SystemFields.ControlNumber }] },
                    new ColumnMapping { Column = "PRODBEG", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = SystemFields.BegBates }] },
                ],
            };
            var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(["DOCID", "PRODBEG"], ["DOC-1", "PROD0001"], ["DOC-2", "PROD0003"]));
            var opt = Encoding.ASCII.GetBytes(
                "PROD0001,VOL9,.\\IMAGES\\PROD0001.tif,Y,,,2\r\n" +
                "PROD0002,VOL9,\\\\fileserver\\exports\\VOL9\\IMAGES\\PROD0002.tif,,,,\r\n" +
                "PROD0003,VOL9,IMAGES/PROD0003.tif,Y,,,1\r\n");
            var batch = await h.StartAsync(ws, dat, profile, opt: opt);
            (await h.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
            var first = await PageSetsAsync(h, ws);
            (first["DOC-1"].Pages, first["DOC-1"].MissingPages, first["DOC-2"].Pages).Should().Be((2, 0, 1));
            (await h.IssuesAsync(batch, null)).Should().BeEmpty();
            var keys = await h.Db.ScalarAsync<string>(
                "SELECT string_agg(p.image_key, ',' ORDER BY p.ordinal) FROM opportunity.page p JOIN opportunity.document d ON d.workspace_id = p.workspace_id AND d.active_page_set_id = p.page_set_id WHERE d.workspace_id = @ws AND d.control_number = 'DOC-1'",
                ("ws", ws));
            keys.Should().Be("PROD0001,PROD0002");

            // Re-imaging: an OPT alone, by control number, replaces DOC-1's pages; an unknown key is an orphan error.
            var reimage = Encoding.ASCII.GetBytes(
                "DOC-1,VOL9,IMAGES\\DOC-1-A.jpg,Y,,,2\r\n" +
                "DOC-1.0002,VOL9,IMAGES\\DOC-1-B.jpg,,,,\r\n" +
                "DOC-9,VOL9,IMAGES\\DOC-9.jpg,Y,,,1\r\n");
            var versionBefore = await VersionAsync(h, ws, "DOC-1");
            var reload = await h.StartImagesOnlyAsync(ws, reimage, new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL9" } });
            (await h.RunAsync(reload)).Status.Should().Be(JobStatus.CompletedWithErrors);
            var second = await PageSetsAsync(h, ws);
            second["DOC-1"].PageSetId.Should().NotBe(first["DOC-1"].PageSetId);
            (second["DOC-1"].Pages, second["DOC-1"].MissingPages).Should().Be((2, 0));
            second["DOC-2"].PageSetId.Should().Be(first["DOC-2"].PageSetId);
            (await VersionAsync(h, ws, "DOC-1")).Should().Be(versionBefore + 1);
            (await h.CountAsync("SELECT count(*) FROM opportunity.page_set WHERE workspace_id = @ws", ws)).Should().Be(3, "the replaced set is retained");
            (await h.CountAsync("SELECT count(*) FROM opportunity.page_image WHERE workspace_id = @ws AND format = 2", ws)).Should().Be(2);
            var reloaded = await h.BatchAsync(reload);
            (reloaded.Preparation!.RowsTotal, reloaded.RowsOverlaid, reloaded.RowsErrored).Should().Be((2L, 1L, 1L));
            var errors = await h.IssuesAsync(reload);
            errors.Should().ContainSingle().Which.Should().Match<ImportRowIssueRecord>(i =>
                i.Code == "opt-document-not-found" && i.Source == ImportIssueSource.Opt && i.RowNo == 3 && i.ControlNumber == "DOC-9");

            // The same OPT again changes nothing: the document is skipped, no new page set, no version bump.
            var again = await h.StartImagesOnlyAsync(ws, reimage, new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL9" } }, "again.opt");
            await h.RunAsync(again);
            var repeated = await h.BatchAsync(again);
            (repeated.RowsOverlaid, repeated.RowsSkipped).Should().Be((0L, 1L));
            (await h.CountAsync("SELECT count(*) FROM opportunity.page_set WHERE workspace_id = @ws", ws)).Should().Be(3);
            (await VersionAsync(h, ws, "DOC-1")).Should().Be(versionBefore + 1);
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
    public async Task An_import_with_an_OPT_fails_before_any_write_when_no_volume_share_is_configured()
    {
        await using var h = await ImportHarness.CreateAsync(postgres);
        var ws = await h.WorkspaceAsync();
        var batch = await h.StartAsync(ws, ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC"], ["X-1"])), opt: "X-1,V,IMAGES\\X-1.tif,Y,,,1\r\n"u8.ToArray());
        (await h.PrepareAsync(batch)).Should().Be(ImportPreparationOutcome.Failed);
        var job = (await h.Jobs.GetAsync(ws, batch.JobId, Ct))!;
        job.Status.Should().Be(JobStatus.Failed);
        (await h.CountAsync("SELECT count(*) FROM opportunity.document WHERE workspace_id = @ws", ws)).Should().Be(0);
    }

    [Fact]
    public async Task The_API_takes_an_OPT_with_the_DAT_or_alone_and_lists_OPT_issues_by_OPT_row()
    {
        var root = Path.Combine(Path.GetTempPath(), "opp-optvol-" + Guid.NewGuid().ToString("N"));
        try
        {
            var images = Directory.CreateDirectory(Path.Combine(root, "IMAGES"));
            await File.WriteAllBytesAsync(Path.Combine(images.FullName, "API-1.png"), PageImages.Png("API-1"), Ct);
            await using var h = await ImportHarness.CreateAsync(postgres);
            h.Volumes = new ImportVolumeOptions { VolumeShareRoot = root };
            var ws = await h.WorkspaceAsync();
            await using var factory = new Api.ApiFactory();
            var app = factory.WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:App", h.Db.AppConnectionString);
                b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(h.Store)));
            });
            using var client = app.CreateClient();

            var dat = ImportHarness.Utf8Bom(ImportHarness.Dat(["BEGDOC"], ["API-1"], ["API-2"]));
            var opt = "API-1,V,IMAGES\\API-1.png,Y,,,1\r\nAPI-2,V,IMAGES\\API-2.png,Y,,,1\r\nAPI-3,V,IMAGES\\API-3.png,Y,,,1\r\n"u8.ToArray();
            using var started = await PostAsync(client, ws, dat, opt, new { name = "With images" }, "opt-1");
            started.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
            var resource = JsonDocument.Parse(await started.Content.ReadAsStringAsync(Ct)).RootElement;
            resource.GetProperty("optFileName").GetString().Should().Be("VOL001.opt");
            resource.GetProperty("imagesOnly").GetBoolean().Should().BeFalse();
            var importId = resource.GetProperty("importId").GetGuid();
            await h.RunAsync((await h.Batches.GetAsync(ws, importId, Ct))!);

            var page = JsonDocument.Parse(await client.GetStringAsync(
                new Uri($"/api/v1/workspaces/{ws}/imports/{importId}/errors?includeWarnings=true", UriKind.Relative), Ct)).RootElement;
            string.Join(';', page.GetProperty("items").EnumerateArray().Select(i =>
                    $"{i.GetProperty("file").GetString()},{i.GetProperty("row").GetInt64()},{i.GetProperty("severity").GetString()},{i.GetProperty("code").GetString()}"))
                .Should().Be("opt,2,warning,opt-image-missing;opt,3,warning,opt-orphan-document");

            // An OPT alone is a page re-load of existing documents: an overlay only.
            using (var append = await PostAsync(client, ws, null, opt, new { mode = "append" }, "opt-2"))
            {
                append.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
            }

            using var alone = await PostAsync(client, ws, null, opt, new { }, "opt-3");
            alone.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted, await alone.Content.ReadAsStringAsync(Ct));
            var reload = JsonDocument.Parse(await alone.Content.ReadAsStringAsync(Ct)).RootElement;
            (reload.GetProperty("imagesOnly").GetBoolean(), reload.GetProperty("mode").GetString(), reload.GetProperty("sourceFileName").GetString())
                .Should().Be((true, "overlay", "VOL001.opt"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid ws, byte[]? dat, byte[]? opt, object request, string key)
    {
        using var form = new MultipartFormDataContent("import-test-boundary");
        if (dat is not null)
        {
            var file = new ByteArrayContent(dat);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", "VOL001.dat");
        }

        if (opt is not null)
        {
            var part = new ByteArrayContent(opt);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "opt", "VOL001.opt");
        }

        form.Add(new StringContent(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), Encoding.UTF8), "request");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/workspaces/{ws}/imports", UriKind.Relative)) { Content = form };
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message, Ct);
    }

    private static string PageKey(string controlNumber, int page) =>
        page <= 1 ? controlNumber : controlNumber + "." + page.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>OPT rows per document (a 'Y' row up to the next one), keyed by the break row's image key.</summary>
    private static Dictionary<string, int> PagesPerDocument(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        string? current = null;
        foreach (var parts in lines.Select(l => l.Split(',')))
        {
            if (parts[3] == "Y")
            {
                current = parts[0];
                result[current] = 0;
            }

            if (current is not null)
            {
                result[current]++;
            }
        }

        return result;
    }

    private static Task<long> VersionAsync(ImportHarness h, Guid ws, string controlNumber) => h.Db.ScalarAsync<long>(
        "SELECT s.document_version FROM opportunity.document_projection_state s JOIN opportunity.document d USING (workspace_id, document_id) WHERE d.workspace_id = @ws AND d.control_number = @cn",
        ("ws", ws), ("cn", controlNumber));

    private sealed record PageSetFacts(
        Guid DocumentId, long RowNo, Guid PageSetId, int PageCount, short Status, bool Active, bool ImagesIncomplete, int Pages, int MissingPages);

    /// <summary>The newest Imported page set of every document (superuser read).</summary>
    private static async Task<Dictionary<string, PageSetFacts>> PageSetsAsync(ImportHarness h, Guid ws)
    {
        var result = new Dictionary<string, PageSetFacts>(StringComparer.Ordinal);
        await using var command = h.Db.DataSource.CreateCommand(
            """
            SELECT DISTINCT ON (d.document_id) d.control_number, d.document_id, coalesce(m.row_no, 0), ps.page_set_id, ps.page_count, ps.status,
                   d.active_page_set_id = ps.page_set_id, d.images_incomplete,
                   (SELECT count(*) FROM opportunity.page p WHERE p.workspace_id = ps.workspace_id AND p.page_set_id = ps.page_set_id),
                   (SELECT count(*) FROM opportunity.page p WHERE p.workspace_id = ps.workspace_id AND p.page_set_id = ps.page_set_id AND p.image_missing)
            FROM opportunity.document d
            JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND ps.source = 1
            LEFT JOIN opportunity.import_batch_member m ON m.workspace_id = d.workspace_id AND m.document_id = d.document_id
            WHERE d.workspace_id = @ws
            ORDER BY d.document_id, ps.created_at DESC, ps.page_set_id DESC, m.row_no
            """);
        command.Parameters.AddWithValue("ws", ws);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetString(0)] = new PageSetFacts(
                reader.GetGuid(1), reader.GetInt64(2), reader.GetGuid(3), reader.GetInt32(4), reader.GetInt16(5), reader.GetBoolean(6), reader.GetBoolean(7),
                (int)reader.GetInt64(8), (int)reader.GetInt64(9));
        }

        return result;
    }

    /// <summary>A generator-made volume (VOL001) in a temp directory; disposing deletes it.</summary>
    private sealed class GeneratedVolume : IDisposable
    {
        private GeneratedVolume(string root) => Root = root;

        public string Root { get; }

        public static GeneratedVolume Create(int documents, VolumeOptions options)
        {
            var root = Path.Combine(Path.GetTempPath(), "opp-optvol-" + Guid.NewGuid().ToString("N"));
            CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), documents), Seed, root, new CorpusRunOptions
            {
                Threads = 1,
                WriteGroundTruth = false,
                SinkFactories = [context => new VolumeWriter(context, root, options)],
            });
            return new GeneratedVolume(root);
        }

        public byte[] Dat() => File.ReadAllBytes(Path.Combine(Root, "VOL001", "DATA", "VOL001.dat"));

        public byte[] Opt() => File.ReadAllBytes(Path.Combine(Root, "VOL001", "DATA", "VOL001.opt"));

        public List<string> OptLines() =>
            [.. Encoding.ASCII.GetString(Opt()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)];

        public List<Defect> Defects() =>
            [.. File.ReadLines(Path.Combine(Root, "volume-defects.jsonl")).Select(line =>
            {
                var e = JsonDocument.Parse(line).RootElement;
                int Int(string name) => e.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
                return new Defect(e.GetProperty("type").GetString()!, e.GetProperty("controlNumber").GetString()!, Int("page"), Int("optLine"),
                    Int("declaredPages"), Int("actualPages"));
            })];

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record Defect(string Type, string ControlNumber, int Page, int OptLine, int DeclaredPages, int ActualPages);
}
