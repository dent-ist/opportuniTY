namespace Opportunity.Application.Workspaces.Deletion;

/// <summary>
/// Configuration section <c>WorkspaceDeletion</c> (E20-T02, ADR-014 §3-§4). The API reads the request side (waiting
/// period, expiry); the deletion coordinator in the indexing worker reads the run side.
/// </summary>
public sealed class WorkspaceDeletionOptions
{
    public const string SectionName = "WorkspaceDeletion";

    /// <summary>Longest waiting period ADR-014 §3.3 allows.</summary>
    public static readonly TimeSpan MaxWaitingPeriod = TimeSpan.FromDays(90);

    /// <summary>
    /// Time between approval and the earliest start of the run: 7 days by default, at most 90. ADR-014 allows less than
    /// one day only in the Lite profile; set <see cref="AllowShortWaitingPeriod"/> to accept it.
    /// </summary>
    public TimeSpan WaitingPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Accepts a waiting period under one day (Lite profile, demonstrations, tests).</summary>
    public bool AllowShortWaitingPeriod { get; set; }

    /// <summary>An unapproved request expires after this long (ADR-014 §3.3: 30 days).</summary>
    public TimeSpan RequestExpiry { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The coordinator's lease on a run; renewed while it works.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The drain waits at least this long after the fence, for API requests and workers that read the workspace's state
    /// just before it (security-state and placement caches), even when no lease is visible.
    /// </summary>
    public TimeSpan DrainSettleDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Wait between the first search purge and the second pass of the verification (ADR-014 §4 step 4: two lease TTLs
    /// plus <c>index.gc_deletes</c>), so a write that passed a fence check just before the fence is removed too.
    /// </summary>
    public TimeSpan ResurrectionGuardDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Rows per purge batch (ADR-014 §4 step 5: 10,000 initially).</summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>Re-runs of a step whose verification found data left, before the run ends with residuals.</summary>
    public int MaxVerificationAttempts { get; set; } = 3;

    /// <summary>
    /// How long database backups and PITR archives are kept (ADR-016): copies taken before the deletion may hold the
    /// workspace's data until this long after it finishes. The certificate states the date.
    /// </summary>
    public TimeSpan BackupRetention { get; set; } = TimeSpan.FromDays(35);

    public void Validate()
    {
        if (WaitingPeriod < TimeSpan.Zero || WaitingPeriod > MaxWaitingPeriod)
        {
            throw new InvalidOperationException($"{SectionName}:WaitingPeriod must be between 0 and 90 days.");
        }

        if (WaitingPeriod < TimeSpan.FromDays(1) && !AllowShortWaitingPeriod)
        {
            throw new InvalidOperationException(
                $"{SectionName}:WaitingPeriod under one day needs {SectionName}:AllowShortWaitingPeriod (Lite profile only, ADR-014 §3.3).");
        }

        if (RequestExpiry <= TimeSpan.Zero || PollInterval <= TimeSpan.Zero || LeaseDuration < TimeSpan.FromSeconds(5)
            || DrainSettleDelay < TimeSpan.Zero || ResurrectionGuardDelay < TimeSpan.Zero || BackupRetention < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}: durations must be positive (lease at least 5 seconds).");
        }

        if (BatchSize is < 1 or > 100_000 || MaxVerificationAttempts is < 1 or > 10)
        {
            throw new InvalidOperationException($"{SectionName}: BatchSize is 1 to 100,000 and MaxVerificationAttempts 1 to 10.");
        }
    }
}
