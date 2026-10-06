using Opportunity.Core.Jobs;

namespace Opportunity.Rendering.Jobs;

/// <summary>Render worker settings (ADR-010 §6 initial values).</summary>
public sealed class RenderJobOptions
{
    /// <summary>Documents per render chunk (ADR-010 §6: 100).</summary>
    public int DocumentsPerChunk { get; init; } = ChunkBounds.For(JobType.Render).MaxItems;

    /// <summary>Known pages (of Imported sets) per chunk; a larger document is a chunk of its own.</summary>
    public int PagesPerChunk { get; init; } = 1_000;

    /// <summary>Native bytes per chunk; a larger native is a chunk of its own.</summary>
    public long SourceBytesPerChunk { get; init; } = 512L * 1024 * 1024;

    /// <summary>Pages rendered between lease extensions (fence F2) of a long document.</summary>
    public int PagesPerHeartbeat { get; init; } = 25;

    /// <summary>How often the render coordinator looks for finished imports and render jobs to plan.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Directory for the per-document source copy and page files; the system temp directory when null.</summary>
    public string? TempDirectory { get; init; }
}
