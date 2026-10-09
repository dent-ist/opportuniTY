using AwesomeAssertions;

using Opportunity.Core.ReviewBatches;
using Opportunity.Core.Security;
using Opportunity.Core.Snapshots;

namespace Opportunity.UnitTests.ReviewBatches;

/// <summary>E10-T05: cutting keep-together groups into batches, batch names, and the ReviewBatch snapshot purpose.</summary>
public sealed class ReviewBatchPackerTests
{
    [Fact]
    public void Single_documents_fill_batches_up_to_the_size()
    {
        var packer = new ReviewBatchPacker(3);
        Enumerable.Range(0, 7).Select(_ => packer.Place(1)).ToList().Should().Equal(1, 1, 1, 2, 2, 2, 3);
    }

    [Fact]
    public void A_family_that_does_not_fit_starts_the_next_batch_and_is_never_split()
    {
        var packer = new ReviewBatchPacker(5);
        packer.Place(2).Should().Be(1);
        packer.Place(2).Should().Be(1);
        packer.Place(3).Should().Be(2, "2 + 2 + 3 would exceed 5, so the family moves to the next batch whole");
        packer.Place(2).Should().Be(2, "3 + 2 fits exactly");
        packer.Place(1).Should().Be(3);
    }

    [Fact]
    public void A_group_larger_than_the_size_gets_a_batch_of_its_own()
    {
        var packer = new ReviewBatchPacker(4);
        packer.Place(1).Should().Be(1);
        packer.Place(9).Should().Be(2, "an oversized family is kept whole in its own batch");
        packer.Place(1).Should().Be(3, "nothing joins a batch that is already over the size");
        packer.CurrentBatch.Should().Be(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ReviewBatchRules.MaxBatchSize + 1)]
    public void Batch_size_is_bounded(int size) =>
        FluentActions.Invoking(() => new ReviewBatchPacker(size)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Batches_are_named_prefix_and_four_digit_number()
    {
        ReviewBatchRules.BatchName("FirstPass", 1).Should().Be("FirstPass_0001");
        ReviewBatchRules.BatchName("QC", 12345).Should().Be("QC_12345");
    }

    [Theory]
    [InlineData("FirstPass", true)]
    [InlineData("QC-2026.10 a_b", true)]
    [InlineData("", false)]
    [InlineData(" lead", false)]
    [InlineData("bad/slash", false)]
    [InlineData("tab\there", false)]
    public void Prefixes_are_plain_names(string prefix, bool valid) => ReviewBatchRules.IsValidPrefix(prefix).Should().Be(valid);

    [Fact]
    public void Review_batch_snapshots_need_the_manage_permission_and_hold_viewable_documents()
    {
        SnapshotRules.RequiredPermission(SnapshotPurpose.ReviewBatch).Should().Be(Permission.ReviewBatchManage);
        SnapshotRules.MemberPermission(SnapshotPurpose.ReviewBatch).Should().Be(Permission.DocumentView);
        SnapshotRules.RetainedForMatter(SnapshotPurpose.ReviewBatch).Should().BeTrue();
        SnapshotStrategyRules.AcceptsSnapshot(SetOperationKind.ReviewBatch, SnapshotPurpose.ReviewBatch).Should().BeTrue();
        RoleCatalog.Get(WorkspaceRole.QcReviewer).Grants.Should().Contain(Permission.ReviewBatchManage);
        RoleCatalog.Get(WorkspaceRole.Reviewer).Grants.Should().NotContain(Permission.ReviewBatchManage);
    }
}
