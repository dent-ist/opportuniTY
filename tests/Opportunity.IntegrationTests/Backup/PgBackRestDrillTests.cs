using System.Diagnostics;
using System.Globalization;
using System.Text;

using AwesomeAssertions;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

using Opportunity.Testing.Images;

namespace Opportunity.IntegrationTests.Backup;

/// <summary>
/// E19-T08 drill (opt-in: <c>OPPORTUNITY_BACKUP_DRILL=1</c>): PostgreSQL point-in-time recovery with pgBackRest, using the
/// Full profile's <c>pgbackrest.conf</c> (asynchronous archiving, aes-256-cbc, bundles, time retention) with a local
/// repository instead of S3, and the Full profile's <c>restore-to-time.sh</c>. It takes a full backup, commits work before
/// and after a recovery target, measures how long a committed row waits for the archive with <c>archive_timeout = 60s</c>
/// (the RPO), then restores (1) to the target — rows after it are gone, rows before it are there — and (2) after losing
/// the data directory, to the end of the archive — nothing committed before the last archived segment is lost. Restore
/// and replay times are printed. The image bundles a published pgBackRest binary because package mirrors may be
/// unreachable (see Drill/pgbackrest-drill.Dockerfile); <c>OPPORTUNITY_TEST_IMAGE_PGBACKREST</c> overrides its source.
/// Scale with <c>OPPORTUNITY_BACKUP_DRILL_ROWS</c> (default 20,000 rows of ~1 KB).
/// </summary>
public sealed class PgBackRestDrillTests
{
    private const string Data = "/var/lib/postgresql/data";

    /// <summary>pgBackRest 2.59.3 (MIT) as published on Docker Hub (Ubuntu 24.04 build), pinned by digest; test-only.</summary>
    private const string PgBackRestImage = "woblerr/pgbackrest:2.59.3@sha256:aa9fdfb4fbfd20ab564fb28a3244da1fb25132e2f083f8a47717db74f2ee06a9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pitr_restores_to_a_timestamp_and_after_data_loss_to_the_end_of_the_archive_within_the_rpo()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OPPORTUNITY_BACKUP_DRILL") == "1",
            "Set OPPORTUNITY_BACKUP_DRILL=1 to run the PostgreSQL PITR drill (builds a pgBackRest image, about 3 minutes).");
        var rows = int.TryParse(Environment.GetEnvironmentVariable("OPPORTUNITY_BACKUP_DRILL_ROWS"), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : 20_000;
        var root = Path.GetDirectoryName(VersionsFile.Locate())!;
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(root, "tests", "Opportunity.IntegrationTests", "Backup", "Drill"))
            .WithDockerfile("pgbackrest-drill.Dockerfile")
            .WithBuildArgument("POSTGRES_IMAGE", ContainerImages.Postgres)
            .WithBuildArgument("PGBACKREST_IMAGE", Environment.GetEnvironmentVariable(ContainerImages.OverridePrefix + "PGBACKREST") is { Length: > 0 } overridden
                ? overridden
                : PgBackRestImage)
            .WithName("opportunity-local/pgbackrest-drill:" + Guid.NewGuid().ToString("N")[..8])
            .WithCleanUp(true)
            .Build();
        await image.CreateAsync(Ct);
        await using var container = new ContainerBuilder(image)
            .WithEntrypoint("sleep", "infinity")
            // Read by pgBackRest like repo1-cipher-pass; a fixed, obviously fake drill value.
            .WithEnvironment("PGBACKREST_REPO1_CIPHER_PASS", "drill-only-not-a-real-passphrase")
            .WithResourceMapping(Encoding.UTF8.GetBytes(DrillConfiguration(root)), "/etc/pgbackrest/pgbackrest.conf")
            .WithResourceMapping(new FileInfo(Path.Combine(root, "deploy", "docker-compose", "full", "pgbackrest", "restore-to-time.sh")),
                "/etc/opportunity/pgbackrest/")
            .Build();
        await container.StartAsync(Ct);
        try
        {
            await DrillAsync(container, rows);
        }
        finally
        {
            await container.DisposeAsync();
            await image.DeleteAsync(CancellationToken.None);
        }
    }

    private static async Task DrillAsync(IContainer c, int rows)
    {
        // initdb with the Full profile's archive settings.
        await RunAsync(c, $"chown -R postgres:postgres /etc/pgbackrest /var/lib/pgbackrest && gosu postgres initdb -D {Data} --data-checksums >/dev/null");
        await RunAsync(c,
            $"""
            cat >> {Data}/postgresql.conf <<'EOF'
            wal_level = replica
            archive_mode = on
            archive_command = 'pgbackrest --stanza=opportunity archive-push %p'
            archive_timeout = 60s
            listen_addresses = ''
            EOF
            """);
        await StartAsync(c);
        await RunAsync(c, "gosu postgres pgbackrest --stanza=opportunity stanza-create && gosu postgres pgbackrest --stanza=opportunity check");
        await SqlAsync(c, "CREATE TABLE drill (id bigint PRIMARY KEY, batch text NOT NULL, payload text NOT NULL, committed_at timestamptz NOT NULL DEFAULT clock_timestamp())");
        await InsertAsync(c, "base", 1, rows);
        var backup = Stopwatch.StartNew();
        await RunAsync(c, "gosu postgres pgbackrest --stanza=opportunity --type=full backup");
        Report($"drill: full backup of {rows} rows in {backup.Elapsed.TotalSeconds:F1} s; {await SqlAsync(c, "SELECT pg_size_pretty(pg_database_size('postgres'))")}");

        // Work before the target, the target itself, then work after it.
        await InsertAsync(c, "before", rows + 1, 1_000);
        var target = await SqlAsync(c, "SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') || '+00'");
        await Task.Delay(1_100, Ct);
        await InsertAsync(c, "after", rows + 1_001, 1_000);
        await SqlAsync(c, "SELECT pg_switch_wal()");
        await RunAsync(c, "gosu postgres pgbackrest --stanza=opportunity check");

        // RPO: a committed row with no further activity reaches the archive through archive_timeout alone.
        await InsertAsync(c, "tail", rows + 2_001, 10);
        var segment = await SqlAsync(c, "SELECT pg_walfile_name(pg_current_wal_lsn())");
        var waited = Stopwatch.StartNew();
        while (true)
        {
            // Same timeline: WAL file names order like their positions.
            var archived = await SqlAsync(c, "SELECT coalesce(last_archived_wal, '') FROM pg_stat_archiver");
            if (archived.Length == 24 && string.CompareOrdinal(archived, segment) >= 0)
            {
                break;
            }

            waited.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2), "archive_timeout = 60s must archive a quiet segment");
            await Task.Delay(1_000, Ct);
        }

        // Asynchronous archiving: wait until the spool has pushed it to the repository too.
        await RunAsync(c, "gosu postgres pgbackrest --stanza=opportunity check");
        Report($"drill: a committed row was archived {waited.Elapsed.TotalSeconds:F0} s after commit (archive_timeout 60 s; RPO target 5 min)");
        waited.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(5));

        // (2) Host loss with the archive intact: an empty data directory restored to the end of the archive.
        // It runs first: its promotion starts timeline 2, whose history still holds the target of (1).
        await RunAsync(c, $"gosu postgres pg_ctl -D {Data} -m immediate -w stop && find {Data} -mindepth 1 -delete");
        var restore = Stopwatch.StartNew();
        await RunAsync(c, "gosu postgres /bin/bash /etc/opportunity/pgbackrest/restore-to-time.sh latest --set \"$(gosu postgres pgbackrest --stanza=opportunity info --output=json | grep -o '\"label\":\"[^\"]*F\"' | head -1 | cut -d'\"' -f4)\"");
        await StartAsync(c);
        await WaitPromotedAsync(c);
        Report($"drill: restore of the lost data directory to the end of the archive in {restore.Elapsed.TotalSeconds:F1} s");
        (await SqlAsync(c, "SELECT count(*) FROM drill WHERE batch = 'tail'")).Should().Be("10", "everything committed before the last archived segment survives");

        // (1) Point in time: the target is restored exactly.
        await RunAsync(c, $"gosu postgres pg_ctl -D {Data} -m immediate -w stop");
        restore.Restart();
        var output = await RunAsync(c, $"gosu postgres /bin/bash /etc/opportunity/pgbackrest/restore-to-time.sh '{target}'");
        output.Should().Contain("--type=time").And.Contain("--target-action=promote").And.Contain("--delta");
        await StartAsync(c);
        await WaitPromotedAsync(c);
        Report($"drill: restore to {target} and replay in {restore.Elapsed.TotalSeconds:F1} s");
        (await SqlAsync(c, "SELECT count(*) FROM drill WHERE batch = 'before'")).Should().Be("1000");
        (await SqlAsync(c, "SELECT count(*) FROM drill WHERE batch IN ('after', 'tail')")).Should().Be("0", "work after the target is the loss window");
        (await SqlAsync(c, "SELECT count(*) FROM drill")).Should().Be((rows + 1_000).ToString(CultureInfo.InvariantCulture));
        (await SqlAsync(c, "SELECT timeline_id > 2 FROM pg_control_checkpoint()")).Should().Be("t", "each promotion starts a new timeline");

        // The repository verifies, and retention keeps the newest full backup.
        await RunAsync(c, "gosu postgres pgbackrest --stanza=opportunity verify");
    }

    /// <summary>The Full profile's configuration with a local repository (and pgBackRest's own processes through the wrapper).</summary>
    private static string DrillConfiguration(string root)
    {
        var full = File.ReadAllLines(Path.Combine(root, "deploy", "docker-compose", "full", "pgbackrest", "pgbackrest.conf"));
        var lines = new List<string>();
        foreach (var line in full)
        {
            if (line.StartsWith("repo1-s3", StringComparison.Ordinal) || line.StartsWith("repo1-storage", StringComparison.Ordinal)
                || line.StartsWith("repo1-type", StringComparison.Ordinal) || line.StartsWith("repo1-path", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(line);
            if (line == "[global]")
            {
                lines.Add("repo1-type=posix");
                lines.Add("repo1-path=/var/lib/pgbackrest");
                lines.Add("cmd=/usr/bin/pgbackrest");
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Drill measurements go to the test output (and the console for scripted drills).</summary>
    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        Console.Error.WriteLine(line);
    }

    private static async Task StartAsync(IContainer c) =>
        await RunAsync(c, $"gosu postgres pg_ctl -D {Data} -l /tmp/postgres.log -w -t 600 start");

    private static async Task WaitPromotedAsync(IContainer c)
    {
        for (var i = 0; i < 600; i++)
        {
            if (await SqlAsync(c, "SELECT pg_is_in_recovery()") == "f")
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        throw new TimeoutException("PostgreSQL did not promote after recovery.");
    }

    private static Task<string> InsertAsync(IContainer c, string batch, int from, int count) => SqlAsync(c,
        $"INSERT INTO drill (id, batch, payload) SELECT g, '{batch}', repeat(md5(g::text), 32) FROM generate_series({from}, {from + count - 1}) g");

    private static async Task<string> SqlAsync(IContainer c, string sql) =>
        (await RunAsync(c, $"gosu postgres psql -h /var/run/postgresql -d postgres -tAX -v ON_ERROR_STOP=1 -c \"{sql.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")).Trim();

    private static async Task<string> RunAsync(IContainer c, string script)
    {
        var result = await c.ExecAsync(["bash", "-c", "set -euo pipefail\n" + script], Ct);
        result.ExitCode.Should().Be(0, "{0}\n{1}{2}", script, result.Stdout, result.Stderr);
        return result.Stdout;
    }
}
