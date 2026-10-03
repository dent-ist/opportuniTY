using Opportunity.Application.Search.Indexing;

namespace Opportunity.Search.Indexing;

/// <summary>The placement thresholds of ADR-006 §2 as pure functions of the configured options.</summary>
public static class PlacementPolicy
{
    /// <summary>Growth headroom for multi-shard dedicated indexes.</summary>
    public const double ShardGrowthHeadroom = 1.5;

    public static PlacementDecision Decide(OpenSearchOptions options, WorkspacePlacementRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        var p = options.Placement;
        var bytes = CalibratedBytes(p, request.ExpectedIndexedBytes);
        var dedicated = request.DedicatedIndex || request.ExpectedDocuments >= p.DedicatedDocuments || bytes >= p.DedicatedBytes;
        return dedicated
            ? new PlacementDecision(IndexPlacementKind.Dedicated, DedicatedPrimaryShards(p, bytes), bytes)
            : new PlacementDecision(IndexPlacementKind.Shared, options.SharedPrimaryShards, bytes);
    }

    /// <summary>R5: PostgreSQL estimate times the calibration factor.</summary>
    public static long CalibratedBytes(IndexPlacementOptions placement, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return estimatedBytes <= 0 ? 0 : (long)Math.Min(long.MaxValue, Math.Ceiling(estimatedBytes * placement.SizeCalibrationFactor));
    }

    /// <summary>1 below <c>MultiShardBytes</c>, else <c>max(2, ceil(1.5 x bytes / TargetShardBytes))</c>.</summary>
    public static int DedicatedPrimaryShards(IndexPlacementOptions placement, long projectedPrimaryBytes)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (projectedPrimaryBytes < placement.MultiShardBytes)
        {
            return 1;
        }

        var shards = Math.Ceiling(ShardGrowthHeadroom * projectedPrimaryBytes / placement.TargetShardBytes);
        return (int)Math.Clamp(shards, 2, 1024);
    }

    /// <summary>Assigned bytes at which a shared index closes for new workspaces.</summary>
    public static long SharedIndexCloseAtBytes(OpenSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (long)(options.SharedPrimaryShards * (double)options.Placement.TargetShardBytes * options.Placement.SharedIndexCloseAtFraction);
    }
}

public sealed record PlacementDecision(IndexPlacementKind Kind, int PrimaryShards, long EstimatedBytes);
