#if OPPORTUNITY_FAILPOINTS
using System.Text.Json.Nodes;

using Npgsql;

using Opportunity.Application.Search.Indexing;
using Opportunity.Correctness.ShadowLedger;
using Opportunity.Search.Indexing;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>Shared plumbing for tests that judge a run with the shadow-ledger oracle (E17-T07).</summary>
internal static class LedgerSupport
{
    /// <summary>When set, every verdict.json is kept under this directory (CI uploads it); otherwise nothing is written.</summary>
    public const string ResultsVariable = "OPPORTUNITY_ORACLE_RESULTS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The workspace's read target (alias and routing) as the index manager resolves it.</summary>
    public static async Task<LedgerIndexTarget> TargetAsync(IIndexManager indexes, Guid workspaceId)
    {
        var placement = await indexes.ResolveAsync(workspaceId, IndexPurpose.Read, Ct);
        return new LedgerIndexTarget(placement.Read.Index, placement.Read.Routing);
    }

    public static Task<LedgerOracle> StartAsync(
        NpgsqlDataSource postgres, HttpClient openSearch, Guid workspaceId, LedgerIndexTarget target, Func<long>? rejections, long? seed,
        IReadOnlyCollection<Guid>? extra = null, LedgerScope scope = LedgerScope.Touched, int batchSize = 1_000, DateTimeOffset? since = null) =>
        LedgerOracle.StartAsync(postgres, openSearch, new ShadowLedgerOptions
        {
            WorkspaceId = workspaceId,
            Since = since,
            Index = target,
            Candidate = Environment.GetEnvironmentVariable(FaultMatrix.CandidateVariable),
            VersionConflictRejections = rejections,
            Seed = seed,
            ExtraDocuments = extra ?? [],
            Scope = scope,
            BatchSize = batchSize,
            SampleInterval = TimeSpan.FromMilliseconds(50),
        }, Ct);

    /// <summary>Keeps the verdict when <see cref="ResultsVariable"/> is set, and prints it to the test output.</summary>
    public static async Task KeepAsync(ShadowLedgerVerdict verdict, string name)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(verdict.ToString());
        if (Environment.GetEnvironmentVariable(ResultsVariable) is { Length: > 0 } root)
        {
            var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
            await verdict.WriteAsync(Path.Combine(root, safe, "verdict.json"), Ct);
        }
    }

    /// <summary>Asserts the verdict JSON is well formed for the bundle (the schema itself is checked in Benchmarks.Tests).</summary>
    public static JsonObject Json(ShadowLedgerVerdict verdict) => JsonNode.Parse(verdict.ToJson())!.AsObject();
}
#endif
