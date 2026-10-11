using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Exports;
using Opportunity.Application.Productions;
using Opportunity.Application.Redactions;
using Opportunity.Contracts.Api;
using Opportunity.Contracts.Import;
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

#if OPPORTUNITY_FAILPOINTS
using Opportunity.Application.Faults;
#endif

namespace Opportunity.IntegrationTests.Productions;

/// <summary>
/// E12-T06 acceptance against PostgreSQL and the file-system object store: every volume run verifies its redacted and
/// withheld members (pages read back and checked pixel by pixel in the render session, replacement text, no native) and
/// records the result in the manifest, the run and the burn-in QC report (AC 1). Seeded leaks of the writer, armed
/// through test-only switches, are each detected and block the volume: the original text of a redacted document (AC 2),
/// its native without a recorded native-redaction method (AC 3) and pages whose redactions were not burned in. A
/// blocked run ends as Failed with its report, writes no load files or manifest and is never delivered.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class BurnInVerificationTests(MigrationPostgresFixture postgres)
{
    private const ulong Seed = 20261009;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_volume_is_verified_and_a_seeded_leak_blocks_it()
    {
        var root = ExportRoundTripTests.TempDirectory("opp-burnin-src-");
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
            var user = await h.UserAsync(ws, WorkspaceRole.ProductionManager);
            var documents = await ExportRoundTripTests.DocumentIdsAsync(h, ws);
            var sources = await ProductionVolumeTests.SourcesAsync(v, ws);
            var withText = (await h.Db.ColumnAsync($"SELECT document_id::text FROM opportunity.document WHERE workspace_id = '{ws}' AND text_object_id IS NOT NULL"))
                .Select(Guid.Parse).ToHashSet();

            // The redacted document: TIFF pages, a native and a text (so a leak of either can be seeded); another file type withheld.
            var redacted = sources.First(s => s.FirstImageFormat == 1 && s.Pages > 0 && s.HasNative && withText.Contains(s.DocumentId)
                && s.Extension is { Length: > 0 });
            var withheldType = sources.First(s => s.Extension is { Length: > 0 } e && e != redacted.Extension).Extension!;
            var redactions = new RedactionStore(h.Db.AppDataSource);
            var set = (await redactions.ListSetsAsync(ws, Ct)).Single();
            List<PlannedRevision> boxes =
            [
                new(Guid.CreateVersion7(), RedactionOperation.Add, redacted.PageSetId, 1, new NormalizedRect(100_000, 100_000, 300_000, 150_000),
                    RedactionType.Black, "PII", null),
                new(Guid.CreateVersion7(), RedactionOperation.Add, redacted.PageSetId, 1, new NormalizedRect(450_000, 500_000, 400_000, 120_000),
                    RedactionType.Labelled, "AttorneyClient", null),
            ];
            if (redacted.Pages > 1)
            {
                boxes.Add(new(Guid.CreateVersion7(), RedactionOperation.Add, redacted.PageSetId, 2, NormalizedRect.FullPage, RedactionType.Black, "PII", null));
            }

            (await redactions.SaveAsync(ws, redacted.DocumentId, set.RedactionSetId, 0, null, user, boxes, [], Ct)).Should().Be(RedactionWriteStatus.Ok);

            var snapshot = await v.Productions.SnapshotAsync(ws, user, documents);
            var draft = await v.Productions.CreateOkAsync(ws, user, snapshot.SnapshotId, ProductionHarness.Spec("BIV") with
            {
                FileTypeRules = [new ProductionFileTypeRule([withheldType], ProductionOutputResource.Placeholder)],
            });
            var allocated = await v.Productions.AllocateAsync(ws, user, draft.ProductionId);
            (await v.Productions.Service().FinalizeAsync(ProductionHarness.Principal(user), ws, draft.ProductionId, allocated.RowVersion, ProductionHarness.Acknowledged, Ct))
                .Status.Should().Be(ProductionOutcomeStatus.Ok);
            var members = await v.Productions.AssignmentAsync(ws, draft.ProductionId);
            var member = members.Single(m => m.DocumentId == redacted.DocumentId);
            var withheld = members.Where(m => m.Output == Core.Productions.ProductionOutputKind.Placeholder).ToList();
            withheld.Should().NotBeEmpty();
            var redactedPages = boxes.Select(b => b.Ordinal).Distinct().Count();

            // AC 1: a clean run is verified automatically; the result is in the run, the manifest and the QC report.
            var clean = await v.RunAsync(await v.StartAsync(ws, user, draft.ProductionId));
            clean.Status.Should().Be(ExportStatus.Completed, clean.StatusReason);
            clean.Verification.Should().NotBeNull();
            clean.Verification!.Should().BeEquivalentTo(new
            {
                Passed = true,
                Documents = 1L + withheld.Count,
                Pages = (long)redactedPages,
                Boxes = (long)boxes.Count,
                Failures = 0L,
            });
            clean.Report!.Verification.Should().BeEquivalentTo(clean.Verification);
            var report = await ReportAsync(v, clean);
            SHA256.HashData(report.Bytes).Should().Equal(clean.Verification.ReportSha256);
            report.Rows.Should().OnlyContain(r => r[3] == BurnInVerification.Passed);
            report.Rows.Where(r => r[0] == member.ProdBegBates).Select(r => r[2]).Should().Equal(
                [.. Enumerable.Repeat(BurnInVerification.CheckImage, redactedPages), BurnInVerification.CheckText, BurnInVerification.CheckNative]);
            report.Rows.Where(r => r[0] == member.ProdBegBates && r[2] == BurnInVerification.CheckImage)
                .Sum(r => int.Parse(r[4], CultureInfo.InvariantCulture)).Should().Be(boxes.Count, "every box was inspected");
            foreach (var placeholder in withheld)
            {
                report.Rows.Where(r => r[0] == placeholder.ProdBegBates).Select(r => r[2]).Should().Equal(BurnInVerification.CheckText, BurnInVerification.CheckNative);
            }

            var files = await v.FilesAsync(clean);
            var manifest = JsonNode.Parse(files["MANIFEST.json"].Bytes)!["burnInVerification"]!;
            manifest["status"]!.GetValue<string>().Should().Be("passed");
            manifest["verifier"]!.GetValue<string>().Should().Be(v.Imager.VerifierVersion);
            manifest["boxes"]!.GetValue<long>().Should().Be(boxes.Count);
            manifest["reportSha256"]!.GetValue<string>().Should().Be(Convert.ToHexStringLower(clean.Verification.ReportSha256));
            files.Keys.Should().NotContain(ProductionVolumeLayout.BurnInReportPath, "the QC report stays with the run and is never delivered");
            (await h.Db.ColumnAsync(
                $"SELECT details->>'BurnInVerification' FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'VolumeCompleted'"))
                .Should().Equal(BurnInVerification.Passed);

#if OPPORTUNITY_FAILPOINTS
            // AC 2: the original text shipped for a redacted document is detected and blocks the volume.
            var text = await SeededRunAsync(v, ws, user, draft.ProductionId, FaultFlags.VolumeShipsOriginalText);
            (await ReportAsync(v, text)).Rows.Where(r => r[3] == BurnInVerification.Failed).Should().ContainSingle()
                .Which.Should().Equal(member.ProdBegBates, string.Empty, BurnInVerification.CheckText, BurnInVerification.Failed, "0",
                    BurnInCodes.OriginalTextShipped);

            // AC 3: a redacted document's native in the volume without a recorded native-redaction method blocks it.
            var native = await SeededRunAsync(v, ws, user, draft.ProductionId, FaultFlags.VolumeShipsRedactedNative);
            (await ReportAsync(v, native)).Rows.Where(r => r[3] == BurnInVerification.Failed).Should().ContainSingle()
                .Which.Should().Equal(member.ProdBegBates, string.Empty, BurnInVerification.CheckNative, BurnInVerification.Failed, "0",
                    BurnInCodes.NativeShipped);

            // Pages produced without their redactions: the black boxes are not opaque (the labelled box lies on blank paper,
            // so nothing of the page shows through it and it passes).
            var unburned = await SeededRunAsync(v, ws, user, draft.ProductionId, FaultFlags.VolumeSkipsBurn);
            var failedPages = (await ReportAsync(v, unburned)).Rows.Where(r => r[3] == BurnInVerification.Failed).ToList();
            failedPages.Should().HaveCount(redactedPages).And.OnlyContain(r => r[0] == member.ProdBegBates && r[2] == BurnInVerification.CheckImage);
            failedPages[0][5].Should().StartWith(BurnInCodes.BoxNotOpaque + "#1:");

            // With the writer fixed, the next run is delivered again.
            v.Faults = null;
            var again = await v.RunAsync(await v.StartAsync(ws, user, draft.ProductionId));
            again.Status.Should().Be(ExportStatus.Completed);
            again.Report!.ManifestSha256.Should().Equal(clean.Report.ManifestSha256, "the verification is reproducible like the volume");
            (await h.Db.ColumnAsync(
                $"SELECT details->>'Failures' FROM audit.audit_event WHERE workspace_id = '{ws}' AND action = 'VerificationFailed' AND details->>'Check' = 'BurnIn' ORDER BY occurred_at"))
                .Should().Equal("1", "1", redactedPages.ToString(CultureInfo.InvariantCulture));
#endif
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

#if OPPORTUNITY_FAILPOINTS
    /// <summary>A run with one seeded leak: it fails its verification, keeps its report and writes nothing deliverable.</summary>
    private static async Task<ExportRecord> SeededRunAsync(ProductionVolumeHarness v, Guid ws, Guid user, Guid productionId, string flag)
    {
        v.Faults = new Switches(flag);
        var run = await v.RunAsync(await v.StartAsync(ws, user, productionId));
        run.Status.Should().Be(ExportStatus.Failed, flag);
        run.StatusReason.Should().StartWith("Burn-in verification failed:");
        run.Report.Should().BeNull();
        run.Verification.Should().BeEquivalentTo(new { Passed = false });
        run.Verification!.Failures.Should().BePositive();
        (await v.FilesAsync(run, [ExportFileKind.Dat, ExportFileKind.Opt, ExportFileKind.Manifest])).Should().BeEmpty(
            "a blocked volume gets no load files or manifest, and a run that is not Completed is never listed or downloaded");
        return run;
    }

    private sealed class Switches(string armed) : IFaultInjector
    {
        public ValueTask HitAsync(string failpoint, FailpointContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool IsArmed(string flag) => flag == armed;
    }
#endif

    private static async Task<(byte[] Bytes, List<string[]> Rows)> ReportAsync(ProductionVolumeHarness v, ExportRecord run)
    {
        var bytes = (await v.FilesAsync(run, [ExportFileKind.Verification]))[ProductionVolumeLayout.BurnInReportPath].Bytes;
        Encoding.UTF8.GetString(bytes).Split("\r\n")[0].Should().Be(string.Join(',', BurnInVerification.ReportHeader));
        return (bytes, ProductionVolumeTests.ParseCsv(bytes));
    }
}
