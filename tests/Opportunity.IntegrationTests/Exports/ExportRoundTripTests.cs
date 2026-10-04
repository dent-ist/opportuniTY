using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Coding;
using Opportunity.Application.Exports;
using Opportunity.Application.Fields;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Import;
using Opportunity.Core.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.Jobs;
using Opportunity.Core.Security;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;
using Opportunity.DataGenerator.Corpus.Volumes;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Volumes;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Production.Exports;

namespace Opportunity.IntegrationTests.Exports;

/// <summary>
/// E12-T01 acceptance: a generator-made volume (natives, text, page images, families) is imported, coded, frozen and
/// exported; the exported DAT/OPT/natives/text re-import into a new workspace with the existing importer, and exporting
/// that workspace with the same settings reproduces the same fields, families, coding, text, natives and pages.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class ExportRoundTripTests(MigrationPostgresFixture postgres)
{
    private const ulong Seed = 20261005;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_exported_volume_re_imports_with_identical_documents_families_coding_text_natives_and_pages()
    {
        var root = TempDirectory("opp-export-src-");
        var package = TempDirectory("opp-export-pkg-");
        try
        {
            CorpusRunner.Run(ProfileSerializer.WithDocumentCount(new CorpusProfile(), 40), Seed, root, new CorpusRunOptions
            {
                Threads = 1,
                WriteGroundTruth = false,
                SinkFactories = [context => new VolumeWriter(context, root, new VolumeOptions())],
            });

            await using var h = await ExportHarness.CreateAsync(postgres, documentsPerChunk: 7);

            // Workspace A: the generated volume, imported with the existing importer, then coded.
            h.Import.Volumes = new ImportVolumeOptions { VolumeShareRoot = root };
            var source = await h.Import.WorkspaceAsync();
            var sourceImport = await h.Import.StartAsync(source, await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.dat"), Ct),
                new ImportProfileDefinition { Paths = new PathSettings { VolumeRoot = "VOL001" } }, name: "VOL001.dat",
                opt: await File.ReadAllBytesAsync(Path.Combine(root, "VOL001", "DATA", "VOL001.opt"), Ct));
            (await h.Import.RunAsync(sourceImport)).Status.Should().Be(JobStatus.Completed);
            (await h.Import.IssuesAsync(sourceImport)).Should().BeEmpty();
            var sourceCoding = await CodingFieldsAsync(h, source);
            var documents = await DocumentIdsAsync(h, source);
            documents.Should().HaveCount(40);
            await CodeAsync(h, source, sourceCoding, documents);
            (await h.Db.ScalarAsync<long>("SELECT count(DISTINCT family_id) FROM opportunity.document WHERE workspace_id = @ws", ("ws", source)))
                .Should().BeLessThan(40, "the generated volume has families");
            (await h.Db.ScalarAsync<long>("SELECT count(*) FROM opportunity.page WHERE workspace_id = @ws", ("ws", source))).Should().BePositive();

            var user = await h.UserAsync(source, WorkspaceRole.ProductionManager);
            var snapshot = await h.SnapshotAsync(source, user, documents);
            var fieldNames = await ExportableFieldNamesAsync(h, source);
            var first = await h.RunAsync(await h.CreateAsync(source, user, Request(snapshot.SnapshotId, await FieldsAsync(h, source, fieldNames))));

            first.Status.Should().Be(ExportStatus.Completed);
            first.Report!.DocumentsExported.Should().Be(40);
            first.Report.DocumentsExcluded.Should().Be(0);
            first.Report.Natives.Should().Be(40);
            first.Report.Texts.Should().Be(40);
            first.Report.Pages.Should().BePositive();
            var firstFiles = await h.FilesAsync(first);
            AssertManifest(firstFiles, first);

            // Workspace B: the exported package, unpacked as a recipient would, re-imported with the same importer.
            await h.ExtractAsync(first, package);
            h.Import.Volumes = new ImportVolumeOptions { VolumeShareRoot = package };
            var target = await h.Import.WorkspaceAsync();
            var targetCoding = await CodingFieldsAsync(h, target);
            var reimport = await h.Import.StartAsync(target, firstFiles["VOL001/DATA/VOL001.dat"].Bytes,
                new ImportProfileDefinition
                {
                    Columns =
                    [
                        new ColumnMapping { Column = "Responsive", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = targetCoding.Responsive }] },
                        new ColumnMapping { Column = "Issues", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = targetCoding.Issues }] },
                        new ColumnMapping { Column = "Review Notes", Targets = [new MappingTarget { Kind = MappingTargetKind.Field, FieldId = targetCoding.Notes }] },
                    ],
                },
                name: "VOL001.dat", opt: firstFiles["VOL001/DATA/VOL001.opt"].Bytes,
                codingFields: [targetCoding.Responsive, targetCoding.Issues, targetCoding.Notes]);
            (await h.Import.RunAsync(reimport)).Status.Should().Be(JobStatus.Completed);
            (await h.Import.IssuesAsync(reimport)).Should().BeEmpty();
            var reimported = await DocumentIdsAsync(h, target);
            reimported.Should().HaveCount(40);
            (await FamiliesAsync(h, target)).Should().BeEquivalentTo(await FamiliesAsync(h, source), "families round-trip by parent control number");

            // Export B with the same fields (by name) and settings: the volumes must agree.
            await h.UserAsync(target, WorkspaceRole.ProductionManager, user);
            var targetSnapshot = await h.SnapshotAsync(target, user, reimported);
            var second = await h.RunAsync(await h.CreateAsync(target, user, Request(targetSnapshot.SnapshotId, await FieldsAsync(h, target, fieldNames))));
            second.Status.Should().Be(ExportStatus.Completed);
            var secondFiles = await h.FilesAsync(second);

            // Natives, text and page images: the same files at the same paths with the same SHA-256.
            Artifacts(secondFiles).Should().BeEquivalentTo(Artifacts(firstFiles));
            Encoding.UTF8.GetString(secondFiles["VOL001/DATA/VOL001.opt"].Bytes).Should().Be(Encoding.UTF8.GetString(firstFiles["VOL001/DATA/VOL001.opt"].Bytes));

            // Fields and coding: every DAT value equal, except the group identifiers, which a new workspace renumbers
            // (compared as groupings).
            var a = await ParseDatAsync(firstFiles["VOL001/DATA/VOL001.dat"].Bytes);
            var b = await ParseDatAsync(secondFiles["VOL001/DATA/VOL001.dat"].Bytes);
            a.Header.Should().Contain(["Control Number", "Beg Attach", "End Attach", "Responsive", "Issues", "Review Notes", "FamilyID", "ParentID",
                "NativePath", "TextPath"]);
            b.Header.Should().Equal(a.Header);
            b.Rows.Should().HaveCount(a.Rows.Count);
            string[] groups = ["Duplicate Group", "Email Thread Group"];
            for (var r = 0; r < a.Rows.Count; r++)
            {
                for (var c = 0; c < a.Header.Count; c++)
                {
                    if (!groups.Contains(a.Header[c]))
                    {
                        b.Rows[r][c].Should().Be(a.Rows[r][c], $"row {r + 1} ({a.Rows[r][0]}), column {a.Header[c]}");
                    }
                }
            }

            foreach (var column in groups.Select(g => a.Header.IndexOf(g)))
            {
                Partition(b.Rows, column).Should().BeEquivalentTo(Partition(a.Rows, column));
            }

            a.Rows.Count(r => r[a.Header.IndexOf("Responsive")] == "Yes").Should().BePositive("the coding is in the volume");
            a.Rows.Count(r => r[a.Header.IndexOf("ParentID")].Length > 0).Should().BePositive("attachments name their parent");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(package, recursive: true);
        }
    }

    internal static CreateExportRequest Request(Guid snapshotId, IReadOnlyList<ExportFieldRequest> fields) => new(snapshotId, fields);

    /// <summary>Every live field of the workspace except the computed flags, then the family columns.</summary>
    private static async Task<List<string>> ExportableFieldNamesAsync(ExportHarness h, Guid ws)
    {
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        int[] computed = [SystemFields.TextLength, SystemFields.TextTruncated, SystemFields.TextMissing, SystemFields.NativeMissing, SystemFields.ImagesIncomplete,
            SystemFields.DuplicatePrimary];
        return [.. catalog.Fields.Where(f => !f.IsDeleted && !computed.Contains(f.FieldId)).OrderBy(f => f.FieldId).Select(f => f.Name)];
    }

    private static async Task<List<ExportFieldRequest>> FieldsAsync(ExportHarness h, Guid ws, List<string> names)
    {
        var catalog = await h.Db.Fields.GetCatalogAsync(ws, cancellationToken: Ct);
        var byName = catalog.Fields.Where(f => !f.IsDeleted).ToDictionary(f => f.Name, StringComparer.Ordinal);
        return [.. names.Select(n => new ExportFieldRequest(FieldId: byName[n].FieldId)), new(Column: ExportColumnResource.FamilyId), new(Column: ExportColumnResource.ParentId)];
    }

    internal sealed record CodingFields(int Responsive, int Issues, int Notes, int Pricing, int Antitrust);

    internal static async Task<CodingFields> CodingFieldsAsync(ExportHarness h, Guid ws)
    {
        async Task<int> Field(NewField field) => (await h.Db.Fields.CreateFieldAsync(field, Ct)).Value!.FieldId;
        async Task<int> Choice(int fieldId, string name) => (await h.Db.Fields.AddChoiceAsync(ws, fieldId, name, Ct)).Value!.ChoiceId;
        var responsive = await Field(new NewField(ws, "Responsive", FieldType.Boolean, FieldStorage.Coding));
        var issues = await Field(new NewField(ws, "Issues", FieldType.MultiChoice, FieldStorage.Coding));
        var notes = await Field(new NewField(ws, "Review Notes", FieldType.Text, FieldStorage.Coding));
        return new CodingFields(responsive, issues, notes, await Choice(issues, "Pricing"), await Choice(issues, "Antitrust"));
    }

    internal static async Task CodeAsync(ExportHarness h, Guid ws, CodingFields f, IReadOnlyList<Guid> documents)
    {
        for (var i = 0; i < documents.Count; i += 3)
        {
            var result = await h.Db.Coding.ApplyAsync(new CodingWriteRequest
            {
                WorkspaceId = ws,
                IdempotencyKey = "code-" + Guid.CreateVersion7().ToString("N"),
                Actor = new CodingActor(Guid.CreateVersion7(), CodingActorType.Human),
                Documents = [new CodingTarget(documents[i])],
                Operations =
                [
                    CodingFieldOperation.Set(f.Responsive, JsonValue.Create(i % 2 == 0)),
                    i % 2 == 0 ? CodingFieldOperation.AddChoices(f.Issues, f.Pricing, f.Antitrust) : CodingFieldOperation.AddChoices(f.Issues, f.Antitrust),
                    CodingFieldOperation.Set(f.Notes, JsonValue.Create($"Reviewed \"batch\" {i}; see memo")),
                ],
            }, Ct);
            result.Outcome.Should().Be(CodingWriteOutcome.Applied);
        }
    }

    internal static async Task<List<Guid>> DocumentIdsAsync(ExportHarness h, Guid ws) =>
        [.. (await h.Db.ColumnAsync($"SELECT document_id::text FROM opportunity.document WHERE workspace_id = '{ws}' ORDER BY control_number")).Select(Guid.Parse)];

    /// <summary>Control number → parent control number (empty for top-level documents).</summary>
    private static async Task<Dictionary<string, string>> FamiliesAsync(ExportHarness h, Guid ws) =>
        (await h.Db.ColumnAsync(
            $"""
            SELECT d.control_number || '|' || coalesce(p.control_number, '') || '|' || t.control_number
            FROM opportunity.document d
            LEFT JOIN opportunity.document p ON p.workspace_id = d.workspace_id AND p.document_id = d.parent_document_id
            JOIN opportunity.document t ON t.workspace_id = d.workspace_id AND t.document_id = d.family_id
            WHERE d.workspace_id = '{ws}'
            """))
        .Select(v => v.Split('|'))
        .ToDictionary(p => p[0], p => p[1] + "|" + p[2], StringComparer.Ordinal);

    internal static void AssertManifest(Dictionary<string, (ExportFileRecord File, byte[] Bytes)> files, ExportRecord export)
    {
        files.Keys.Should().Contain([ExportLayout.ManifestJson, ExportLayout.ManifestCsv, ExportLayout.ExclusionsCsv, "VOL001/DATA/VOL001.dat"]);
        var manifestBytes = files[ExportLayout.ManifestJson].Bytes;
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifestBytes)).Should().Be(Convert.ToHexStringLower(export.Report!.ManifestSha256));
        var manifest = JsonNode.Parse(manifestBytes)!;
        var listed = manifest["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["sha256"]!.GetValue<string>());
        listed.Keys.Should().BeEquivalentTo(files.Keys.Where(k => k is not (ExportLayout.ManifestJson or ExportLayout.ManifestCsv)));
        foreach (var (path, sha) in listed)
        {
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(files[path].Bytes)).Should().Be(sha, path);
        }

        manifest["documents"]!["exported"]!.GetValue<long>().Should().Be(export.Report.DocumentsExported);
        export.Report.Files.Should().Be(files.Count);
    }

    private static Dictionary<string, string> Artifacts(Dictionary<string, (ExportFileRecord File, byte[] Bytes)> files) =>
        files.Where(f => f.Value.File.Kind is ExportFileKind.Native or ExportFileKind.Text or ExportFileKind.Image)
            .ToDictionary(f => f.Key, f => Convert.ToHexStringLower(f.Value.File.Sha256), StringComparer.Ordinal);

    internal static async Task<(List<string> Header, List<List<string>> Rows)> ParseDatAsync(byte[] dat)
    {
        await using var reader = await DatReader.OpenAsync(new MemoryStream(dat), new DatReaderOptions(), cancellationToken: Ct);
        reader.HasPreflightErrors.Should().BeFalse();
        var rows = new List<List<string>>();
        await foreach (var record in reader.ReadAllAsync(Ct))
        {
            rows.Add([.. record.Values]);
        }

        reader.Issues.Should().BeEmpty();
        return ([.. reader.Header.Names], rows);
    }

    private static List<List<int>> Partition(List<List<string>> rows, int column) =>
        [.. rows.Select((r, i) => (Value: r[column], Index: i)).Where(x => x.Value.Length > 0).GroupBy(x => x.Value)
            .Select(g => g.Select(x => x.Index).Order().ToList()).OrderBy(g => g[0])];

    internal static string TempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
