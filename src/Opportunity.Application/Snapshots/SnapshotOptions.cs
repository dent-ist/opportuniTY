using System.Globalization;

namespace Opportunity.Application.Snapshots;

/// <summary>Snapshot settings (section <c>Snapshots</c>, ADR-002 §2 and §9 defaults).</summary>
public sealed class SnapshotOptions
{
    public const string SectionName = "Snapshots";

    /// <summary>Selections up to this size are frozen inside the request (201); larger ones answer 202 and materialize in the background.</summary>
    public int SynchronousMaxDocuments { get; set; } = 50_000;

    /// <summary>Largest selection accepted at all; larger targets are rejected with "narrow the selection" (ADR-002 §2 MaxEstimate).</summary>
    public long MaxDocuments { get; set; } = 10_000_000;

    /// <summary>IDs per search page during selection.</summary>
    public int SelectionPageSize { get; set; } = 5_000;

    /// <summary>Candidate IDs per PDP call inside the freeze.</summary>
    public int AuthorizationBatchSize { get; set; } = 5_000;

    /// <summary>Materialization claim; renewed never, taken over by another process once it expires.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>ADR-002 §5.3: a lost reader restarts the selection once; the second loss fails the snapshot.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>ADR-002 §9: a snapshot no job references expires this long after creation (unconfirmed lifetime).</summary>
    public TimeSpan UnreferencedLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>ADR-002 §9: a bulk-coding snapshot expires this long after its last job finished.</summary>
    public TimeSpan JobRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Run the background materializer and retention loop in this process.</summary>
    public bool BackgroundEnabled { get; set; } = true;

    /// <summary>How often the background materializer looks for unclaimed snapshots.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often retention runs.</summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Identifies this process in materialization claims.</summary>
    public string InstanceId { get; set; } = string.Create(
        CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");

    public void Validate()
    {
        Require(SynchronousMaxDocuments >= 0, nameof(SynchronousMaxDocuments));
        Require(MaxDocuments >= 1 && MaxDocuments >= SynchronousMaxDocuments, nameof(MaxDocuments));
        Require(SelectionPageSize is >= 1 and <= 10_000, nameof(SelectionPageSize));
        Require(AuthorizationBatchSize is >= 1 and <= 50_000, nameof(AuthorizationBatchSize));
        Require(ClaimLease >= TimeSpan.FromSeconds(10), nameof(ClaimLease));
        Require(MaxAttempts is >= 1 and <= 10, nameof(MaxAttempts));
        Require(UnreferencedLifetime >= TimeSpan.FromMinutes(1), nameof(UnreferencedLifetime));
        Require(JobRetention >= TimeSpan.Zero, nameof(JobRetention));
        Require(PollInterval > TimeSpan.Zero && RetentionInterval > TimeSpan.Zero, nameof(PollInterval));
        Require(!string.IsNullOrWhiteSpace(InstanceId) && InstanceId.Length <= 200, nameof(InstanceId));
    }

    private static void Require(bool condition, string setting)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{SectionName}:{setting} is out of range.");
        }
    }
}
