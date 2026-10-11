using System.Globalization;

namespace Opportunity.Application.Backup;

/// <summary>Outcome of a snapshot as the OpenSearch repository reports it (<c>SUCCESS</c>, <c>PARTIAL</c>, …).</summary>
public enum SearchSnapshotState
{
    /// <summary>Anchors were written; the repository has not confirmed the snapshot (yet).</summary>
    Requested,

    InProgress,
    Success,
    Partial,
    Failed,
}

/// <summary>One snapshot in the OpenSearch repository (the repository is the authority on its outcome and times).</summary>
/// <param name="Indices">Physical indices the snapshot holds.</param>
public sealed record SearchSnapshotInfo(
    string Name,
    SearchSnapshotState State,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    IReadOnlyList<string> Indices,
    string? Failure = null)
{
    public bool IsUsable => State == SearchSnapshotState.Success && StartTime is not null && EndTime is not null;
}

/// <summary>The PostgreSQL record of a snapshot the snapshot job requested (<c>opportunity.search_snapshot</c>).</summary>
public sealed record SearchSnapshotRecord(
    Guid SnapshotId,
    string Name,
    string Repository,
    DateTimeOffset RequestedAt,
    SearchSnapshotState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int Workspaces);

/// <summary>
/// The catch-up anchor of one workspace (design §3): its visible watermark <c>G_s</c> and its placement, committed
/// before the snapshot started.
/// </summary>
/// <param name="PlacementState">1 Active, 2 Building, 3 Moving (a placement being rebuilt or moved is excluded from catch-up).</param>
public sealed record SearchSnapshotAnchor(
    Guid WorkspaceId,
    long IndexedThroughGeneration,
    long? SearchGeneration,
    short? PlacementKind,
    short? PlacementState,
    int? PlacementGeneration)
{
    /// <summary>The placement was being rebuilt or moved at capture, so the snapshot over-states it (design §3).</summary>
    public bool PlacementInTransition => PlacementState is 2 or 3;
}

/// <summary>Result of restoring a snapshot into the cluster.</summary>
/// <param name="Indices">Indices restored from the snapshot.</param>
/// <param name="Deleted">Existing indices of the installation's prefix deleted first.</param>
public sealed record SearchSnapshotRestoreResult(IReadOnlyList<string> Indices, IReadOnlyList<string> Deleted);

/// <summary>
/// The OpenSearch snapshot repository (ADR-016 §3.1): <c>repository-s3</c> against a dedicated bucket in Full, any
/// registered repository type in tests. Covers the installation's own <c>&lt;prefix&gt;-*</c> indices only, without the
/// global state. Implemented by <c>Opportunity.Search</c>; callers never see an index name except in reports.
/// </summary>
public interface ISearchSnapshotRepository
{
    /// <summary>The repository name snapshots are taken in.</summary>
    string RepositoryName { get; }

    /// <summary>Registers the repository when it is configured and missing (idempotent).</summary>
    Task EnsureRepositoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes a snapshot of the installation's indices and waits for its outcome.</summary>
    Task<SearchSnapshotInfo> CreateAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Every snapshot in the repository, oldest first.</summary>
    Task<IReadOnlyList<SearchSnapshotInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<SearchSnapshotInfo?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a snapshot from the repository (missing is fine).</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// R4: deletes the installation's current <c>&lt;prefix&gt;-*</c> indices, restores the snapshot's indices with their
    /// aliases (no global state) and waits until every restored index is readable (yellow).
    /// </summary>
    Task<SearchSnapshotRestoreResult> RestoreAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>PostgreSQL side of the snapshot job: the snapshot rows and their per-workspace anchors (V0070).</summary>
public interface ISearchSnapshotStore
{
    /// <summary>
    /// Step 1 of design §3: records the snapshot and, per workspace that is not purged, its visible watermark and
    /// placement. Every anchor is committed when this returns, before the snapshot starts.
    /// </summary>
    Task<SearchSnapshotRecord> BeginAsync(string name, string repository, CancellationToken cancellationToken = default);

    /// <summary>Step 3: records the repository's outcome.</summary>
    Task CompleteAsync(Guid snapshotId, SearchSnapshotInfo outcome, CancellationToken cancellationToken = default);

    Task<SearchSnapshotRecord?> FindAsync(string repository, string name, CancellationToken cancellationToken = default);

    /// <summary>The newest snapshot records first.</summary>
    Task<IReadOnlyList<SearchSnapshotRecord>> ListAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>The anchors of a snapshot, by workspace.</summary>
    Task<IReadOnlyDictionary<Guid, SearchSnapshotAnchor>> GetAnchorsAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Forgets an expired snapshot: its row and (cascade) its anchors.</summary>
    Task<bool> DeleteAsync(Guid snapshotId, CancellationToken cancellationToken = default);
}

/// <summary>Settings of the snapshot job (<c>Backup:SearchSnapshots</c>, ADR-016 §3.2).</summary>
public sealed class SearchSnapshotOptions
{
    public const string SectionName = "Backup:SearchSnapshots";

    /// <summary>Run the hourly snapshot job in the dispatcher host. Off by default; the Full profile turns it on.</summary>
    public bool Enabled { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Repository name in OpenSearch.</summary>
    public string Repository { get; set; } = "opportunity-snapshots";

    /// <summary>
    /// Repository type to register when the repository is missing (<c>s3</c> in Full, <c>fs</c> in tests); empty means
    /// the repository is registered by the operator and must exist.
    /// </summary>
    public string? RepositoryType { get; set; }

    /// <summary>Repository settings for registration, e.g. <c>bucket</c>, <c>base_path</c>, <c>client</c> or <c>location</c>.</summary>
    public Dictionary<string, string> RepositorySettings { get; } = new(StringComparer.Ordinal);

    /// <summary>Snapshot names are <c>&lt;prefix&gt;-yyyymmdd-hhmmss</c> (UTC).</summary>
    public string NamePrefix { get; set; } = "opportunity";

    /// <summary>The newest snapshots kept regardless of age (48 hourly).</summary>
    public int KeepNewest { get; set; } = 48;

    /// <summary>One snapshot per UTC day is kept up to this age (= <c>WorkspaceDeletion:BackupRetention</c>, 35 days).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(35);

    /// <summary>Upper bound for one snapshot to complete.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(1);

    public void Validate()
    {
        if (!SearchSnapshotNames.IsValidRepository(Repository))
        {
            throw new InvalidOperationException($"{SectionName}:Repository must be a lowercase name ([a-z0-9._-]).");
        }

        if (!SearchSnapshotNames.IsValidRepository(NamePrefix))
        {
            throw new InvalidOperationException($"{SectionName}:NamePrefix must be a lowercase name ([a-z0-9._-]).");
        }

        if (Interval < TimeSpan.FromMinutes(1) || KeepNewest < 1 || Retention < TimeSpan.Zero || Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}: Interval must be at least 1 minute, KeepNewest at least 1, Retention and Timeout positive.");
        }
    }
}

public static class SearchSnapshotNames
{
    public static string For(string prefix, DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}-{at.UtcDateTime:yyyyMMdd-HHmmss}");

    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 200 && IsLowerName(name);

    public static bool IsValidRepository(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 100 && IsLowerName(name);

    private static bool IsLowerName(string name) =>
        (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
}
