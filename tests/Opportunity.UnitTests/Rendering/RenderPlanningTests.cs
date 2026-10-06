using AwesomeAssertions;

using Opportunity.Application.Rendering;
using Opportunity.Rendering.Jobs;
using Opportunity.Rendering.Renderers;

namespace Opportunity.UnitTests.Rendering;

/// <summary>E11-T02 chunk planning (ADR-010 §6 bounds) and deterministic output identifiers.</summary>
public sealed class RenderPlanningTests
{
    [Fact]
    public void Chunks_are_bounded_by_documents_known_pages_and_native_bytes()
    {
        var options = new RenderJobOptions { DocumentsPerChunk = 3, PagesPerChunk = 100, SourceBytesPerChunk = 1_000 };
        var candidates = new List<RenderCandidate>
        {
            new(Id(1), 10, 0), new(Id(2), 10, 0), new(Id(3), 10, 0), new(Id(4), 10, 0), // 3 documents, then a new chunk
            new(Id(5), 95, 0),                                                            // pages: 10 + 95 > 100
            new(Id(6), 0, 900), new(Id(7), 0, 200),                                       // bytes: 900 + 200 > 1,000
            new(Id(8), 5_000, 0),                                                         // oversized: a chunk of its own
            new(Id(9), 1, 1),
        };

        var plans = RenderCoordinator.Plan(candidates, options);

        plans.Select(p => p.Membership.DocumentIds!.Select(d => d.ToByteArray()[15]).ToArray()).Should().BeEquivalentTo(
            new[] { new byte[] { 1, 2, 3 }, [4], [5, 6], [7], [8], [9] }, o => o.WithStrictOrdering());
        plans.Select(p => p.ItemCount).Should().Equal(3, 1, 2, 1, 1, 1);
        RenderCoordinator.Plan([], options).Should().BeEmpty();
    }

    [Fact]
    public void Output_identifiers_are_deterministic_per_source_renderer_and_settings()
    {
        var document = Guid.CreateVersion7();
        var sha = new byte[32];
        var identity = new RasterRenderer().Identity;
        var otherSettings = new RasterRenderer(new RenderSettings { ReviewDpi = 300 }).Identity;

        RenderIds.NativePageSet(document, sha, identity).Should().Be(RenderIds.NativePageSet(document, sha, new RasterRenderer().Identity));
        RenderIds.NativePageSet(document, sha, otherSettings).Should().NotBe(RenderIds.NativePageSet(document, sha, identity));
        RenderIds.NativePageSet(document, [.. sha[..31], 1], identity).Should().NotBe(RenderIds.NativePageSet(document, sha, identity));
        RenderIds.ImportedRendition(document, identity).Should().NotBe(RenderIds.NativePageSet(document, sha, identity));
        RenderIds.NativePageSet(document, sha, identity).ToString()[14].Should().Be('8', "a version 8 (name-based, SHA-256) UUID");
    }

    [Fact]
    public void A_render_scope_round_trips_through_job_parameters()
    {
        var scope = new RenderScope(Guid.CreateVersion7(), Guid.CreateVersion7());

        RenderScope.FromParameters(scope.ToParameters()).Should().Be(scope);
        RenderScope.FromParameters(new RenderScope(scope.ImportJobId, null).ToParameters()).Should().Be(scope with { ImportBatchId = null });
        RenderScope.FromParameters([]).Should().BeNull();
    }

    private static Guid Id(byte n)
    {
        var bytes = new byte[16];
        bytes[15] = n;
        return new Guid(bytes);
    }
}
