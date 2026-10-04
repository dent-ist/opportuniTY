using System.Security.Cryptography;

using AwesomeAssertions;

using Opportunity.Application.Snapshots;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.UnitTests.Snapshots;

public class SnapshotRulesTests
{
    [Fact]
    public void Page_hash_uses_the_documented_big_endian_layout()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var expected = SHA256.HashData(Convert.FromHexString(
            "0000000000000007" + "00112233445566778899aabbccddeeff" + "000000000000002a" + "0002"));

        SnapshotHashing.PageHash([new SnapshotMember(7, id, 42, SnapshotInclusionReason.Explicit)]).Should().Equal(expected);
    }

    [Fact]
    public void Root_hash_depends_on_count_and_page_order()
    {
        byte[] a = [.. Enumerable.Repeat((byte)1, 32)], b = [.. Enumerable.Repeat((byte)2, 32)];

        SnapshotHashing.RootHash(2, [a, b]).Should().NotEqual(SnapshotHashing.RootHash(2, [b, a]));
        SnapshotHashing.RootHash(2, [a, b]).Should().NotEqual(SnapshotHashing.RootHash(3, [a, b]));
        SnapshotHashing.RootHash(0, []).Should().HaveCount(32);
    }

    [Theory]
    [InlineData(SnapshotPurpose.BulkCoding, Permission.CodingBulk, Permission.CodingBulk)]
    [InlineData(SnapshotPurpose.Export, Permission.ExportCreate, Permission.ExportCreate)]
    [InlineData(SnapshotPurpose.Production, Permission.ProductionCreate, Permission.ProductionCreate)]
    [InlineData(SnapshotPurpose.Report, Permission.SearchExecute, Permission.DocumentView)]
    public void Each_purpose_names_its_permissions(SnapshotPurpose purpose, Permission create, Permission member)
    {
        SnapshotRules.RequiredPermission(purpose).Should().Be(create);
        SnapshotRules.MemberPermission(purpose).Should().Be(member);
        SnapshotRules.RetainedForMatter(purpose).Should().Be(purpose != SnapshotPurpose.BulkCoding);
    }

    [Fact]
    public void Chunks_cover_every_ordinal_exactly_once()
    {
        var snapshot = new SnapshotRecord
        {
            WorkspaceId = Guid.NewGuid(),
            SnapshotId = Guid.NewGuid(),
            Status = SnapshotStatus.Ready,
            Name = "n",
            Purpose = SnapshotPurpose.BulkCoding,
            SourceKind = SnapshotSourceKind.DocumentIds,
            DocumentCount = 2_501,
            CreatedBy = Guid.NewGuid(),
            CreatedByDisplay = "u",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var chunks = DocumentSetSnapshotService.PlanChunks(snapshot, 1_000);

        chunks.Select(c => (c.RangeFrom, c.RangeTo)).Should().Equal(((long?)1L, (long?)1_000L), (1_001L, 2_000L), (2_001L, 2_501L));
        chunks.Should().OnlyContain(c => c.SnapshotId == snapshot.SnapshotId);
        FluentActions.Invoking(() => DocumentSetSnapshotService.PlanChunks(snapshot with { Status = SnapshotStatus.Materializing }, 10))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Options_reject_inconsistent_limits()
    {
        new SnapshotOptions().Validate();
        FluentActions.Invoking(() => new SnapshotOptions { SynchronousMaxDocuments = 10, MaxDocuments = 5 }.Validate())
            .Should().Throw<InvalidOperationException>();
    }
}
