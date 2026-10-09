using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Exports;
using Opportunity.Application.Productions;
using Opportunity.Core.Storage;
using Opportunity.Production.Volumes;

namespace Opportunity.UnitTests.Productions;

/// <summary>
/// E12-T06: the text and native rules of the burn-in verification (replacement text only; no native without a recorded
/// native-redaction method), the report rows and their totals.
/// </summary>
public sealed class BurnInVerificationRulesTests
{
    private static readonly byte[] Replacement = Encoding.UTF8.GetBytes(ProductionVolumeSettings.RedactedText);

    [Fact]
    public void A_redacted_documents_text_must_be_the_replacement_text()
    {
        var original = new ExportSourceObject("ws/original", SHA256.HashData("the privileged original"u8), 23, "text/plain");

        BurnInVerification.Text(null, Replacement, original).Should().BeEmpty("no text file ships nothing");
        BurnInVerification.Text(File(Replacement), Replacement, original).Should().BeEmpty();
        BurnInVerification.Text(File("the privileged original"u8.ToArray()), Replacement, original)
            .Should().Equal(new BurnInFinding(BurnInCodes.OriginalTextShipped));
        BurnInVerification.Text(File("something else"u8.ToArray()), Replacement, original)
            .Should().Equal(new BurnInFinding(BurnInCodes.TextNotReplaced));
        BurnInVerification.Text(File([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(ProductionVolumeSettings.RedactedText)]), Replacement, null)
            .Should().Equal([new BurnInFinding(BurnInCodes.TextNotReplaced)], "the expected bytes are in the volume's encoding");
    }

    [Fact]
    public void A_redacted_documents_native_needs_a_recorded_method()
    {
        BurnInVerification.Native(null).Should().BeEmpty();
        BurnInVerification.Native(File([1, 2, 3])).Should().Equal(new BurnInFinding(BurnInCodes.NativeShipped));
        BurnInVerification.Native(File([1, 2, 3]), recordedMethod: "native-redaction-tool 1").Should().BeEmpty();
    }

    [Fact]
    public void Report_rows_are_content_free_and_their_totals_count_checks_pages_and_boxes()
    {
        string[] rows =
        [
            BurnInVerification.Row("ABC0000001", "ABC0000001", BurnInVerification.CheckImage, 2, []),
            BurnInVerification.Row("ABC0000001", "ABC0000002", BurnInVerification.CheckImage, 1,
                [new BurnInFinding(BurnInCodes.BoxNotOpaque, 1, 120), new BurnInFinding(BurnInCodes.HiddenData)]),
            BurnInVerification.Row("ABC0000001", null, BurnInVerification.CheckText, 0, []),
            BurnInVerification.Row("ABC0000001", null, BurnInVerification.CheckNative, 0, []),
            BurnInVerification.Row("-X,1", null, BurnInVerification.CheckText, 0, [new BurnInFinding(BurnInCodes.OriginalTextShipped)]),
        ];

        rows[1].Should().Be("ABC0000001,ABC0000002,Image,Failed,1,BoxNotOpaque#1:120;HiddenData\r\n");
        BurnInVerification.ParseRow(rows[4].TrimEnd('\r', '\n')).Should().Equal("'-X,1", string.Empty, "Text", "Failed", "0", "OriginalTextShipped");
        BurnInVerification.Summarize(rows.Select(r => r.TrimEnd('\r', '\n'))).Should().Be(new BurnInSummary(2, 2, 3, 2, 2));
        BurnInVerification.Summarize([]).Passed.Should().BeTrue("a production without redacted or withheld members has nothing to fail");
        var malformed = () => BurnInVerification.Summarize(["a,b"]);
        malformed.Should().Throw<FormatException>();
    }

    private static NewExportFile File(byte[] content) =>
        new("VOL/TEXT/TEXT0001/ABC0000001.txt", ExportFileKind.Text, "ws/key", SHA256.HashData(content), content.Length, "application/octet-stream", "k1",
            EncryptionScheme.ProviderSse);
}
