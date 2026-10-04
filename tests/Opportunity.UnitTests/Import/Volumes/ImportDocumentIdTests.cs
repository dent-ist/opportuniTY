using AwesomeAssertions;

using Opportunity.Import.Jobs;
using Opportunity.Import.Volumes;

namespace Opportunity.UnitTests.Import.Volumes;

/// <summary>E08-T04: retried chunks address the same document keys; content types come from bytes only.</summary>
public class ImportDocumentIdTests
{
    [Fact]
    public void Row_document_ids_are_stable_unique_uuid_v7()
    {
        var batch = Guid.CreateVersion7();

        var ids = Enumerable.Range(1, 10_000).Select(row => ImportDocumentIds.For(batch, row)).ToList();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().Equal(Enumerable.Range(1, 10_000).Select(row => ImportDocumentIds.For(batch, row)));
        ids.Should().OnlyContain(id => id.Version == 7 && (id.Variant & 0xC) == 0x8);
        ImportDocumentIds.For(Guid.CreateVersion7(), 1).Should().NotBe(ids[0]);
        ids[0].ToString("N")[..12].Should().Be(batch.ToString("N")[..12], "the import's own timestamp");
    }

    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "application/pdf")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14 }, "application/zip")]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "application/x-ole-storage")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, "image/tiff")]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C }, "application/octet-stream")]
    [InlineData(new byte[0], "application/octet-stream")]
    public void Content_types_are_sniffed_from_leading_bytes(byte[] head, string expected) =>
        ContentSniffer.Sniff(head).Should().Be(expected);
}
