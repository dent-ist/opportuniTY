using System.Globalization;

using Opportunity.Core.Jobs;

namespace Opportunity.Production.Exports;

/// <summary>Export worker settings (ADR-010 §6 initial values).</summary>
public sealed class ExportJobOptions
{
    /// <summary>Snapshot members per chunk (ADR-010 §6: 250 for exports).</summary>
    public int DocumentsPerChunk { get; init; } = ChunkBounds.For(JobType.Export).MaxItems;

    /// <summary>Documents of one chunk whose files are copied in parallel.</summary>
    public int FileConcurrency { get; init; } = 4;

    /// <summary>The planning/finalization claim; renewed while the work runs, taken over by another worker once it expires.</summary>
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the export worker looks for exports to plan or finalize.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Directory for the temporary manifest files of the finalization; the system temp directory when null.</summary>
    public string? TempDirectory { get; init; }

    public string WorkerId { get; init; } = string.Create(
        CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
}
