using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using AwesomeAssertions;

using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Snapshots;
using Opportunity.Core.Snapshots;
using Opportunity.Data.Snapshots;
using Opportunity.IntegrationTests.Containers;
using Opportunity.IntegrationTests.Documents;
using Opportunity.IntegrationTests.Migrations;

namespace Opportunity.IntegrationTests.Snapshots;

/// <summary>
/// E10-T03 / ADR-002 §6: the membership-representation benchmark. Opt-in (it writes a large data set): run with
/// <c>OPPORTUNITY_SNAPSHOT_BENCHMARK=1</c> and optionally <c>OPPORTUNITY_SNAPSHOT_BENCHMARK_MEMBERS</c> (default 100,000;
/// 1,000,000 on reference hardware, Q-70) and <c>OPPORTUNITY_SNAPSHOT_BENCHMARK_OUT</c> (a file for the Markdown
/// result). Compares, over the same N live documents of one workspace:
/// <list type="bullet">
/// <item>(b) PG member pages — the production store (<see cref="DocumentSetSnapshotStore"/>): freeze through
/// <c>FreezeAsync</c> (allow-all authorizer), chunks through <c>ReadMembersAsync</c> as the RLS-bound app login;</item>
/// <item>(a) a row per member — a benchmark-only table with the same ordering and per-page hashes;</item>
/// <item>(c) immutable chunked manifests — 1,000-member binary pages, zlib-compressed, with SHA-256 per page, written to
/// a temporary directory standing in for object storage (local disk: no network latency is included).</item>
/// </list>
/// Metrics per representation: freeze time, stored bytes (heap + index + TOAST, or file bytes), WAL bytes, p50/p95 to
/// resolve one 1,000-member chunk, p95 of the set-based bulk-coding <c>UPDATE</c> joined to one chunk, delete time and
/// the <c>VACUUM</c> that follows it (autovacuum proxy). Nothing is asserted about absolute times (Q-44/Q-70).
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class SnapshotRepresentationBenchmark(MigrationPostgresFixture postgres)
{
    private const int PageSize = SnapshotRules.DefaultPageSize;
    private const int ChunkSamples = 200;
    private const int UpdateSamples = 50;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Optional session settings for both logins, e.g. <c>-c work_mem=64MB</c> (<c>OPPORTUNITY_SNAPSHOT_BENCHMARK_PGOPTIONS</c>).</summary>
    private static string? PgOptions => Environment.GetEnvironmentVariable("OPPORTUNITY_SNAPSHOT_BENCHMARK_PGOPTIONS");

    [Fact]
    public async Task Membership_representations_compared_at_scale()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OPPORTUNITY_SNAPSHOT_BENCHMARK") == "1",
            "Opt-in benchmark: set OPPORTUNITY_SNAPSHOT_BENCHMARK=1 (ADR-002 §6, E10-T03).");
        var members = int.Parse(Environment.GetEnvironmentVariable("OPPORTUNITY_SNAPSHOT_BENCHMARK_MEMBERS") ?? "100000", CultureInfo.InvariantCulture);
        members.Should().BeGreaterThanOrEqualTo(PageSize * 10);

        await using var core = await CoreSchemaDatabase.CreateAsync(postgres);
        var ws = await core.CreateWorkspaceAsync();
        // Large statements: no command timeout (the defaults are 30 s).
        await using var db = new Bench(
            NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(core.ConnectionString) { CommandTimeout = 0, Options = PgOptions }.ConnectionString),
            NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(core.AppConnectionString) { CommandTimeout = 0, Options = PgOptions }.ConnectionString));
        await db.ExecuteAsync(
            """
            WITH d AS (SELECT n, gen_random_uuid() AS id FROM generate_series(1, @n) AS n)
            INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id)
            SELECT @ws, d.id, 'BM' || lpad(d.n::text, 9, '0'), 'BM' || lpad(d.n::text, 9, '0'), d.id FROM d;
            INSERT INTO opportunity.document_projection_state (workspace_id, document_id)
            SELECT workspace_id, document_id FROM opportunity.document WHERE workspace_id = @ws;
            CREATE TABLE public.bench_coding (document_id uuid PRIMARY KEY, v integer NOT NULL DEFAULT 0);
            INSERT INTO public.bench_coding (document_id) SELECT document_id FROM opportunity.document WHERE workspace_id = @ws;
            ANALYZE;
            """,
            ("ws", ws), ("n", members));
        var random = new Random(91);
        var temp = Directory.CreateTempSubdirectory("opp-snapshot-bench-");
        try
        {
            var b = await PageStoreAsync(db, ws, members, random);
            var a = await RowPerMemberAsync(db, ws, members, random);
            var c = await ManifestsAsync(db, ws, members, random, temp.FullName);
            var report = Report(members, [a, b, c]);
            TestContext.Current.TestOutputHelper?.WriteLine(report);
            if (Environment.GetEnvironmentVariable("OPPORTUNITY_SNAPSHOT_BENCHMARK_OUT") is { Length: > 0 } output)
            {
                await File.WriteAllTextAsync(output, report, Ct);
            }

            foreach (var result in new[] { a, b, c })
            {
                result.Members.Should().Be(members, result.Name);
            }
        }
        finally
        {
            temp.Delete(recursive: true);
            await db.ExecuteAsync("DROP TABLE IF EXISTS public.bench_coding; DROP TABLE IF EXISTS public.bench_member;");
        }
    }

    /// <summary>(b): the production store, end to end.</summary>
    private static async Task<Result> PageStoreAsync(Bench db, Guid ws, int members, Random random)
    {
        var store = new DocumentSetSnapshotStore(db.App);
        var created = await store.CreateAsync(new NewSnapshot
        {
            WorkspaceId = ws,
            Name = "Benchmark",
            Purpose = SnapshotPurpose.BulkCoding,
            SourceKind = SnapshotSourceKind.DocumentIds,
            RequestedCount = members,
            CreatedBy = Guid.NewGuid(),
            CreatedByDisplay = "benchmark",
            ClaimOwner = "benchmark",
            ClaimLease = TimeSpan.FromHours(1),
        }, Ct);
        var id = created.Snapshot.SnapshotId;
        var ids = (await db.ColumnAsync($"SELECT document_id::text FROM opportunity.document WHERE workspace_id = '{ws}'")).Select(Guid.Parse).ToList();
        foreach (var batch in ids.Chunk(10_000))
        {
            await store.StageAsync(ws, id, batch, SnapshotInclusionReason.Explicit, Ct);
        }

        var wal = await WalAsync(db);
        var watch = Stopwatch.StartNew();
        var frozen = await store.FreezeAsync(new SnapshotFreezeRequest { WorkspaceId = ws, SnapshotId = id, ClaimOwner = "benchmark" },
            (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()), Ct);
        var freeze = watch.Elapsed;
        var walBytes = await WalAsync(db) - wal;
        frozen.Snapshot!.DocumentCount.Should().Be(members);
        var bytes = await ScalarAsync<long>(db, "SELECT pg_total_relation_size('opportunity.document_set_snapshot_page')");

        var resolve = await SampleAsync(random, members, ChunkSamples, async (from, to) =>
            (await store.ReadMembersAsync(ws, id, from, to, Ct)).Count.Should().Be((int)(to - from + 1)));
        var update = await SampleAsync(random, members, UpdateSamples, (from, to) => db.ExecuteAsync(
            """
            UPDATE public.bench_coding c SET v = c.v + 1
              FROM (SELECT u.document_id
                      FROM opportunity.document_set_snapshot_page p
                     CROSS JOIN LATERAL unnest(p.document_ids) WITH ORDINALITY AS u(document_id, i)
                     WHERE p.workspace_id = @ws AND p.snapshot_id = @id
                       AND p.page_no BETWEEN (@from - 1) / @size + 1 AND (@to - 1) / @size + 1
                       AND p.first_ordinal + u.i - 1 BETWEEN @from AND @to) m
             WHERE c.document_id = m.document_id
            """,
            ("ws", ws), ("id", id), ("from", from), ("to", to), ("size", (long)PageSize)));

        watch.Restart();
        (await store.ExpireAsync(ws, TimeSpan.Zero, TimeSpan.Zero, 10, Ct)).Should().Contain(id);
        var delete = watch.Elapsed;
        watch.Restart();
        await db.ExecuteAsync("VACUUM opportunity.document_set_snapshot_page");
        return new Result("(b) PG member pages", members, freeze, bytes, walBytes, resolve, update, delete, watch.Elapsed);
    }

    /// <summary>(a): one row per member with the same ordering, baselines and per-page hashes.</summary>
    private static async Task<Result> RowPerMemberAsync(Bench db, Guid ws, int members, Random random)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            """
            CREATE TABLE public.bench_member (
                workspace_id uuid NOT NULL, snapshot_id uuid NOT NULL, ordinal bigint NOT NULL, document_id uuid NOT NULL,
                baseline_version bigint NOT NULL, inclusion_reason smallint NOT NULL,
                PRIMARY KEY (workspace_id, snapshot_id, ordinal))
            """);
        var wal = await WalAsync(db);
        var watch = Stopwatch.StartNew();
        await using (var connection = await db.Admin.OpenConnectionAsync(Ct))
        await using (var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, Ct))
        {
            await using var freeze = new NpgsqlCommand(
                $"""
                INSERT INTO public.bench_member
                SELECT @ws, @id, row_number() OVER (ORDER BY o.family_key, o.family_id, o.family_sequence, o.document_id),
                       o.document_id, o.document_version, 2
                  FROM ({OrderedMembersSql}) o;
                SELECT count(*) FROM (
                    SELECT sha256(string_agg(int8send(m.ordinal) || uuid_send(m.document_id) || int8send(m.baseline_version)
                                             || int2send(m.inclusion_reason), ''::bytea ORDER BY m.ordinal))
                      FROM public.bench_member m WHERE m.workspace_id = @ws AND m.snapshot_id = @id
                     GROUP BY (m.ordinal - 1) / @size) h;
                """, connection, tx);
            freeze.Parameters.AddWithValue("ws", ws);
            freeze.Parameters.AddWithValue("id", id);
            freeze.Parameters.AddWithValue("size", (long)PageSize);
            await freeze.ExecuteScalarAsync(Ct);
            await tx.CommitAsync(Ct);
        }

        var freezeTime = watch.Elapsed;
        var walBytes = await WalAsync(db) - wal;
        var bytes = await ScalarAsync<long>(db, "SELECT pg_total_relation_size('public.bench_member')");
        var resolve = await SampleAsync(random, members, ChunkSamples, async (from, to) =>
        {
            await using var command = db.Admin.CreateCommand(
                """
                SELECT ordinal, document_id, baseline_version, inclusion_reason FROM public.bench_member
                 WHERE workspace_id = @ws AND snapshot_id = @id AND ordinal BETWEEN @from AND @to ORDER BY ordinal
                """);
            command.Parameters.AddWithValue("ws", ws);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("from", from);
            command.Parameters.AddWithValue("to", to);
            var count = 0;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                count++;
            }

            count.Should().Be((int)(to - from + 1));
        });
        var update = await SampleAsync(random, members, UpdateSamples, (from, to) => db.ExecuteAsync(
            """
            UPDATE public.bench_coding c SET v = c.v + 1 FROM public.bench_member m
             WHERE m.workspace_id = @ws AND m.snapshot_id = @id AND m.ordinal BETWEEN @from AND @to AND c.document_id = m.document_id
            """,
            ("ws", ws), ("id", id), ("from", from), ("to", to)));
        watch.Restart();
        await db.ExecuteAsync("DELETE FROM public.bench_member WHERE workspace_id = @ws AND snapshot_id = @id", ("ws", ws), ("id", id));
        var delete = watch.Elapsed;
        watch.Restart();
        await db.ExecuteAsync("VACUUM public.bench_member");
        return new Result("(a) row per member", members, freezeTime, bytes, walBytes, resolve, update, delete, watch.Elapsed);
    }

    /// <summary>(c): compressed 1,000-member manifest pages; a chunk is read, decoded and loaded into a temp table.</summary>
    private static async Task<Result> ManifestsAsync(Bench db, Guid ws, int members, Random random, string root)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "manifest")).FullName;
        var hashes = new List<byte[]>();
        var watch = Stopwatch.StartNew();
        await using (var command = db.Admin.CreateCommand($"SELECT o.document_id, o.document_version FROM ({OrderedMembersSql}) o ORDER BY o.family_key, o.family_id, o.family_sequence, o.document_id"))
        {
            command.Parameters.AddWithValue("ws", ws);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var page = new List<SnapshotMember>(PageSize);
            long ordinal = 0;
            while (await reader.ReadAsync(Ct))
            {
                page.Add(new SnapshotMember(++ordinal, reader.GetGuid(0), reader.GetInt64(1), SnapshotInclusionReason.Explicit));
                if (page.Count == PageSize)
                {
                    hashes.Add(await WritePageAsync(dir, hashes.Count + 1, page));
                    page.Clear();
                }
            }

            if (page.Count > 0)
            {
                hashes.Add(await WritePageAsync(dir, hashes.Count + 1, page));
            }
        }

        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"),
            "{\"pages\":[" + string.Join(',', hashes.Select(h => "\"" + Convert.ToHexStringLower(h) + "\"")) + "]}", Ct);
        var freeze = watch.Elapsed;
        var bytes = new DirectoryInfo(dir).EnumerateFiles().Sum(f => f.Length);

        await using var connection = await db.Admin.OpenConnectionAsync(Ct);
        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE chunk_member (ordinal bigint PRIMARY KEY, document_id uuid NOT NULL, baseline_version bigint NOT NULL, reason smallint NOT NULL)",
            connection))
        {
            await create.ExecuteNonQueryAsync(Ct);
        }

        async Task LoadAsync(long from, long to)
        {
            await using (var truncate = new NpgsqlCommand("TRUNCATE chunk_member", connection))
            {
                await truncate.ExecuteNonQueryAsync(Ct);
            }

            await using var import = await connection.BeginBinaryImportAsync(
                "COPY chunk_member (ordinal, document_id, baseline_version, reason) FROM STDIN (FORMAT BINARY)", Ct);
            for (var pageNo = (int)((from - 1) / PageSize) + 1; pageNo <= (int)((to - 1) / PageSize) + 1; pageNo++)
            {
                foreach (var m in await ReadPageAsync(dir, pageNo))
                {
                    if (m.Ordinal >= from && m.Ordinal <= to)
                    {
                        await import.StartRowAsync(Ct);
                        await import.WriteAsync(m.Ordinal, NpgsqlDbType.Bigint, Ct);
                        await import.WriteAsync(m.DocumentId, NpgsqlDbType.Uuid, Ct);
                        await import.WriteAsync(m.BaselineVersion, NpgsqlDbType.Bigint, Ct);
                        await import.WriteAsync((short)m.Reason, NpgsqlDbType.Smallint, Ct);
                    }
                }
            }

            (await import.CompleteAsync(Ct)).Should().Be((ulong)(to - from + 1));
        }

        var resolve = await SampleAsync(random, members, ChunkSamples, LoadAsync);
        var update = await SampleAsync(random, members, UpdateSamples, async (from, to) =>
        {
            await LoadAsync(from, to);
            await using var command = new NpgsqlCommand(
                "UPDATE public.bench_coding c SET v = c.v + 1 FROM chunk_member m WHERE c.document_id = m.document_id", connection);
            await command.ExecuteNonQueryAsync(Ct);
        });
        watch.Restart();
        Directory.Delete(dir, recursive: true);
        return new Result("(c) object-store manifests", members, freeze, bytes, null, resolve, update, watch.Elapsed, null);
    }

    /// <summary>Live documents of <c>@ws</c> with the freeze's ordering keys (family sort key, family, sequence, document).</summary>
    private const string OrderedMembersSql =
        """
        SELECT d.document_id, ps.document_version, r.control_number_sort_key AS family_key, d.family_id, d.family_sequence
          FROM opportunity.document d
          JOIN opportunity.document r ON r.workspace_id = d.workspace_id AND r.document_id = d.family_id
          JOIN opportunity.document_projection_state ps
            ON ps.workspace_id = d.workspace_id AND ps.document_id = d.document_id AND NOT ps.is_deleted
         WHERE d.workspace_id = @ws
        """;

    private static async Task<byte[]> WritePageAsync(string dir, int pageNo, List<SnapshotMember> page)
    {
        var raw = new byte[page.Count * 34];
        var span = raw.AsSpan();
        foreach (var m in page)
        {
            BinaryPrimitives.WriteInt64BigEndian(span, m.Ordinal);
            m.DocumentId.TryWriteBytes(span[8..], bigEndian: true, out _);
            BinaryPrimitives.WriteInt64BigEndian(span[24..], m.BaselineVersion);
            BinaryPrimitives.WriteInt16BigEndian(span[32..], (short)m.Reason);
            span = span[34..];
        }

        await using (var file = File.Create(Path.Combine(dir, pageNo.ToString(CultureInfo.InvariantCulture) + ".bin")))
        await using (var zlib = new ZLibStream(file, CompressionLevel.Fastest))
        {
            await zlib.WriteAsync(raw, Ct);
        }

        return SHA256.HashData(raw);
    }

    private static async Task<List<SnapshotMember>> ReadPageAsync(string dir, int pageNo)
    {
        await using var file = File.OpenRead(Path.Combine(dir, pageNo.ToString(CultureInfo.InvariantCulture) + ".bin"));
        await using var zlib = new ZLibStream(file, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        await zlib.CopyToAsync(buffer, Ct);
        var raw = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        var members = new List<SnapshotMember>(raw.Length / 34);
        for (var offset = 0; offset < raw.Length; offset += 34)
        {
            var s = raw.Span[offset..];
            members.Add(new SnapshotMember(
                BinaryPrimitives.ReadInt64BigEndian(s), new Guid(s[8..24], bigEndian: true), BinaryPrimitives.ReadInt64BigEndian(s[24..]),
                (SnapshotInclusionReason)BinaryPrimitives.ReadInt16BigEndian(s[32..])));
        }

        return members;
    }

    /// <summary>Times <paramref name="samples"/> random 1,000-member chunks (unaligned, so a chunk usually spans two pages).</summary>
    private static async Task<Timings> SampleAsync(Random random, int members, int samples, Func<long, long, Task> work)
    {
        var times = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            var from = random.NextInt64(1, members - PageSize + 2);
            var watch = Stopwatch.StartNew();
            await work(from, from + PageSize - 1);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return new Timings(Percentile(times, 0.50), Percentile(times, 0.95));
    }

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static async Task<long> WalAsync(Bench db) =>
        (long)await ScalarAsync<decimal>(db, "SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), '0/0')");

    private static async Task<T> ScalarAsync<T>(Bench db, string sql)
    {
        await using var command = db.Admin.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string Report(int members, IReadOnlyList<Result> results)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Snapshot membership benchmark: {members:N0} members, {Environment.ProcessorCount} logical CPUs, PostgreSQL settings: {PgOptions ?? "container defaults"}, {DateTimeOffset.UtcNow:yyyy-MM-dd} (interim)");
        text.AppendLine();
        text.AppendLine("| Representation | Freeze | Stored bytes | Bytes/member | WAL bytes | Chunk p50 / p95 | Bulk UPDATE p95 | Delete | VACUUM |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Name} | {r.Freeze.TotalSeconds:0.00} s | {r.Bytes / 1048576.0:0.0} MiB | {(double)r.Bytes / members:0.0} | " +
                $"{(r.WalBytes is { } w ? (w / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB" : "n/a")} | " +
                $"{r.Resolve.P50:0.0} / {r.Resolve.P95:0.0} ms | {r.Update.P95:0.0} ms | {r.Delete.TotalMilliseconds:0} ms | " +
                $"{(r.Vacuum is { } v ? v.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms" : "n/a")} |");
        }

        var b = results.Single(r => r.Name.StartsWith("(b)", StringComparison.Ordinal));
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"(b) freeze extrapolated linearly to 10M members: {b.Freeze.TotalSeconds * (10_000_000.0 / members) / 60:0.0} min; (b) bytes/member: {(double)b.Bytes / members:0.0}.");
        return text.ToString();
    }

    private sealed record Timings(double P50, double P95);

    /// <summary>The benchmark database: superuser (bypasses RLS) and the RLS-bound app login, without command timeouts.</summary>
    private sealed class Bench(NpgsqlDataSource admin, NpgsqlDataSource app) : IAsyncDisposable
    {
        public NpgsqlDataSource Admin { get; } = admin;

        public NpgsqlDataSource App { get; } = app;

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = Admin.CreateCommand(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(Ct);
        }

        public async Task<List<string>> ColumnAsync(string sql)
        {
            await using var command = Admin.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var values = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }

        public async ValueTask DisposeAsync()
        {
            await Admin.DisposeAsync();
            await App.DisposeAsync();
        }
    }

    private sealed record Result(
        string Name, long Members, TimeSpan Freeze, long Bytes, long? WalBytes, Timings Resolve, Timings Update, TimeSpan Delete, TimeSpan? Vacuum);
}
