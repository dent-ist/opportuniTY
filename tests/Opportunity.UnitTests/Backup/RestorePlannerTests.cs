using AwesomeAssertions;

using Opportunity.Application.Backup;

namespace Opportunity.UnitTests.Backup;

/// <summary>
/// E19-T08 / ADR-016 §1.2, design §4.1: R1 picks the search path and refuses any combination that would leave OpenSearch
/// ahead of PostgreSQL, a target without a backup, or a target after the object replica's or the key backup's coverage.
/// </summary>
public sealed class RestorePlannerTests
{
    private static readonly DateTimeOffset Incident = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly PgArchiveInfo Archive = new("opportunity", "ok",
    [
        new PgBackupSet("20261004-010000F", "full", Incident.AddDays(-7).AddHours(-11), Incident.AddDays(-7).AddHours(-10), false),
        new PgBackupSet("20261004-010000F_20261011-010000D", "diff", Incident.AddHours(-11), Incident.AddHours(-10).AddMinutes(-50), false),
    ], "000000010000000000000001", "0000000100000000000000FF");

    private static SearchSnapshotInfo Snapshot(string name, DateTimeOffset start, TimeSpan duration, SearchSnapshotState state = SearchSnapshotState.Success) =>
        new(name, state, start, start + duration, ["opp-shared-001-g2"]);

    [Fact]
    public void A_target_picks_the_newest_snapshot_that_ends_at_least_epsilon_before_it()
    {
        var target = Incident.AddMinutes(-30);
        var snapshots = new[]
        {
            Snapshot("opportunity-a", Incident.AddHours(-2), TimeSpan.FromMinutes(5)),
            Snapshot("opportunity-b", Incident.AddHours(-1), TimeSpan.FromMinutes(5)),
            // Ends 30 s before the target: OpenSearch could hold writes PostgreSQL loses.
            Snapshot("opportunity-c", target.AddMinutes(-1), TimeSpan.FromSeconds(30)),
            Snapshot("opportunity-d", Incident.AddMinutes(-50), TimeSpan.FromMinutes(5), SearchSnapshotState.Partial),
        };

        var plan = RestorePlanner.Plan(new RestorePlanRequest(target, Incident, Archive, snapshots));

        plan.Accepted.Should().BeTrue(string.Join("; ", plan.Problems));
        plan.Path.Should().Be(RestorePath.Snapshot);
        plan.Snapshot!.Name.Should().Be("opportunity-b");
        plan.Backup!.Label.Should().Be("20261004-010000F_20261011-010000D");
        plan.LossWindow.Should().Be(TimeSpan.FromMinutes(30));
        plan.ReplayWindow.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void A_named_snapshot_that_ends_too_late_is_refused()
    {
        var target = Incident.AddMinutes(-30);
        var plan = RestorePlanner.Plan(new RestorePlanRequest(target, Incident, Archive,
            [Snapshot("opportunity-late", target.AddMinutes(-2), TimeSpan.FromMinutes(1.5))], Snapshot: "opportunity-late"));

        plan.Accepted.Should().BeFalse();
        plan.Problems.Should().ContainSingle().Which.Should().Contain("ahead of PostgreSQL");
    }

    [Fact]
    public void A_snapshot_older_than_the_replay_bound_is_not_used_and_the_path_is_a_rebuild()
    {
        var target = Incident.AddMinutes(-10);
        var plan = RestorePlanner.Plan(new RestorePlanRequest(target, Incident, Archive, [Snapshot("opportunity-old", target.AddHours(-25), TimeSpan.FromMinutes(5))]));

        plan.Accepted.Should().BeTrue();
        plan.Path.Should().Be(RestorePath.Rebuild);
        plan.Snapshot.Should().BeNull();
        RestorePlanner.Disqualify(Snapshot("opportunity-old", target.AddHours(-25), TimeSpan.FromMinutes(5)), target).Should().Contain("24 h");
    }

    [Fact]
    public void End_of_wal_keeps_the_live_cluster_but_an_earlier_target_must_not()
    {
        var endOfWal = RestorePlanner.Plan(new RestorePlanRequest(null, Incident, Archive, [], KeepLiveSearch: true));
        endOfWal.Accepted.Should().BeTrue();
        endOfWal.Path.Should().Be(RestorePath.EndOfWal);
        endOfWal.LossWindow.Should().Be(TimeSpan.Zero);
        endOfWal.Backup!.Label.Should().Be("20261004-010000F_20261011-010000D", "the newest backup replays to the end of WAL");

        var earlier = RestorePlanner.Plan(new RestorePlanRequest(Incident.AddMinutes(-5), Incident, Archive, [], KeepLiveSearch: true));
        earlier.Accepted.Should().BeFalse();
        earlier.Path.Should().Be(RestorePath.KeepLive);
    }

    [Fact]
    public void Targets_outside_the_archive_the_object_replica_or_the_key_backup_are_refused()
    {
        RestorePlanner.Plan(new RestorePlanRequest(Incident.AddDays(-8), Incident, Archive, [])).Problems
            .Should().ContainSingle().Which.Should().Contain("earliest recoverable time");

        var replica = RestorePlanner.Plan(new RestorePlanRequest(Incident.AddMinutes(-5), Incident, Archive, [], ObjectSyncPoint: Incident.AddMinutes(-8)));
        replica.Accepted.Should().BeFalse();
        replica.Problems.Should().ContainSingle().Which.Should().Contain("object replica");

        var keys = RestorePlanner.Plan(new RestorePlanRequest(Incident.AddMinutes(-5), Incident, Archive, [], KeyBackupCoverage: Incident.AddHours(-1)));
        keys.Accepted.Should().BeFalse();
        keys.Problems.Should().ContainSingle().Which.Should().Contain("key");
    }

    [Fact]
    public void Pgbackrest_info_json_is_parsed_per_stanza()
    {
        const string json = """
            [{"archive":[{"database":{"id":1,"repo-key":1},"id":"17-1","max":"000000010000000000000009","min":"000000010000000000000002"}],
              "backup":[
                {"archive":{"start":"000000010000000000000002","stop":"000000010000000000000002"},"error":false,"label":"20261011-090000F",
                 "timestamp":{"start":1760173200,"stop":1760173260},"type":"full"},
                {"archive":{"start":"000000010000000000000005","stop":"000000010000000000000005"},"error":true,"label":"20261011-090000F_20261011-100000I",
                 "timestamp":{"start":1760176800,"stop":1760176830},"type":"incr"}],
              "cipher":"aes-256-cbc","name":"opportunity","status":{"code":0,"message":"ok"}},
             {"name":"other","backup":[],"status":{"code":0,"message":"ok"}}]
            """;

        var info = PgBackRestInfo.Parse(json, "opportunity");

        info.Status.Should().Be("ok");
        info.ArchiveMax.Should().Be("000000010000000000000009");
        info.Backups.Should().HaveCount(2);
        info.EarliestRecoverable.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1760173260));
        info.BackupFor(null)!.Label.Should().Be("20261011-090000F", "a backup that ended in error is never used");
        FluentActions.Invoking(() => PgBackRestInfo.Parse(json, "missing")).Should().Throw<FormatException>();
        FluentActions.Invoking(() => PgBackRestInfo.Parse("{}", "opportunity")).Should().Throw<FormatException>();
    }

    [Fact]
    public void Retention_keeps_the_newest_snapshots_and_one_per_day_and_never_the_last_usable_one()
    {
        var now = Incident;
        var options = new SearchSnapshotOptions { KeepNewest = 3, Retention = TimeSpan.FromDays(2) };
        var snapshots = Enumerable.Range(0, 80)
            .Select(h => Snapshot($"opportunity-{h:D3}", now.AddHours(-h), TimeSpan.FromMinutes(2)))
            .Append(Snapshot("opportunity-failed", now.AddMinutes(-30), TimeSpan.FromMinutes(1), SearchSnapshotState.Failed))
            .Append(Snapshot("manual-keep", now.AddDays(-30), TimeSpan.FromMinutes(1)))
            .ToList();

        var expired = SearchSnapshotService.SelectExpired(snapshots, options, now);
        var kept = snapshots.Select(s => s.Name).Except(expired).ToList();

        kept.Should().Contain(["opportunity-000", "opportunity-001", "opportunity-002"]);
        kept.Should().Contain("manual-keep", "snapshots without the job's prefix are never touched");
        expired.Should().Contain("opportunity-failed").And.Contain("opportunity-079");
        // Within retention: one per UTC day (the oldest of the day).
        kept.Count(k => k.StartsWith("opportunity-0", StringComparison.Ordinal)).Should().BeInRange(4, 6);
        SearchSnapshotService.SelectExpired([Snapshot("opportunity-only", now.AddDays(-90), TimeSpan.FromMinutes(1))], options, now)
            .Should().BeEmpty("the newest usable snapshot is always kept");
    }
}
