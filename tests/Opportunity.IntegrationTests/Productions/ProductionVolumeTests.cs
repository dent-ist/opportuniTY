using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using BitMiracle.LibTiff.Classic;

using Opportunity.Application.Coding;
using Opportunity.Application.Exports;
using Opportunity.Application.Productions;
using Opportunity.Application.Redactions;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Import;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Redactions;
using Opportunity.Core.Security;
using Opportunity.Data.Redactions;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.Volumes;
using Opportunity.IntegrationTests.Exports;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Productions;
using Opportunity.Production.Volumes;

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E12-T05 acceptance against PostgreSQL and the file-system object store: a generated, imported volume (families,
/// natives, text, page images) is produced with a redaction, a designation, natively produced and withheld file types;
/// the volume's images carry the burned redactions and endorsements while the sources stay unchanged, every placeholder
/// and slip sheet takes exactly one Bates number and a DAT row, OPT rows = images = Bates span for every member, a re-run
/// and a run that crashed mid-chunk write byte-identical volumes, and the volume re-imports with 0 errors and the same
/// page counts and Bates numbers.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ProductionVolumeTests(MigrationPostgresFixture postgres)
{
    private const ulong Seed = 20261009;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_finalized_production_is_written_with_burned_redactions_endorsements_placeholders_and_load_files_reproducibly()
    {
        var root = ExportRoundTripTests.TempDirectory("opp-volume-src-");
        var package = ExportRoundTripTests.TempDirectory("opp-volume-pkg-");
        try
        {
            CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), 16), Seed, root, new CorpusRunOptions
            {
                Threads = 1,
                WriteGroundTruth = false,
                SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions())],
            });
            await using var v = await ProductionVolumeHarness.CreateAsync(postgres, documentsPerChunk: 5);
            var h = v.Exports;
            h.Import.Volumes = new ImportVolumeOptions { VolumeShareRoot = root };
            var ws = await h.Db.CreateWorkspaceAsync();
            await h.Db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
            var batch = await h.Import.StartAsync(ws, await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.dat"), Ct),
                new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL001" } }, name: "VOL001.dat",
                opt: await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.opt"), Ct));
            (await h.Import.RunAsync(batch)).Status.Should().Be(JobStatus.Completed);
            (await h.Import.IssuesAsync(batch)).Should().BeEmpty();
            var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
            var documents = await ExportRoundTripTests.DocumentIdsAsync(h, ws);
            var sources = await SourcesAsync(v, ws);

            // The redacted document has a TIFF first page; one other file type is produced natively, another withheld.
            var redacted = sources.First(s => s.FirstImageFormat == 1 && s.Pages > 0 && s.Extension is { Length: > 0 });
            var others = sources.Where(s => s.Extension is { Length: > 0 } e && e != redacted.Extension).GroupBy(s => s.Extension!).ToList();
            others.Should().HaveCountGreaterThanOrEqualTo(2, "the generated volume has several file types");
            var nativeType = others.First(g => g.All(s => s.HasNative)).Key;
            var withheldType = others.First(g => g.Key != nativeType).Key;
            var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
            var designationField = catalog.Fields.Single(f => f.SecurityClass == SecurityClass.ConfidentialityDesignation);
            var confidential = catalog.ChoicesOf(designationField.FieldId).Single(c => c.Name == "CONFIDENTIAL").ChoiceId;
            (await h.Db.Coding.ApplyAsync(new CodingWriteRequest
            {
                WorkspaceId = ws,
                IdempotencyKey = "designate-" + Guid.CreateVersion7().ToString("N"),
                Actor = new CodingActor(user, CodingActorType.Human),
                Documents = [new CodingTarget(redacted.DocumentId)],
                Operations = [CodingFieldOperation.Set(designationField.FieldId, JsonValue.Create(confidential))],
            }, Ct)).Outcome.Should().Be(CodingWriteOutcome.Applied);

            var redactions = new RedactionStore(h.Db.AppDataSource);
            var set = (await redactions.ListSetsAsync(ws, Ct)).Single();
            var black = new NormalizedRect(100_000, 100_000, 300_000, 150_000);
            var labelled = new NormalizedRect(450_000, 500_000, 400_000, 120_000);
            (await redactions.SaveAsync(ws, redacted.DocumentId, set.RedactionSetId, 0, null, user,
            [
                new PlannedRevision(Guid.CreateVersion7(), RedactionOperation.Add, redacted.PageSetId, 1, black, RedactionType.Black, "PII", null),
                new PlannedRevision(Guid.CreateVersion7(), RedactionOperation.Add, redacted.PageSetId, 1, labelled, RedactionType.Labelled, "AttorneyClient", null),
            ], [], Ct)).Should().Be(RedactionWriteStatus.Ok);
            var sourceImage = await StoredImageAsync(v, ws, redacted.PageSetId);

            // Draft, allocate and finalize (page-level Bates), then write the volume.
            var snapshot = await v.Productions.SnapshotAsync(ws, user, documents);
            var draft = await v.Productions.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("PRD") with
            {
                FileTypeRules =
                [
                    new ProductionFileTypeRule([nativeType], ProductionOutputResource.Native),
                    new ProductionFileTypeRule([withheldType], ProductionOutputResource.Placeholder),
                ],
            });
            var allocated = await v.Productions.AllocateAsync(ws, user, draft.ProductionId);
            allocated.BatesState.Should().Be(BatesAllocationState.Allocated, allocated.BatesReason);
            var finalized = await v.Productions.Service().FinalizeAsync(ProductionHarness.Principal(user), ws, draft.ProductionId, allocated.RowVersion, Ct);
            finalized.Status.Should().Be(ProductionOutcomeStatus.Ok, finalized.Reason);
            var members = await v.Productions.AssignmentAsync(ws, draft.ProductionId);
            members.Should().OnlyContain(m => m.RedactionSetId == set.RedactionSetId);
            members.Single(m => m.DocumentId == redacted.DocumentId).Should().BeEquivalentTo(new { RedactionVersion = 1L, RedactionCount = 2 });

            var first = await v.RunAsync(await v.StartAsync(ws, user, draft.ProductionId));
            first.Status.Should().Be(ExportStatus.Completed, first.StatusReason);
            first.Report!.DocumentsExported.Should().Be(members.Count);
            var files = await v.FilesAsync(first);
            var spec = ProductionSpecificationRules.Deserialize(allocated.SpecificationJson);
            var volumeName = "PRD_VOL001";
            files.Keys.Should().Contain([$"{volumeName}/DATA/{volumeName}.dat", $"{volumeName}/DATA/{volumeName}.opt", "MANIFEST.json", "MANIFEST.csv"]);
            AssertManifest(files, first);

            // DAT: one row per member with the production columns.
            var (header, rows) = await ExportRoundTripTests.ParseDatAsync(files[$"{volumeName}/DATA/{volumeName}.dat"].Bytes);
            header.Should().StartWith(["ProdBegBates", "ProdEndBates", "ProdBegAttach", "ProdEndAttach"]);
            header.Should().Contain(["FileName", "MD5Hash", "Confidentiality", "Redacted", "PageCount", "NativeLink", "TextLink"]);
            rows.Should().HaveCount(members.Count);
            string Cell(List<string> row, string column) => row[header.IndexOf(column)];
            var byBeg = rows.ToDictionary(r => Cell(r, "ProdBegBates"));

            // OPT rows = image files = Bates span for every member (AC 3).
            var opt = Encoding.UTF8.GetString(files[$"{volumeName}/DATA/{volumeName}.opt"].Bytes)
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(',')).ToList();
            var images = files.Values.Where(f => f.File.Kind == ExportFileKind.Image).ToList();
            opt.Should().HaveCount(images.Count);
            opt.Select(o => o[2].Replace('\\', '/')).Should().BeEquivalentTo(images.Select(i => i.File.Path));
            var format = ProductionSpecificationRules.FormatOf(spec);
            foreach (var member in members)
            {
                var span = (int)(member.EndNumber!.Value - member.BegNumber!.Value + 1);
                var rowsOfMember = opt.SkipWhile(o => o[0] != member.ProdBegBates).Take(span).ToList();
                rowsOfMember.Should().HaveCount(span);
                rowsOfMember[0][3].Should().Be("Y");
                rowsOfMember[0][6].Should().Be(span.ToString(CultureInfo.InvariantCulture));
                rowsOfMember.Skip(1).Should().OnlyContain(o => o[3] == string.Empty);
                rowsOfMember.Select(o => o[0]).Should().Equal(Enumerable.Range(0, span).Select(i => format.Format(member.BegNumber.Value + i)));
                images.Count(i => i.File.Ordinal == member.Sequence).Should().Be(span, member.ProdBegBates);
                Cell(byBeg[member.ProdBegBates!], "PageCount").Should().Be(span.ToString(CultureInfo.InvariantCulture));
                Cell(byBeg[member.ProdBegBates!], "ProdEndBates").Should().Be(member.ProdEndBates);
                Cell(byBeg[member.ProdBegBates!], "ProdBegAttach").Should().Be(member.ProdBegAttach);
            }

            // Placeholders and slip sheets each consume exactly one Bates number and appear in the DAT (AC 2).
            var verification = await v.FilesAsync(first, [ExportFileKind.Verification]);
            var pageRecords = ParseCsv(verification[ProductionVolumeLayout.VerificationPath].Bytes);
            var withheld = members.Where(m => m.Output == Core.Productions.ProductionOutputKind.Placeholder).ToList();
            var natives = members.Where(m => m.Output == Core.Productions.ProductionOutputKind.Native).ToList();
            withheld.Should().NotBeEmpty();
            natives.Should().NotBeEmpty();
            foreach (var member in withheld.Concat(natives))
            {
                member.Units.Should().Be(1);
                member.ProdBegBates.Should().Be(member.ProdEndBates);
                Cell(byBeg[member.ProdBegBates!], "PageCount").Should().Be("1");
                pageRecords.Where(r => r[1] == member.ProdBegBates).Select(r => r[8]).Should().Equal(
                    member.Output == Core.Productions.ProductionOutputKind.Native ? ProductionVolumeChunkExecutor.PageKindSlipSheet : ProductionVolumeChunkExecutor.PageKindPlaceholder);
            }

            foreach (var member in natives)
            {
                Cell(byBeg[member.ProdBegBates!], "NativeLink").Should().StartWith($@"{volumeName}\NATIVES\NATIVE0001\{member.ProdBegBates}.");
                files.Keys.Should().Contain(Cell(byBeg[member.ProdBegBates!], "NativeLink").Replace('\\', '/'));
            }

            foreach (var member in withheld)
            {
                Cell(byBeg[member.ProdBegBates!], "NativeLink").Should().BeEmpty();
                Encoding.UTF8.GetString(files[Cell(byBeg[member.ProdBegBates!], "TextLink").Replace('\\', '/')].Bytes).Should().Be("Withheld – Privileged");
            }

            // The redacted member: imaged only, its text never the original, its pages carry the burned boxes and the
            // endorsement bands, and its designation is in the load file (AC 1).
            var redactedMember = members.Single(m => m.DocumentId == redacted.DocumentId);
            var redactedRow = byBeg[redactedMember.ProdBegBates!];
            Cell(redactedRow, "Redacted").Should().Be("Yes");
            Cell(redactedRow, "Confidentiality").Should().Be("CONFIDENTIAL");
            Cell(redactedRow, "NativeLink").Should().BeEmpty();
            Encoding.UTF8.GetString(files[Cell(redactedRow, "TextLink").Replace('\\', '/')].Bytes).Should().Be(ProductionVolumeSettings.RedactedText);
            var firstPage = pageRecords.Single(r => r[0] == redactedMember.ProdBegBates);
            firstPage[8].Should().Be(ProductionVolumeChunkExecutor.PageKindSource);
            var produced = files[firstPage[2]].Bytes;
            Convert.ToHexStringLower(SHA256.HashData(produced)).Should().Be(firstPage[3]);
            var (width, height, pixels) = ReadBitonalTiff(produced);
            var pageTop = int.Parse(firstPage[6], CultureInfo.InvariantCulture);
            var pageHeight = int.Parse(firstPage[7], CultureInfo.InvariantCulture);
            pageTop.Should().Be(0, "the default endorsements are stamped in a band below the page");
            height.Should().BeGreaterThan(pageTop + pageHeight, "the bottom endorsement band holds the Bates number and designation");
            var boxes = firstPage[11].Split(';').Select(b => b.Split(' ')).ToList();
            boxes.Should().HaveCount(2);
            var blackBox = boxes.Single(b => b[4] == "Black").Take(4).Select(n => int.Parse(n, CultureInfo.InvariantCulture)).ToArray();
            blackBox.Should().Equal(Expected(black, width, pageHeight, pageTop));
            Fraction(pixels, width, blackBox, ink: true).Should().Be(1d, "every pixel inside a black box is black");
            var whiteBox = boxes.Single(b => b[4] == "Labelled").Take(4).Select(n => int.Parse(n, CultureInfo.InvariantCulture)).ToArray();
            Fraction(pixels, width, whiteBox, ink: true).Should().BeInRange(0.001, 0.5, "a labelled box is white with its label printed in it");
            Fraction(pixels, width, [0, pageTop + pageHeight, width, height - pageTop - pageHeight], ink: true).Should().BePositive("the bottom band is endorsed");
            (await StoredImageAsync(v, ws, redacted.PageSetId)).Should().Equal(sourceImage, "the source image is never changed");
            await AssertSourceIntactAsync(v, ws, redacted.PageSetId);

            // A re-run writes byte-identical files and manifest (Q-08).
            var second = await v.RunAsync(await v.StartAsync(ws, user, draft.ProductionId));
            second.Status.Should().Be(ExportStatus.Completed);
            second.Report!.ManifestSha256.Should().Equal(first.Report.ManifestSha256);
            Hashes(await v.FilesAsync(second)).Should().Equal(Hashes(files));
            (await v.FilesAsync(second, [ExportFileKind.Verification]))[ProductionVolumeLayout.VerificationPath].Bytes
                .Should().Equal(verification[ProductionVolumeLayout.VerificationPath].Bytes);
            (await h.Db.ColumnAsync($"SELECT action FROM audit.audit_event WHERE workspace_id = '{ws}' AND category = 'Production' AND action IN ('Run', 'Rerun', 'VolumeCompleted') ORDER BY occurred_at"))
                .Should().Equal("Run", "VolumeCompleted", "Rerun", "VolumeCompleted");

#if OPPORTUNITY_FAILPOINTS
            // A worker that dies mid-chunk: the chunk is redone after its lease expires and the volume is the same.
            var crashed = await v.StartAsync(ws, user, draft.ProductionId);
            await v.CoordinateAsync(ws);
            var chunks = await h.Import.Jobs.GetChunksAsync(ws, crashed.Job.JobId, cancellationToken: Ct);
            chunks.Should().HaveCountGreaterThan(1);
            var crashing = new CrashingStore(v.Store) { CrashAfterPuts = 2 };
            var crash = () => v.Consumer(crashing).HandleAsync(ProductionVolumeHarness.Payload(chunks[0]), ProductionVolumeHarness.Received(chunks[0]), Ct);
            await crash.Should().ThrowAsync<Application.Faults.SimulatedCrashException>();
            await h.Db.ExecuteAsync(
                "UPDATE opportunity.job_chunk SET lease_expires_at = now() - interval '1 minute' WHERE workspace_id = @ws AND status = 3", ("ws", ws));
            (await h.Import.Chunks.RecoverExpiredLeasesAsync(ws, TimeSpan.Zero, cancellationToken: Ct)).ReturnedToPending.Should().Be(1);
            (await v.DeliverOpenChunksAsync(ws, crashed.Job.JobId)).Should().Be(chunks.Count);
            await v.CoordinateAsync(ws);
            var resumed = (await h.Exports.GetAsync(ws, crashed.Export.ExportId, Ct))!;
            resumed.Status.Should().Be(ExportStatus.Completed);
            resumed.Report!.ManifestSha256.Should().Equal(first.Report.ManifestSha256, "the resumed run wrote the same bytes");
            Hashes(await v.FilesAsync(resumed)).Should().Equal(Hashes(files));
#endif

            // Round trip (AC 4): the volume re-imports with 0 errors, the same page counts and the same Bates numbers.
            foreach (var (path, (_, bytes)) in files)
            {
                var target = Path.Combine([package, .. path.Split('/')]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, Ct);
            }

            h.Import.Volumes = new ImportVolumeOptions { VolumeShareRoot = package };
            var recipient = await h.Import.WorkspaceAsync();
            var reimport = await h.Import.StartAsync(recipient, files[$"{volumeName}/DATA/{volumeName}.dat"].Bytes, new ImportProfileDefinition
            {
                Columns =
                [
                    Map("ProdBegBates", new MappingTarget { Kind = MappingTargetKind.Field, FieldId = SystemFields.ControlNumber },
                        new MappingTarget { Kind = MappingTargetKind.Field, FieldId = SystemFields.BegBates }),
                    Map("ProdEndBates", new MappingTarget { Kind = MappingTargetKind.Field, FieldId = SystemFields.EndBates }),
                    Map("NativeLink", new MappingTarget { Kind = MappingTargetKind.Structural, Structural = StructuralTarget.NativePath }),
                    Map("TextLink", new MappingTarget { Kind = MappingTargetKind.Structural, Structural = StructuralTarget.TextPath }),
                ],
            }, name: $"{volumeName}.dat", opt: files[$"{volumeName}/DATA/{volumeName}.opt"].Bytes);
            (await h.Import.RunAsync(reimport)).Status.Should().Be(JobStatus.Completed);
            (await h.Import.IssuesAsync(reimport)).Should().BeEmpty();
            var imported = (await h.Db.ColumnAsync(
                $"""
                SELECT d.control_number || '|' || d.beg_bates || '|' || d.end_bates || '|' || coalesce(ps.page_count, 0)
                FROM opportunity.document d
                LEFT JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id
                WHERE d.workspace_id = '{recipient}'
                """)).Select(r => r.Split('|')).ToDictionary(r => r[0]);
            imported.Should().HaveCount(members.Count);
            foreach (var member in members)
            {
                imported[member.ProdBegBates!].Should().Equal(member.ProdBegBates, member.ProdBegBates, member.ProdEndBates,
                    (member.EndNumber!.Value - member.BegNumber!.Value + 1).ToString(CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task Only_finalized_productions_get_volumes_and_the_export_routes_never_show_them()
    {
        await using var v = await ProductionVolumeHarness.CreateAsync(postgres);
        var ws = await v.Db.CreateWorkspaceAsync();
        await v.Db.Fields.InitializeNewWorkspaceAsync(ws, Ct);
        var user = await v.Exports.UserAsync(ws, WorkspaceRole.ProductionManager);
        var docs = await v.Productions.FamiliesAsync(ws, "FIN", [(0, "pdf"), (0, "msg")], [(0, "zip")]);
        var snapshot = await v.Productions.SnapshotAsync(ws, user, docs);
        var draft = await v.Productions.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("FIN") with
        {
            FileTypeRules = [new ProductionFileTypeRule(["zip"], ProductionOutputResource.Placeholder)],
        });
        var refused = await v.Service().StartAsync(ProductionHarness.Principal(user), ws, draft.ProductionId, null, Ct);
        refused.Status.Should().Be(ProductionVolumeStartStatus.Conflict);
        (await v.Service().StartAsync(ProductionHarness.Principal(user), ws, Guid.CreateVersion7(), null, Ct)).Status.Should().Be(ProductionVolumeStartStatus.NotFound);

        var allocated = await v.Productions.AllocateAsync(ws, user, draft.ProductionId);
        (await v.Productions.Service().FinalizeAsync(ProductionHarness.Principal(user), ws, draft.ProductionId, allocated.RowVersion, Ct))
            .Status.Should().Be(ProductionOutcomeStatus.Ok);

        // Documents without page images: every page is a "Technical Issue" page; the withheld one a placeholder.
        var volume = await v.RunAsync(await v.StartAsync(ws, user, draft.ProductionId));
        volume.Status.Should().Be(ExportStatus.Completed, volume.StatusReason);
        var records = ParseCsv((await v.FilesAsync(volume, [ExportFileKind.Verification]))[ProductionVolumeLayout.VerificationPath].Bytes);
        records.Select(r => r[8]).Should().Equal(
            ProductionVolumeChunkExecutor.PageKindTechnicalIssue, ProductionVolumeChunkExecutor.PageKindTechnicalIssue, ProductionVolumeChunkExecutor.PageKindPlaceholder);
        (await v.Exports.Exports.ListAsync(ws, null, null, 10, Ct)).Should().BeEmpty("a volume run is never listed as an export");
        (await v.Exports.Exports.GetActiveAsync(ws, 10, Ct)).Should().BeEmpty();
        (await v.Exports.Exports.ListVolumesAsync(ws, draft.ProductionId, null, 10, Ct)).Should().ContainSingle();
    }

    internal sealed record Source(Guid DocumentId, string? Extension, int Pages, Guid PageSetId, short? FirstImageFormat, bool HasNative);

    internal static async Task<List<Source>> SourcesAsync(ProductionVolumeHarness v, Guid ws) =>
        [.. (await v.Db.ColumnAsync(
            $"""
            SELECT d.document_id::text || '|' || coalesce(lower(d.file_extension), '') || '|' || coalesce(ps.page_count, 0) || '|'
                   || coalesce(ps.page_set_id::text, '00000000-0000-0000-0000-000000000000') || '|'
                   || coalesce((SELECT pi.format FROM opportunity.page_image pi WHERE pi.workspace_id = d.workspace_id AND pi.page_set_id = ps.page_set_id
                                AND pi.ordinal = 1 AND pi.purpose = 1)::text, '') || '|' || (d.native_object_id IS NOT NULL)::text
            FROM opportunity.document d
            LEFT JOIN opportunity.page_set ps ON ps.workspace_id = d.workspace_id AND ps.page_set_id = d.active_page_set_id
            WHERE d.workspace_id = '{ws}'
            ORDER BY d.control_number
            """)).Select(r => r.Split('|')).Select(p => new Source(
                Guid.Parse(p[0]), p[1].Length > 0 ? p[1] : null, int.Parse(p[2], CultureInfo.InvariantCulture), Guid.Parse(p[3]),
                p[4].Length > 0 ? short.Parse(p[4], CultureInfo.InvariantCulture) : null, p[5] == "true"))];

    internal static async Task<byte[]> StoredImageAsync(ProductionVolumeHarness v, Guid ws, Guid pageSetId)
    {
        var key = await v.Db.ScalarAsync<string>(
            """
            SELECT o.logical_key FROM opportunity.page_image pi
            JOIN opportunity.stored_object o ON o.workspace_id = pi.workspace_id AND o.object_id = pi.object_id
            WHERE pi.workspace_id = @ws AND pi.page_set_id = @ps AND pi.ordinal = 1 AND pi.purpose = 1
            """, ("ws", ws), ("ps", pageSetId));
        await using var stream = await v.Store.OpenReadAsync(ObjectKey.Parse(key), cancellationToken: Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private static async Task AssertSourceIntactAsync(ProductionVolumeHarness v, Guid ws, Guid pageSetId)
    {
        var registered = await v.Db.ScalarAsync<byte[]>(
            """
            SELECT o.sha256 FROM opportunity.page_image pi
            JOIN opportunity.stored_object o ON o.workspace_id = pi.workspace_id AND o.object_id = pi.object_id
            WHERE pi.workspace_id = @ws AND pi.page_set_id = @ps AND pi.ordinal = 1 AND pi.purpose = 1
            """, ("ws", ws), ("ps", pageSetId));
        SHA256.HashData(await StoredImageAsync(v, ws, pageSetId)).Should().Equal(registered, "the source still matches its registered SHA-256");
    }

    /// <summary>MANIFEST.json lists every other delivered file with its SHA-256, and its own SHA-256 is the run's.</summary>
    private static void AssertManifest(Dictionary<string, (ExportFileRecord File, byte[] Bytes)> files, ExportRecord volume)
    {
        var manifestBytes = files["MANIFEST.json"].Bytes;
        SHA256.HashData(manifestBytes).Should().Equal(volume.Report!.ManifestSha256);
        var manifest = JsonNode.Parse(manifestBytes)!;
        manifest["kind"]!.GetValue<string>().Should().Be("productionVolume");
        var listed = manifest["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["sha256"]!.GetValue<string>());
        listed.Keys.Should().BeEquivalentTo(files.Keys.Where(k => k is not ("MANIFEST.json" or "MANIFEST.csv")));
        foreach (var (path, sha) in listed)
        {
            Convert.ToHexStringLower(SHA256.HashData(files[path].Bytes)).Should().Be(sha, path);
        }

        manifest.ToJsonString().Should().NotContain(volume.ExportId.ToString()).And.NotContain(volume.JobId.ToString(), "nothing run-specific");
        volume.Report.Files.Should().Be(files.Count);
    }

    private static int[] Expected(NormalizedRect rect, int width, int pageHeight, int pageTop)
    {
        var box = RedactionGeometry.BurnedPixels(rect, width, pageHeight);
        return [box.X, box.Y + pageTop, box.Width, box.Height];
    }

    private static ColumnMapping Map(string column, params MappingTarget[] targets) => new() { Column = column, Targets = targets };

    private static Dictionary<string, string> Hashes(Dictionary<string, (ExportFileRecord File, byte[] Bytes)> files) =>
        files.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(f.Value.Bytes)), StringComparer.Ordinal);

    internal static List<string[]> ParseCsv(byte[] bytes) =>
        [.. Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(','))];

    /// <summary>A single-page TIFF G4 as one bool per pixel (true: black).</summary>
    private static (int Width, int Height, bool[] Pixels) ReadBitonalTiff(byte[] tiff)
    {
        using var stream = new MemoryStream(tiff);
        using var image = Tiff.ClientOpen("produced", "r", stream, new TiffStream())!;
        image.GetField(TiffTag.COMPRESSION)[0].ToInt().Should().Be((int)Compression.CCITTFAX4);
        image.NumberOfDirectories().Should().Be(1);
        var width = image.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        var height = image.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var whiteIsZero = image.GetField(TiffTag.PHOTOMETRIC)[0].ToInt() == (int)Photometric.MINISWHITE;
        var line = new byte[image.ScanlineSize()];
        var pixels = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            image.ReadScanline(line, y).Should().BeTrue();
            for (var x = 0; x < width; x++)
            {
                var bit = (line[x >> 3] & (0x80 >> (x & 7))) != 0;
                pixels[y * width + x] = whiteIsZero ? bit : !bit;
            }
        }

        return (width, height, pixels);
    }

    private static double Fraction(bool[] pixels, int width, int[] box, bool ink)
    {
        long hits = 0, total = 0;
        for (var y = box[1]; y < box[1] + box[3]; y++)
        {
            for (var x = box[0]; x < box[0] + box[2]; x++)
            {
                total++;
                if (pixels[y * width + x] == ink)
                {
                    hits++;
                }
            }
        }

        return total == 0 ? 0 : (double)hits / total;
    }

#if OPPORTUNITY_FAILPOINTS
    /// <summary>An object store that "kills the worker" (a simulated crash) after a number of puts.</summary>
    private sealed class CrashingStore(IObjectStore inner) : IObjectStore
    {
        private int _puts;

        public int CrashAfterPuts { get; set; }

        public async Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _puts) > CrashAfterPuts)
            {
                throw new Application.Faults.SimulatedCrashException("The rendering worker died mid-chunk.");
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
}
