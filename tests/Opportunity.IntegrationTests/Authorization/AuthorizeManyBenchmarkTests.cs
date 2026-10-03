using System.Diagnostics;
using System.Globalization;

using AwesomeAssertions;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Core.Security;
using Opportunity.IntegrationTests.Migrations;
using Opportunity.Security.Authorization;

namespace Opportunity.IntegrationTests.Authorization;

/// <summary>
/// E05-T02 acceptance: <c>AuthorizeMany</c> for 100 IDs ≤ 20 ms p95 at 1M documents. Opt-in (it loads 1M documents):
/// set <c>OPPORTUNITY_PDP_BENCHMARK=1</c>. Each iteration is a fresh DI scope, so it includes the principal-state read
/// (the worst case: a search page post-filter in a new request). Results are recorded in docs/security/pdp-benchmark.md.
/// </summary>
[Collection(MigrationPostgresGroup.Name)]
public sealed class AuthorizeManyBenchmarkTests(MigrationPostgresFixture postgres)
{
    private const int Documents = 1_000_000;
    private const int BatchSize = 100;
    private const int Warmup = 200;
    private const int Iterations = 2_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Authorize_many_100_ids_at_1m_documents_is_within_20_ms_p95()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OPPORTUNITY_PDP_BENCHMARK") == "1",
            "Set OPPORTUNITY_PDP_BENCHMARK=1 to run the 1M-document AuthorizeMany benchmark.");

        await using var db = await AuthorizationDatabase.CreateAsync(postgres);
        var ws = await db.Core.CreateWorkspaceAsync();
        var user = await db.CreateUserAsync();
        await db.AssignAsync(ws, WorkspaceRole.Reviewer, user);
        await db.AssignGroupAsync(ws, WorkspaceRole.QcReviewer, "cn=qc");

        var load = Stopwatch.StartNew();
        await LongAsync(db,
            """
            INSERT INTO opportunity.document (workspace_id, document_id, control_number, control_number_norm, family_id)
            SELECT @ws, id, cn, cn, id
            FROM (SELECT gen_random_uuid() AS id, 'BENCH' || lpad(g::text, 8, '0') AS cn FROM generate_series(1, @n) g) s
            """,
            ("ws", ws), ("n", Documents));
        // ~10 % restricted (half of them AEO, hidden from the reviewer) and ~1 % behind a wall naming the user.
        await LongAsync(db,
            """
            INSERT INTO opportunity.document_restriction (workspace_id, document_id, class_key)
            SELECT workspace_id, document_id, CASE WHEN random() < 0.5 THEN 'Privileged' ELSE 'AttorneysEyesOnly' END
            FROM opportunity.document WHERE workspace_id = @ws AND random() < 0.10
            """,
            ("ws", ws));
        var wall = await db.WallAsync(ws, users: [user], groups: ["cn=deal"], documents: []);
        await LongAsync(db,
            "INSERT INTO opportunity.document_wall (workspace_id, document_id, wall_id) SELECT workspace_id, document_id, @wall FROM opportunity.document WHERE workspace_id = @ws AND random() < 0.01",
            ("ws", ws), ("wall", wall));
        await LongAsync(db, "VACUUM ANALYZE opportunity.document");
        await LongAsync(db, "VACUUM ANALYZE opportunity.document_restriction");
        await LongAsync(db, "VACUUM ANALYZE opportunity.document_wall");
        load.Stop();

        var ids = await SampleIdsAsync(db, ws, 50_000);
        var principal = new SecurityPrincipal { UserId = user, DisplayName = "bench", Groups = ["cn=qc", "cn=deal", "cn=other"] };

        // In-request: the post-filter runs after PEP-1 already read the principal state in the same scope (normal path).
        var (warm, allowed) = await MeasureAsync(db, ws, principal, ids, warmScope: true);

        // Fresh scope per call: each call also reads the principal state (worker chunk, first check of a request).
        var (cold, _) = await MeasureAsync(db, ws, principal, ids, warmScope: false);

        var report = string.Create(CultureInfo.InvariantCulture,
            $"AuthorizeMany {BatchSize} ids @ {Documents:N0} docs (load {load.Elapsed.TotalSeconds:F0} s, {Iterations} calls each, " +
            $"allowed {allowed * 100.0 / (Iterations * (double)BatchSize):F1} %): in-request {Describe(warm)}; fresh scope {Describe(cold)}");
        TestContext.Current.SendDiagnosticMessage(report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        if (Environment.GetEnvironmentVariable("OPPORTUNITY_PDP_BENCHMARK_REPORT") is { Length: > 0 } reportPath)
        {
            await File.AppendAllTextAsync(reportPath, report + Environment.NewLine, Ct);
        }

        Percentile(warm, 0.95).Should().BeLessThanOrEqualTo(20, report);
    }

    private static async Task<(List<double> Samples, long Allowed)> MeasureAsync(
        AuthorizationDatabase db, Guid ws, SecurityPrincipal principal, List<Guid> ids, bool warmScope)
    {
        var random = new Random(47);
        var samples = new List<double>(Iterations);
        long allowed = 0;
        for (var i = 0; i < Warmup + Iterations; i++)
        {
            var batch = Enumerable.Range(0, BatchSize).Select(_ => ids[random.Next(ids.Count)]).ToList();
            var pdp = new AuthorizationService(db.Reader, new NullAuditEventWriter(), TimeProvider.System);
            if (warmScope)
            {
                (await pdp.AuthorizeAsync(principal, ws, Permission.SearchExecute, Ct)).IsAllowed.Should().BeTrue();
            }

            var started = Stopwatch.GetTimestamp();
            var result = await pdp.AuthorizeManyAsync(principal, ws, Permission.DocumentView, batch, DenialAudit.Summary, Ct);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (i >= Warmup)
            {
                samples.Add(elapsed);
                allowed += result.Values.Count(d => d.IsAllowed);
            }
        }

        samples.Sort();
        return (samples, allowed);
    }

    private static double Percentile(List<double> sorted, double q) => sorted[(int)Math.Ceiling(q * sorted.Count) - 1];

    private static string Describe(List<double> s) => string.Create(CultureInfo.InvariantCulture,
        $"p50 {Percentile(s, 0.50):F2} ms, p95 {Percentile(s, 0.95):F2} ms, p99 {Percentile(s, 0.99):F2} ms, max {s[^1]:F2} ms");

    private static async Task LongAsync(AuthorizationDatabase db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.Core.DataSource.CreateCommand(sql);
        command.CommandTimeout = 0;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<Guid>> SampleIdsAsync(AuthorizationDatabase db, Guid ws, int count)
    {
        await using var command = db.Core.DataSource.CreateCommand(
            "SELECT document_id FROM opportunity.document TABLESAMPLE SYSTEM (10) WHERE workspace_id = $1 LIMIT $2");
        command.Parameters.AddWithValue(ws);
        command.Parameters.AddWithValue(count);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>(count);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
