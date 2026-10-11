using System.Globalization;
using System.Text.Json;

namespace Opportunity.Application.Backup;

/// <summary>How OpenSearch is brought back (design §4.1).</summary>
public enum RestorePath
{
    /// <summary>Path 0: PostgreSQL recovered to the end of its WAL; the live cluster is kept and only redriven.</summary>
    EndOfWal,

    /// <summary>Path A: a qualifying snapshot is restored and the work above its anchor is replayed.</summary>
    Snapshot,

    /// <summary>Path B: keep a live cluster after an earlier recovery target behind an exhaustive ahead-scan (not automated).</summary>
    KeepLive,

    /// <summary>Path C: empty cluster, every workspace reindexed from PostgreSQL.</summary>
    Rebuild,
}

/// <summary>One pgBackRest backup set (<c>pgbackrest info --output=json</c>).</summary>
/// <param name="Stop">The backup's consistency point: the earliest time a restore from it can reach.</param>
public sealed record PgBackupSet(string Label, string Type, DateTimeOffset Start, DateTimeOffset Stop, bool Error);

/// <summary>What the PostgreSQL archive can recover (one stanza of <c>pgbackrest info</c>).</summary>
public sealed record PgArchiveInfo(string Stanza, string Status, IReadOnlyList<PgBackupSet> Backups, string? ArchiveMin, string? ArchiveMax)
{
    /// <summary>The earliest recoverable time: the stop of the oldest good backup.</summary>
    public DateTimeOffset? EarliestRecoverable => Backups.Where(b => !b.Error).Select(b => (DateTimeOffset?)b.Stop).Min();

    /// <summary>The backup a restore to <paramref name="target"/> starts from (newest good backup that stopped at or before it).</summary>
    public PgBackupSet? BackupFor(DateTimeOffset? target) =>
        Backups.Where(b => !b.Error && (target is null || b.Stop <= target)).MaxBy(b => b.Stop);
}

public static class PgBackRestInfo
{
    /// <summary>Parses the JSON of <c>pgbackrest info --output=json</c> for one stanza.</summary>
    /// <exception cref="FormatException">The JSON is not pgBackRest info output, or the stanza is missing.</exception>
    public static PgArchiveInfo Parse(string json, string stanza)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(stanza);
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.GetProperty("name").GetString() != stanza)
                {
                    continue;
                }

                var backups = new List<PgBackupSet>();
                if (entry.TryGetProperty("backup", out var list))
                {
                    foreach (var b in list.EnumerateArray())
                    {
                        var ts = b.GetProperty("timestamp");
                        backups.Add(new PgBackupSet(
                            b.GetProperty("label").GetString()!,
                            b.GetProperty("type").GetString()!,
                            DateTimeOffset.FromUnixTimeSeconds(ts.GetProperty("start").GetInt64()),
                            DateTimeOffset.FromUnixTimeSeconds(ts.GetProperty("stop").GetInt64()),
                            b.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True));
                    }
                }

                string? min = null, max = null;
                if (entry.TryGetProperty("archive", out var archives))
                {
                    foreach (var a in archives.EnumerateArray())
                    {
                        min ??= a.TryGetProperty("min", out var m) ? m.GetString() : null;
                        max = a.TryGetProperty("max", out var x) ? x.GetString() : max;
                    }
                }

                var status = entry.TryGetProperty("status", out var s) && s.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "unknown"
                    : "unknown";
                return new PgArchiveInfo(stanza, status, backups, min, max);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("Not the JSON output of `pgbackrest info --output=json`: " + ex.Message, ex);
        }

        throw new FormatException($"Stanza '{stanza}' is not in the pgBackRest info output.");
    }
}

/// <param name="TargetTime">The PostgreSQL recovery target <c>T_r</c>; null recovers to the end of the archived WAL.</param>
/// <param name="DecisionTime">When the restore was decided (the incident); the loss window ends here.</param>
/// <param name="Archive">The PostgreSQL archive (<c>pgbackrest info</c>).</param>
/// <param name="Snapshots">The OpenSearch repository's snapshots.</param>
/// <param name="KeepLiveSearch">The live OpenSearch cluster survived and the operator wants to keep it (path 0 only).</param>
/// <param name="Snapshot">A snapshot the operator asks for; null lets the planner choose.</param>
/// <param name="ObjectSyncPoint">After site loss: the object replica's sync point (rule 1.2); null when the live store is used.</param>
/// <param name="KeyBackupCoverage">The newest key backup's time when the key directory is restored from backup; null when the keys survived.</param>
public sealed record RestorePlanRequest(
    DateTimeOffset? TargetTime,
    DateTimeOffset DecisionTime,
    PgArchiveInfo Archive,
    IReadOnlyList<SearchSnapshotInfo> Snapshots,
    bool KeepLiveSearch = false,
    string? Snapshot = null,
    DateTimeOffset? ObjectSyncPoint = null,
    DateTimeOffset? KeyBackupCoverage = null);

/// <param name="Accepted">False when any of <see cref="Problems"/> makes the combination unsafe (ADR-016 §1.2).</param>
/// <param name="EffectiveTarget">The recovery target used for the checks (the decision time for an end-of-WAL restore).</param>
/// <param name="Backup">The pgBackRest backup set the restore starts from.</param>
/// <param name="LossWindow">Committed work that is lost: decision time minus target (zero for an end-of-WAL restore).</param>
/// <param name="ReplayWindow">Path A: work between the snapshot's start and the target that catch-up replays.</param>
public sealed record RestorePlan(
    bool Accepted,
    RestorePath Path,
    DateTimeOffset? TargetTime,
    DateTimeOffset EffectiveTarget,
    PgBackupSet? Backup,
    SearchSnapshotInfo? Snapshot,
    TimeSpan LossWindow,
    TimeSpan? ReplayWindow,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes);

/// <summary>
/// R1 of the restore procedure (ADR-016 §1.2 and §8, design §4.1): checks a recovery target against the PostgreSQL
/// archive, the OpenSearch snapshots, the object store and the key backups, and picks the search path. It refuses a
/// snapshot that ends later than <c>T_r − ε</c> (OpenSearch would be ahead of PostgreSQL, which external versioning
/// cannot repair) and one older than the 24 h replay bound, and it refuses to keep a live cluster for an earlier
/// <c>T_r</c>.
/// </summary>
public static class RestorePlanner
{
    /// <summary>Clock-skew allowance between OpenSearch and PostgreSQL hosts (NTP required).</summary>
    public static readonly TimeSpan Epsilon = TimeSpan.FromSeconds(60);

    /// <summary>A snapshot older than this cannot be replayed (search work partitions are kept 3 days, design §4.3).</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromHours(24);

    public static RestorePlan Plan(RestorePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        var notes = new List<string>();
        var endOfWal = request.TargetTime is null;
        var target = request.TargetTime ?? request.DecisionTime;

        var backup = request.Archive.BackupFor(request.TargetTime);
        if (backup is null)
        {
            problems.Add(request.Archive.EarliestRecoverable is { } earliest
                ? $"No backup completed at or before the target {Time(target)}; the earliest recoverable time is {Time(earliest)}."
                : "The archive holds no usable backup.");
        }

        if (request.TargetTime is { } t && t > request.DecisionTime)
        {
            problems.Add($"The target {Time(t)} is later than the decision time {Time(request.DecisionTime)}.");
        }

        if (request.ObjectSyncPoint is { } sync && target > sync)
        {
            problems.Add(
                $"The object replica is synchronized up to {Time(sync)}: a target after it would reference objects the store does not hold (ADR-016 §1.2). Choose a target at or before it.");
        }

        if (request.KeyBackupCoverage is { } keys && target > keys)
        {
            problems.Add(
                $"The restored key directory covers key events up to {Time(keys)}: choose a target at or before it, or restore a newer key backup first (ADR-016 §5.1).");
        }

        var path = RestorePath.Snapshot;
        SearchSnapshotInfo? snapshot = null;
        if (request.KeepLiveSearch)
        {
            if (endOfWal)
            {
                path = RestorePath.EndOfWal;
                notes.Add("PostgreSQL is recovered to the end of the archived WAL; the live OpenSearch cluster is kept and in-flight work is redriven.");
            }
            else
            {
                path = RestorePath.KeepLive;
                problems.Add(
                    "Keeping the live OpenSearch cluster after a restore to an earlier time leaves it ahead of PostgreSQL (ADR-016 §1.3). Restore a snapshot (path A) or rebuild (path C); path B (exhaustive ahead purge) is not automated.");
            }
        }
        else
        {
            snapshot = ChooseSnapshot(request, target, problems, notes);
            path = snapshot is null ? RestorePath.Rebuild : RestorePath.Snapshot;
            if (snapshot is null)
            {
                notes.Add("No snapshot qualifies: OpenSearch starts empty and every workspace is reindexed from PostgreSQL after reopening (path C).");
            }
        }

        if (endOfWal)
        {
            notes.Add("Recovery runs to the end of the archived WAL: with archive_timeout = 60 s at most the last unarchived segment is lost.");
        }

        if (backup is not null)
        {
            notes.Add($"pgBackRest restores backup {backup.Label} ({backup.Type}, stopped {Time(backup.Stop)}) and replays WAL from there.");
        }

        var loss = endOfWal ? TimeSpan.Zero : request.DecisionTime - target;
        TimeSpan? replay = snapshot?.StartTime is { } start ? target - start : null;
        return new RestorePlan(problems.Count == 0, path, request.TargetTime, target, backup, snapshot, loss < TimeSpan.Zero ? TimeSpan.Zero : loss,
            replay, problems, notes);
    }

    /// <summary>The snapshot rule (design §7.1.1): <c>end_time + ε ≤ T_r</c> and <c>T_r − start_time ≤ 24 h</c>.</summary>
    public static string? Disqualify(SearchSnapshotInfo snapshot, DateTimeOffset target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsUsable)
        {
            return $"snapshot {snapshot.Name} is {snapshot.State}, not SUCCESS";
        }

        if (snapshot.EndTime!.Value + Epsilon > target)
        {
            return $"snapshot {snapshot.Name} ended at {Time(snapshot.EndTime.Value)}, less than {Epsilon.TotalSeconds:F0} s before the target {Time(target)}: OpenSearch would be ahead of PostgreSQL";
        }

        if (target - snapshot.StartTime!.Value > MaxSnapshotAge)
        {
            return $"snapshot {snapshot.Name} started {(target - snapshot.StartTime.Value).TotalHours:F1} h before the target, more than the {MaxSnapshotAge.TotalHours:F0} h replay bound";
        }

        return null;
    }

    private static SearchSnapshotInfo? ChooseSnapshot(RestorePlanRequest request, DateTimeOffset target, List<string> problems, List<string> notes)
    {
        if (request.Snapshot is { } wanted)
        {
            var named = request.Snapshots.FirstOrDefault(s => s.Name == wanted);
            if (named is null)
            {
                problems.Add($"Snapshot {wanted} is not in the repository.");
                return null;
            }

            if (Disqualify(named, target) is { } reason)
            {
                problems.Add("Refused: " + reason + ".");
                return null;
            }

            return named;
        }

        var candidates = request.Snapshots.Where(s => Disqualify(s, target) is null).ToList();
        var chosen = candidates.MaxBy(s => s.EndTime);
        if (chosen is not null)
        {
            notes.Add($"Snapshot {chosen.Name} (ended {Time(chosen.EndTime!.Value)}) is the newest that ends at least {Epsilon.TotalSeconds:F0} s before the target.");
        }

        return chosen;
    }

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
