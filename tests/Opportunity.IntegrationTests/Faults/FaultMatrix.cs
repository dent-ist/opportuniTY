#if OPPORTUNITY_FAILPOINTS
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Faults;

namespace Opportunity.IntegrationTests.Faults;

/// <summary>The fault types of the E18-T01 matrix.</summary>
public enum FaultKind
{
    /// <summary>The process dies at the failpoint (<c>kill -9</c>): nothing more is recorded, the delivery stays
    /// unsettled until the killed host's connection closes, and a fresh host takes over.</summary>
    Crash,

    /// <summary>The same message is delivered a second time, concurrently with the first.</summary>
    DuplicateDelivery,

    /// <summary>The handler stalls past its lease / ack deadline; the work is redelivered to another consumer, which
    /// completes it; then the stalled handler resumes.</summary>
    RedeliveryAfterAckTimeout,

    /// <summary>The message is held at the failpoint while later work overtakes it.</summary>
    OutOfOrderDelivery,

    OpenSearch429,

    OpenSearch503,

    /// <summary>A <c>_bulk</c> request partly fails: some items are not applied and answer 429/503 item errors.</summary>
    OpenSearchPartialBulk,

    /// <summary>The workers' link to OpenSearch is cut (Toxiproxy) for 1–2.5 s.</summary>
    OpenSearchPartition,

    /// <summary>The OpenSearch container is paused (<c>docker pause</c>) for 1–2.5 s.</summary>
    OpenSearchPause,

    /// <summary>The workers' and dispatcher's link to PostgreSQL is cut (Toxiproxy) for 1–2.5 s.</summary>
    PostgresPartition,

    /// <summary>
    /// PostgreSQL restarts (container stop/start). Failover is not available in the developer/Testcontainers topology
    /// (one primary, no replica), so a restart stands in for it.
    /// </summary>
    PostgresRestart,

    /// <summary>The workers' and dispatcher's link to RabbitMQ is cut (Toxiproxy) for 1–2.5 s.</summary>
    RabbitMqPartition,

    /// <summary>The RabbitMQ node restarts its broker application (<c>rabbitmqctl stop_app; start_app</c>).</summary>
    RabbitMqRestart,
}

/// <summary>What the trial runs: interactive edits, a bulk coding job, or both at once over overlapping documents.</summary>
public enum FaultWorkload
{
    Mixed,
    Interactive,
    Bulk,
}

/// <summary>One cell of the matrix.</summary>
public sealed record FaultCell(FaultKind Fault, string Failpoint, FaultWorkload Workload)
{
    public override string ToString() => $"{Fault}@{Failpoint}/{Workload}";
}

/// <summary>
/// The seeded fault matrix (E18-T01): fault × failpoint × workload. Every message fault is applied at every failpoint
/// that handles a message, a crash and every infrastructure fault at every failpoint, all under the mixed workload
/// (bulk job and interactive edits over overlapping documents); the crash cells also run each failpoint's own workload. Cells run 1 trial on pull
/// requests (a subset that still covers every fault type and every failpoint) and ≥ 5 trials nightly (all cells).
/// Each trial's seed derives from the base seed and the cell, is printed, and replays the trial
/// (<see cref="SeedVariable"/>).
/// </summary>
public static class FaultMatrix
{
    /// <summary><c>pr</c> (default) or <c>full</c>.</summary>
    public const string ScopeVariable = "OPPORTUNITY_FAULT_MATRIX";

    /// <summary>Trials per cell (default 1; the nightly workflow sets 5).</summary>
    public const string TrialsVariable = "OPPORTUNITY_FAULT_TRIALS";

    /// <summary>Base seed of the run (default fixed, so a PR run is reproducible as a whole).</summary>
    public const string BaseSeedVariable = "OPPORTUNITY_FAULT_BASE_SEED";

    /// <summary>Replays exactly one trial: the seed printed by a failure (combine with a test filter for the cell).</summary>
    public const string SeedVariable = "OPPORTUNITY_FAULT_SEED";

    /// <summary>ADR-004 candidate judged by the oracle (default the interim Candidate A, the only design that exists, Q-70).</summary>
    public const string CandidateVariable = "OPPORTUNITY_FAULT_CANDIDATE";

    public const long DefaultBaseSeed = 20261005;

    /// <summary>Failpoints that handle a message (message faults apply there).</summary>
    public static IReadOnlyList<string> MessageFailpoints { get; } =
    [
        Failpoints.AfterClaim,              // job chunk claimed, before the PostgreSQL commit
        Failpoints.AfterCommit,             // after the PostgreSQL commit, before the RabbitMQ ack
        Failpoints.IndexTaskBeforeBulk,     // index task read PostgreSQL, before the OpenSearch bulk
        Failpoints.IndexTaskAfterBulk,      // after the OpenSearch bulk ack (mid-chunk on the first of several requests)
        Failpoints.IndexTaskAfterApplied,   // task Applied in PostgreSQL, before the RabbitMQ ack
        Failpoints.OutboxBeforeBulk,        // interactive: before the OpenSearch write
        Failpoints.OutboxAfterBulk,         // interactive: after the OpenSearch ack, before marking Applied
    ];

    /// <summary>Every failpoint of the matrix: the message failpoints plus the dispatcher's publish/mark gap.</summary>
    public static IReadOnlyList<string> AllFailpoints { get; } = [.. MessageFailpoints, Failpoints.RelayAfterPublish];

    private static readonly FaultKind[] MessageFaults = [FaultKind.DuplicateDelivery, FaultKind.RedeliveryAfterAckTimeout, FaultKind.OutOfOrderDelivery];

    public static IReadOnlyList<FaultCell> Full { get; } = BuildFull();

    /// <summary>
    /// The pull-request subset: for each fault type one cell, failpoints rotated so that every failpoint is also hit,
    /// plus the crash at every failpoint (kill before/after each commit and ack is the core idempotency case).
    /// </summary>
    public static IReadOnlyList<FaultCell> PullRequest { get; } = BuildPullRequest();

    /// <summary>Restricts the run to cells whose name (<c>Fault@failpoint/Workload</c>) contains one of these comma-separated parts.</summary>
    public const string CellsVariable = "OPPORTUNITY_FAULT_CELLS";

    public static IReadOnlyList<FaultCell> Selected
    {
        get
        {
            var cells = string.Equals(Environment.GetEnvironmentVariable(ScopeVariable), "full", StringComparison.OrdinalIgnoreCase) ? Full : PullRequest;
            if (Environment.GetEnvironmentVariable(CellsVariable) is { Length: > 0 } filter)
            {
                var parts = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                cells = [.. Full.Where(c => parts.Any(p => c.ToString().Contains(p, StringComparison.OrdinalIgnoreCase)))];
            }

            return cells;
        }
    }

    public static int Trials => int.TryParse(Environment.GetEnvironmentVariable(TrialsVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;

    public static long BaseSeed => long.TryParse(Environment.GetEnvironmentVariable(BaseSeedVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
        ? s : DefaultBaseSeed;

    /// <summary>The seeds to run for <paramref name="cell"/>: the replay seed alone, or one per trial.</summary>
    public static IReadOnlyList<long> Seeds(FaultCell cell)
    {
        if (long.TryParse(Environment.GetEnvironmentVariable(SeedVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var replay))
        {
            return [replay];
        }

        return [.. Enumerable.Range(1, Trials).Select(trial => Seed(BaseSeed, cell, trial))];
    }

    /// <summary>A stable seed per (base, cell, trial), independent of the order cells run in.</summary>
    public static long Seed(long baseSeed, FaultCell cell, int trial)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{baseSeed}|{cell}|{trial}")));
        return BitConverter.ToInt64(bytes, 0) & 0x7FFF_FFFF_FFFF;
    }

    /// <summary>The workload a failpoint needs: interactive failpoints run the interactive workload, the others bulk.</summary>
    public static FaultWorkload DedicatedWorkload(string failpoint) =>
        failpoint is Failpoints.OutboxBeforeBulk or Failpoints.OutboxAfterBulk ? FaultWorkload.Interactive : FaultWorkload.Bulk;

    private static List<FaultCell> BuildFull()
    {
        var cells = new List<FaultCell>();
        foreach (var fault in Enum.GetValues<FaultKind>())
        {
            var failpoints = MessageFaults.Contains(fault) ? MessageFailpoints : AllFailpoints;
            foreach (var failpoint in failpoints)
            {
                cells.Add(new FaultCell(fault, failpoint, FaultWorkload.Mixed));
                if (fault == FaultKind.Crash)
                {
                    // The kill cells also run on the failpoint's own workload alone (no concurrent traffic to mask them).
                    cells.Add(new FaultCell(fault, failpoint, DedicatedWorkload(failpoint)));
                }
            }
        }

        return cells;
    }

    private static List<FaultCell> BuildPullRequest()
    {
        var cells = AllFailpoints.Select(f => new FaultCell(FaultKind.Crash, f, FaultWorkload.Mixed)).ToList();
        var rotation = 0;
        foreach (var fault in Enum.GetValues<FaultKind>().Where(f => f != FaultKind.Crash))
        {
            var failpoints = MessageFaults.Contains(fault) ? MessageFailpoints : AllFailpoints;
            cells.Add(new FaultCell(fault, failpoints[rotation % failpoints.Count], FaultWorkload.Mixed));
            rotation += 3;
        }

        return cells;
    }
}
#endif
