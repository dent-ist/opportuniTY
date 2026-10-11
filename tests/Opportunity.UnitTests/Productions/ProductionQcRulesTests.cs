using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Productions;
using Opportunity.Core.Productions;
using Opportunity.Import.LoadFiles;
using Opportunity.Production.Exports;
using Opportunity.Production.Productions;
using Opportunity.Production.Volumes;

namespace Opportunity.UnitTests.Productions;

/// <summary>
/// E12-T07: how the QC gate turns document counts into check statuses and an outcome (overrides only for checks that may
/// be overridden and only at finalization, warnings acknowledged only at finalization), its canonical report, the
/// deterministic PDF writer of the QC report, and the volume reconciliation of the load files read back.
/// </summary>
public sealed class ProductionQcRulesTests
{
    private static readonly ProductionRecord Production = new()
    {
        WorkspaceId = Guid.Parse("018f0000-0000-7000-8000-000000000001"),
        ProductionId = Guid.Parse("018f0000-0000-7000-8000-000000000002"),
        LineageId = Guid.Parse("018f0000-0000-7000-8000-000000000002"),
        Version = 1,
        Name = "First production",
        SnapshotId = Guid.Parse("018f0000-0000-7000-8000-000000000003"),
        SpecificationJson = "{}",
        SpecificationSha256 = new byte[32],
        BatesPrefix = "ABC",
        BatesSuffix = string.Empty,
        BatesPadding = 7,
        BatesStart = 1,
        Status = ProductionStatus.Draft,
        RowVersion = 3,
        BatesState = BatesAllocationState.Allocated,
        BatesFirst = 1,
        BatesLast = 12,
        BatesDocuments = 5,
        CreatedBy = Guid.Parse("018f0000-0000-7000-8000-000000000004"),
        CreatedByDisplay = "Tester",
        CreatedAt = DateTimeOffset.UnixEpoch,
        ModifiedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_check_is_listed_once_and_only_render_failures_redact_calls_and_privilege_conflicts_may_be_overridden()
    {
        ProductionQcRules.Checks.Select(c => c.Check).Should().OnlyHaveUniqueItems().And.HaveCount(Enum.GetValues<ProductionQcCheck>().Length);
        ProductionQcRules.Checks.Where(c => c.Overridable).Select(c => c.Check).Should().BeEquivalentTo(
            [ProductionQcCheck.RedactWithoutRedactions, ProductionQcCheck.RenderFailure, ProductionQcCheck.PrivilegeConflicts]);
        ProductionQcRules.Checks.Where(c => c.Severity == ProductionQcSeverity.Warning).Select(c => c.Check).Should().BeEquivalentTo(
            [ProductionQcCheck.IncompleteFamily, ProductionQcCheck.TextMissing, ProductionQcCheck.BlankConfidentiality]);
        ProductionQcRules.Find("RENDERFAILURE")!.Check.Should().Be(ProductionQcCheck.RenderFailure);
        ProductionQcRules.Find("nope").Should().BeNull();
    }

    [Fact]
    public void Failures_block_unless_overridden_at_finalization_and_warnings_need_an_acknowledgement()
    {
        var counts = new Dictionary<ProductionQcCheck, long>
        {
            [ProductionQcCheck.RenderFailure] = 2,
            [ProductionQcCheck.RedactedNative] = 1,
            [ProductionQcCheck.TextMissing] = 4,
        };

        var check = ProductionQcRules.Evaluate(Production, Request(ProductionQcPurpose.Check, [Override(ProductionQcCheck.RenderFailure)], true), counts);
        check.Outcome.Should().Be(ProductionQcOutcome.Blocked);
        check[ProductionQcCheck.RenderFailure].Status.Should().Be(ProductionQcStatus.Failed, "a check run applies no override");
        check[ProductionQcCheck.TextMissing].Status.Should().Be(ProductionQcStatus.Warning);
        check[ProductionQcCheck.WithheldWithoutPlaceholder].Status.Should().Be(ProductionQcStatus.Passed);

        // A non-overridable failure stays failed even with an override in the request.
        var final = ProductionQcRules.Evaluate(Production,
            Request(ProductionQcPurpose.Finalization, [Override(ProductionQcCheck.RenderFailure), Override(ProductionQcCheck.RedactedNative)], true), counts);
        final.Outcome.Should().Be(ProductionQcOutcome.Blocked);
        final[ProductionQcCheck.RenderFailure].Should().BeEquivalentTo(new { Status = ProductionQcStatus.Overridden, Documents = 2L, OverrideReason = "Reason for RenderFailure" });
        final[ProductionQcCheck.RedactedNative].Status.Should().Be(ProductionQcStatus.Failed);

        counts.Remove(ProductionQcCheck.RedactedNative);
        ProductionQcRules.Evaluate(Production, Request(ProductionQcPurpose.Finalization, [Override(ProductionQcCheck.RenderFailure)], false), counts)
            .Outcome.Should().Be(ProductionQcOutcome.Blocked, "warnings are not acknowledged");
        var passed = ProductionQcRules.Evaluate(Production, Request(ProductionQcPurpose.Finalization, [Override(ProductionQcCheck.RenderFailure)], true), counts);
        passed.Outcome.Should().Be(ProductionQcOutcome.Passed);
        passed[ProductionQcCheck.TextMissing].Status.Should().Be(ProductionQcStatus.Acknowledged);
        passed.Overridden.Select(c => c.Definition.Check).Should().Equal(ProductionQcCheck.RenderFailure);

        // An override of a check that passed is not used.
        ProductionQcRules.Evaluate(Production, Request(ProductionQcPurpose.Finalization, [Override(ProductionQcCheck.PrivilegeConflicts)], true), counts)
            [ProductionQcCheck.PrivilegeConflicts].Should().BeEquivalentTo(new { Status = ProductionQcStatus.Passed, OverrideReason = (string?)null });
    }

    [Fact]
    public void The_report_is_canonical_hashed_and_reads_back()
    {
        var counts = new Dictionary<ProductionQcCheck, long> { [ProductionQcCheck.RenderFailure] = 2, [ProductionQcCheck.IncompleteFamily] = 1 };
        var request = Request(ProductionQcPurpose.Finalization, [Override(ProductionQcCheck.RenderFailure)], true);
        var result = ProductionQcRules.Evaluate(Production, request, counts);
        ProductionQcRules.Evaluate(Production, request, counts).ReportJson.Should().Be(result.ReportJson, "equal input, equal bytes");
        result.ReportSha256.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(result.ReportJson)));
        result.ReportJson.Should().Contain("\"check\":\"renderFailure\"").And.Contain("\"status\":\"overridden\"")
            .And.Contain("\"reason\":\"Reason for RenderFailure\"").And.Contain("\"batesFirst\":\"ABC0000001\"").And.NotContain("\n");

        var read = ProductionQcRules.Read(new ProductionQcRunRecord(result.QcRunId, Production.ProductionId, result.Purpose, result.Outcome, result.RunBy,
            result.RunAt, result.ReportJson, result.ReportSha256));
        read.Checks.Should().BeEquivalentTo(result.Checks);
        read.WarningsAcknowledged.Should().BeTrue();
        ProductionQcRules.AuditDetails(read).Should().Contain(new KeyValuePair<string, string?>("Overridden", "renderFailure"))
            .And.Contain(new KeyValuePair<string, string?>("Documents.incompleteFamily", "1"));
    }

    [Fact]
    public void The_pdf_writer_is_deterministic_and_escapes_text()
    {
        static byte[] Write()
        {
            var pdf = new PdfTextDocument { Footer = "QC (report)" };
            pdf.Add("Production QC report", 16, bold: true);
            for (var i = 0; i < 200; i++)
            {
                pdf.Add($"Line {i}: a (parenthesised) back\\slash – dash and a long line that wraps " + new string('x', 150), 8, indent: 14);
            }

            using var buffer = new MemoryStream();
            pdf.WriteTo(buffer);
            return buffer.ToArray();
        }

        var bytes = Write();
        bytes.Should().Equal(Write());
        var text = Encoding.Latin1.GetString(bytes);
        text.Should().StartWith("%PDF-1.4").And.EndWith("%%EOF\n").And.Contain("\\(parenthesised\\)").And.Contain("back\\\\slash").And.Contain("\\226 dash");
        text.Should().Contain("/Count ").And.Contain("Page 1 of ");
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        text[startxref..].Should().StartWith("xref");
    }

    [Fact]
    public async Task A_volume_reconciles_when_images_opt_rows_bates_span_natives_and_texts_agree()
    {
        var settings = new ExportSettings
        {
            Columns =
            [
                new ExportColumn(ExportColumnKind.ProdBegBates, null, "ProdBegBates"),
                new ExportColumn(ExportColumnKind.ProdEndBates, null, "ProdEndBates"),
                new ExportColumn(ExportColumnKind.NativePath, null, "NativeLink"),
                new ExportColumn(ExportColumnKind.TextPath, null, "TextLink"),
            ],
        };
        var format = new BatesFormat("ABC", 7, string.Empty, BatesNumberingLevel.Page);
        string[][] rows =
        [
            ["ABC0000001", "ABC0000002", string.Empty, @"VOL001\TEXT\TEXT0001\ABC0000001.txt"],
            ["ABC0000003", "ABC0000003", @"VOL001\NATIVES\NATIVE0001\ABC0000003.xlsx", string.Empty],
        ];
        string[] opt = ["ABC0000001", "ABC0000002", "ABC0000003"];
        var good = await ReconcileAsync(settings, format, rows, opt, new VolumeRegistration(2, 3, 1, 1, 3));
        good.Passed.Should().BeTrue(string.Join(" ", good.Problems));
        good.Should().BeEquivalentTo(new { DatRows = 2L, OptRows = 3L, BatesSpan = 3L, NativeLinks = 1L, TextLinks = 1L });

        (await ReconcileAsync(settings, format, rows, opt[..2], new VolumeRegistration(2, 3, 1, 1, 3))).Problems.Should().ContainSingle()
            .Which.Should().Contain("OPT has 2 row(s) for 3 image file(s)");
        (await ReconcileAsync(settings, format, rows, opt, new VolumeRegistration(2, 3, 2, 1, 3))).Problems.Should().ContainSingle()
            .Which.Should().Contain("2 native file(s) for 1 DAT NativeLink value(s)");
        (await ReconcileAsync(settings, format, rows, opt, new VolumeRegistration(2, 3, 1, 0, 3))).Problems.Should().ContainSingle()
            .Which.Should().Contain("0 text file(s) for 1 DAT TextLink value(s)");
        (await ReconcileAsync(settings, format, rows[..1], opt, new VolumeRegistration(2, 3, 0, 1, 3))).Problems
            .Should().Contain(p => p.Contains("The DAT has 1 row(s) for 2 document(s)", StringComparison.Ordinal))
            .And.Contain(p => p.Contains("3 image file(s) for a Bates span of 2", StringComparison.Ordinal));
    }

    private static async Task<VolumeReconciliationResult> ReconcileAsync(
        ExportSettings settings, BatesFormat format, string[][] rows, string[] opt, VolumeRegistration registration)
    {
        var dat = new StringBuilder(LoadFileText.DatRow(settings.Profile, [.. settings.Columns.Select(c => c.Header)]));
        foreach (var row in rows)
        {
            dat.Append(LoadFileText.DatRow(settings.Profile, row));
        }

        var optText = new StringBuilder();
        for (var i = 0; i < opt.Length; i++)
        {
            optText.Append(LoadFileText.OptRow(opt[i], "VOL001", $@"VOL001\IMAGES\IMG0001\{opt[i]}.tif", i == 0, i == 0 ? opt.Length : null));
        }

        using var datStream = new MemoryStream([.. LoadFileEncodings.Preamble(settings.DatEncoding).ToArray(), .. LoadFileText.Encode(settings.DatEncoding, dat.ToString())]);
        using var optStream = new MemoryStream(Encoding.UTF8.GetBytes(optText.ToString()));
        return await VolumeReconciliation.ReconcileAsync(datStream, optStream, settings, format, registration, TestContext.Current.CancellationToken);
    }

    private static ProductionQcOverride Override(ProductionQcCheck check) => new(check, "Reason for " + check);

    private static ProductionQcRequest Request(ProductionQcPurpose purpose, IReadOnlyList<ProductionQcOverride> overrides, bool acknowledge) => new(
        Guid.Parse("018f0000-0000-7000-8000-000000000005"), purpose, Production.CreatedBy, At, [], overrides, acknowledge, DesignationPlan.None, null, true);
}
