using System.Globalization;

namespace Opportunity.Core.Snapshots;

/// <summary>How an operation over a document set reads its membership (baseline §22, ADR-002 §2–§4).</summary>
public enum SelectionStrategy
{
    /// <summary>An OpenSearch point-in-time reader paged with <c>search_after</c>; nothing is stored.</summary>
    PointInTime,

    /// <summary>A materialized <c>DocumentSetSnapshot</c> with immutable, ordered membership.</summary>
    Materialized,
}

/// <summary>The four ADR-002 §2 predicates. Any one of them forces a materialized snapshot.</summary>
[Flags]
public enum StrategyPredicates
{
    None = 0,

    /// <summary><b>L</b>: <c>T_est × SafetyFactor &gt; Pit.JobMaxAge</c> (or the size is unknown).</summary>
    LongRunning = 1,

    /// <summary><b>R</b>: the operation mutates authoritative state or must resume after a crash without redoing work.</summary>
    ExactRestart = 2,

    /// <summary><b>A</b>: the membership must outlive the reader (alias/index changes, referenced after the job).</summary>
    SurvivesIndexChange = 4,

    /// <summary><b>X</b>: the membership is evidence (export, production, privilege log, saved search-term report).</summary>
    LegalReproducibility = 8,
}

/// <summary>Every operation over a document set that the rule decides for (ADR-002 §4).</summary>
public enum SetOperationKind
{
    BulkCoding,

    /// <summary>Apply-to-family above the interactive limit (Q-14).</summary>
    ApplyToFamily,

    Export,
    Production,

    /// <summary>Reuses the production's snapshot.</summary>
    PrivilegeLog,

    /// <summary>Saved or exported search-term report (Q-30).</summary>
    SavedSearchTermReport,

    /// <summary>On-screen, unsaved search-term report preview: the only job that may use a reader.</summary>
    SearchTermReportPreview,

    /// <summary>Review batch membership (E10-T05; frozen review sets, Q-33).</summary>
    ReviewBatch,

    /// <summary>The interactive grid and review cursor: not a job; always a live reader (ADR-002 §8, Q-33).</summary>
    InteractiveCursor,
}

/// <summary>
/// ADR-002 §2 numeric PIT policy for jobs (section <c>Snapshots:Pit</c>). Defaults are the ADR's; the throughputs are
/// the defaults used until an installation has run history for the job type.
/// </summary>
public sealed class PitPolicy
{
    /// <summary><c>Pit.JobMaxAge</c>: the longest a job may hold a reader.</summary>
    public TimeSpan JobMaxAge { get; set; } = TimeSpan.FromSeconds(600);

    /// <summary><c>Pit.SafetyFactor</c>: short-lived ⇔ <c>T_est × SafetyFactor ≤ JobMaxAge</c> (200 s by default).</summary>
    public double SafetyFactor { get; set; } = 3;

    /// <summary><c>T_setup</c>.</summary>
    public TimeSpan SetupTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary><c>R_type</c> default for ID paging, documents per second.</summary>
    public double IdPagingThroughput { get; set; } = 20_000;

    /// <summary><c>R_type</c> default for a search-term report preview, terms per second (<c>N_est</c> = term count).</summary>
    public double TermReportThroughput { get; set; } = 0.5;

    /// <summary><c>ExpansionFactor</c> before a sample exists, when family/duplicate/thread expansion is asked for.</summary>
    public double DefaultExpansionFactor { get; set; } = 4.0;

    /// <summary>The longest estimate that still runs under a reader (<c>JobMaxAge / SafetyFactor</c>).</summary>
    public TimeSpan ShortLivedLimit => JobMaxAge / SafetyFactor;

    public void Validate(string section)
    {
        Require(JobMaxAge > TimeSpan.Zero && JobMaxAge <= TimeSpan.FromHours(1), section, nameof(JobMaxAge));
        Require(SafetyFactor is >= 1 and <= 100, section, nameof(SafetyFactor));
        Require(SetupTime >= TimeSpan.Zero && SetupTime < JobMaxAge, section, nameof(SetupTime));
        Require(IdPagingThroughput > 0 && double.IsFinite(IdPagingThroughput), section, nameof(IdPagingThroughput));
        Require(TermReportThroughput > 0 && double.IsFinite(TermReportThroughput), section, nameof(TermReportThroughput));
        Require(DefaultExpansionFactor is >= 1 and <= 1_000, section, nameof(DefaultExpansionFactor));
    }

    private static void Require(bool condition, string section, string setting)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{section}:{setting} is out of range.");
        }
    }
}

/// <summary>
/// The size of a selection, for <c>T_est = T_setup + N_est / R_type</c> with <c>N_est = Count × ExpansionFactor</c>.
/// </summary>
/// <param name="Count">Hit count (one <c>track_total_hits</c> count), or the term count of a search-term report preview.</param>
/// <param name="ExpansionFactor">1 without expansion; the sampled expanded/hit ratio otherwise (see <see cref="PitPolicy.DefaultExpansionFactor"/>).</param>
/// <param name="Throughput">Measured <c>R_type</c> (10th percentile of recent runs) when known; the policy default otherwise.</param>
public sealed record SetSizeEstimate(long Count, double ExpansionFactor = 1.0, double? Throughput = null);

/// <summary>The rule's answer for one operation, with the predicates that govern it.</summary>
/// <param name="EstimatedRuntime"><c>T_est</c>, or null when no size was given or the operation is not a job.</param>
public sealed record StrategyDecision(
    SetOperationKind Operation, SelectionStrategy Strategy, StrategyPredicates Predicates, TimeSpan? EstimatedRuntime)
{
    /// <summary>The governing predicates as the ADR-002 §3 table writes them, e.g. "R, A, X"; "none" for a reader.</summary>
    public string Governing => SnapshotStrategyRules.Format(Predicates);
}

/// <summary>
/// The deterministic PIT-vs-materialized rule of baseline §22 as decided by ADR-002 §2–§4 (E10-T03). Pure: the same
/// inputs always give the same answer, and nothing here reads state.
/// <list type="bullet">
/// <item><see cref="Choose(bool, bool, bool, bool)"/> is the 16-row decision table: a reader only when L, R, A and X
/// are all false.</item>
/// <item><see cref="FixedPredicates"/> is the job-type table: R, A and X are properties of the operation; only L depends
/// on the estimated runtime.</item>
/// <item>Exports and productions always set R, A and X, so they are materialized whatever their size (§22).</item>
/// <item>Interactive cursors are not jobs: always a live reader with re-establishment and a "results refreshed" notice
/// (ADR-002 §8, Q-33); they are never materialized in the MVP.</item>
/// </list>
/// </summary>
public static class SnapshotStrategyRules
{
    /// <summary>ADR-002 §3: PIT + <c>search_after</c> iff all four predicates are false; otherwise materialized.</summary>
    public static SelectionStrategy Choose(bool longRunning, bool exactRestart, bool survivesIndexChange, bool legalReproducibility) =>
        longRunning || exactRestart || survivesIndexChange || legalReproducibility ? SelectionStrategy.Materialized : SelectionStrategy.PointInTime;

    public static SelectionStrategy Choose(StrategyPredicates predicates) => Choose(
        predicates.HasFlag(StrategyPredicates.LongRunning),
        predicates.HasFlag(StrategyPredicates.ExactRestart),
        predicates.HasFlag(StrategyPredicates.SurvivesIndexChange),
        predicates.HasFlag(StrategyPredicates.LegalReproducibility));

    /// <summary>ADR-002 §4: R, A and X of each operation (L is computed from the estimate).</summary>
    public static StrategyPredicates FixedPredicates(SetOperationKind operation) => operation switch
    {
        SetOperationKind.BulkCoding => StrategyPredicates.ExactRestart,
        SetOperationKind.ApplyToFamily => StrategyPredicates.ExactRestart,
        SetOperationKind.Export or SetOperationKind.Production =>
            StrategyPredicates.ExactRestart | StrategyPredicates.SurvivesIndexChange | StrategyPredicates.LegalReproducibility,
        SetOperationKind.PrivilegeLog => StrategyPredicates.LegalReproducibility,
        SetOperationKind.SavedSearchTermReport => StrategyPredicates.SurvivesIndexChange | StrategyPredicates.LegalReproducibility,
        SetOperationKind.SearchTermReportPreview => StrategyPredicates.None,
        SetOperationKind.ReviewBatch => StrategyPredicates.ExactRestart | StrategyPredicates.SurvivesIndexChange,
        SetOperationKind.InteractiveCursor => StrategyPredicates.None,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
    };

    /// <summary><c>T_est = T_setup + Count × ExpansionFactor / R_type</c> (ADR-002 §2).</summary>
    public static TimeSpan EstimateRuntime(SetOperationKind operation, SetSizeEstimate estimate, PitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(estimate.Count);
        if (!(estimate.ExpansionFactor >= 1) || !double.IsFinite(estimate.ExpansionFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(estimate), estimate.ExpansionFactor, "The expansion factor is at least 1.");
        }

        var throughput = estimate.Throughput
            ?? (operation == SetOperationKind.SearchTermReportPreview ? policy.TermReportThroughput : policy.IdPagingThroughput);
        if (!(throughput > 0) || !double.IsFinite(throughput))
        {
            throw new ArgumentOutOfRangeException(nameof(estimate), throughput, "The throughput must be positive.");
        }

        var seconds = policy.SetupTime.TotalSeconds + (estimate.Count * estimate.ExpansionFactor / throughput);
        return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }

    /// <summary><b>L</b>: <c>T_est × SafetyFactor &gt; JobMaxAge</c>.</summary>
    public static bool IsLongRunning(TimeSpan estimatedRuntime, PitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return estimatedRuntime.TotalSeconds * policy.SafetyFactor > policy.JobMaxAge.TotalSeconds;
    }

    /// <summary>
    /// Decides the strategy of <paramref name="operation"/>. Without an <paramref name="estimate"/> the runtime is
    /// unknown and L counts as true, so an unsized job is never given a reader it might outlive.
    /// </summary>
    public static StrategyDecision Decide(SetOperationKind operation, SetSizeEstimate? estimate, PitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (operation == SetOperationKind.InteractiveCursor)
        {
            return new StrategyDecision(operation, SelectionStrategy.PointInTime, StrategyPredicates.None, null);
        }

        var predicates = FixedPredicates(operation);
        TimeSpan? runtime = estimate is null ? null : EstimateRuntime(operation, estimate, policy);
        if (runtime is not { } t || IsLongRunning(t, policy))
        {
            predicates |= StrategyPredicates.LongRunning;
        }

        return new StrategyDecision(operation, Choose(predicates), predicates, runtime);
    }

    /// <summary>The operation a snapshot of <paramref name="purpose"/> is frozen for.</summary>
    public static SetOperationKind OperationFor(SnapshotPurpose purpose) => purpose switch
    {
        SnapshotPurpose.BulkCoding => SetOperationKind.BulkCoding,
        SnapshotPurpose.Export => SetOperationKind.Export,
        SnapshotPurpose.Production => SetOperationKind.Production,
        SnapshotPurpose.Report => SetOperationKind.SavedSearchTermReport,
        SnapshotPurpose.ReviewBatch => SetOperationKind.ReviewBatch,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown snapshot purpose."),
    };

    /// <summary>
    /// Whether <paramref name="operation"/> needs a materialized snapshot whatever its size: true when R, A or X holds,
    /// so the decision cannot depend on the estimate.
    /// </summary>
    public static bool AlwaysMaterialized(SetOperationKind operation) =>
        operation != SetOperationKind.InteractiveCursor && FixedPredicates(operation) != StrategyPredicates.None;

    /// <summary>
    /// Whether a job of <paramref name="operation"/> may run over a snapshot frozen for <paramref name="purpose"/>: the
    /// operation must be one the rule always materializes, and the snapshot must have been frozen for it.
    /// </summary>
    public static bool AcceptsSnapshot(SetOperationKind operation, SnapshotPurpose purpose) =>
        AlwaysMaterialized(operation) && Enum.IsDefined(purpose) && OperationFor(purpose) == operation;

    /// <summary>The predicates as written in the ADR-002 §3 table ("L, R, A, X"), or "none".</summary>
    public static string Format(StrategyPredicates predicates)
    {
        var names = new List<string>(4);
        if (predicates.HasFlag(StrategyPredicates.LongRunning))
        {
            names.Add("L");
        }

        if (predicates.HasFlag(StrategyPredicates.ExactRestart))
        {
            names.Add("R");
        }

        if (predicates.HasFlag(StrategyPredicates.SurvivesIndexChange))
        {
            names.Add("A");
        }

        if (predicates.HasFlag(StrategyPredicates.LegalReproducibility))
        {
            names.Add("X");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>The audit form of a decision, e.g. "Materialized (R, A, X; T_est 12.5 s)".</summary>
    public static string Describe(StrategyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var runtime = decision.EstimatedRuntime is { } t
            ? string.Create(CultureInfo.InvariantCulture, $"; T_est {t.TotalSeconds:0.#} s")
            : string.Empty;
        return $"{decision.Strategy} ({decision.Governing}{runtime})";
    }
}
