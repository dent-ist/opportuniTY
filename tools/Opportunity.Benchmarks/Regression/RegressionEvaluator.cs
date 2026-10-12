namespace Opportunity.Benchmarks.Regression;

/// <summary>One metric of the current run against its rolling baseline.</summary>
public sealed record MetricComparison
{
    public required string Metric { get; init; }

    public required double Current { get; init; }

    /// <summary>Median of the accepted baseline runs; null while the series collects its baseline.</summary>
    public double? Baseline { get; init; }

    public required int BaselineRuns { get; init; }

    /// <summary>Relative change in the "worse" direction: +0.20 = 20% worse than the baseline.</summary>
    public double? Worse { get; init; }

    public required double Tolerance { get; init; }

    public required bool Gated { get; init; }

    public required bool Regressed { get; init; }

    /// <summary>Also regressed on the previous run(s) of the series: alerts.</summary>
    public required bool Sustained { get; init; }
}

/// <summary>The verdict of one run (written as verdict.json).</summary>
public sealed record RegressionVerdict
{
    public required string RunId { get; init; }

    public required string Series { get; init; }

    public required string SeriesLabel { get; init; }

    public required RunStatus Status { get; init; }

    public required bool Alert { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Accepted runs of the series the baseline was taken from (oldest first).</summary>
    public required IReadOnlyList<string> BaselineRunIds { get; init; }

    public string? PreviousRunId { get; init; }

    public required IReadOnlyList<MetricComparison> Comparisons { get; init; }

    public required IReadOnlyDictionary<string, long> Counters { get; init; }

    public required IReadOnlyList<string> CorrectnessFailures { get; init; }

    public bool SustainedCorrectnessFailure { get; init; }

    public EntryVerdict ToEntryVerdict() => new()
    {
        Status = Status,
        Regressed = [.. Comparisons.Where(c => c.Regressed).Select(c => c.Metric)],
        CorrectnessFailures = CorrectnessFailures,
        Alert = Alert,
    };
}

/// <summary>
/// The nightly regression guard (E18-T06): relative to a rolling baseline of the same series, with a tolerance band and an
/// absolute noise floor per metric; alerts on a regression sustained over consecutive runs, and on any correctness failure at once.
/// </summary>
public static class RegressionEvaluator
{
    public static RegressionVerdict Evaluate(HistoryEntry current, IReadOnlyList<HistoryEntry> history, RegressionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(policy);

        var series = history
            .Where(h => h.Series == current.Series && h.StartedUtc < current.StartedUtc && h.RunId != current.RunId && h.Verdict is not null)
            .OrderBy(h => h.StartedUtc)
            .ToList();

        // A rebaseline (on this run or an earlier one) restarts the pool the baseline is drawn from.
        int restart = current.Rebaseline ? series.Count : series.FindLastIndex(h => h.Rebaseline);
        var pool = restart < 0 ? series : series.Skip(restart).ToList();
        var baseline = pool.Where(h => h.Verdict!.Accepted).TakeLast(policy.Baseline.Window).ToList();
        bool collecting = baseline.Count < policy.Baseline.MinimumRuns;

        // "Sustained" looks at the immediately preceding runs of the series, whatever the baseline pool.
        var previous = series.TakeLast(policy.SustainedRuns - 1).ToList();
        bool previousComplete = previous.Count == policy.SustainedRuns - 1;

        var comparisons = new List<MetricComparison>();
        foreach ((string metric, double value) in current.Metrics.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            MetricRule rule = policy.RuleFor(metric);
            double tolerance = rule.Tolerance ?? policy.Tolerance;
            double[] values = [.. baseline.Where(h => h.Metrics.ContainsKey(metric)).Select(h => h.Metrics[metric])];
            double? median = collecting || values.Length < policy.Baseline.MinimumRuns ? null : Median(values);
            double? worse = median is { } m && m > 0 ? (rule.IsHigherBetter ? (m - value) / m : (value - m) / m) : null;
            bool regressed = rule.Gated && worse is { } w && w > tolerance && Math.Abs(value - median!.Value) >= rule.MinAbsoluteChange;
            bool sustained = regressed && previousComplete && previous.All(p => p.Verdict!.Regressed.Contains(metric, StringComparer.Ordinal));
            comparisons.Add(new MetricComparison
            {
                Metric = metric,
                Current = value,
                Baseline = median,
                BaselineRuns = values.Length,
                Worse = worse is { } x ? Math.Round(x, 4) : null,
                Tolerance = tolerance,
                Gated = rule.Gated,
                Regressed = regressed,
                Sustained = sustained,
            });
        }

        var failures = current.Counters.Where(c => c.Value > 0).Select(c => c.Key).Order(StringComparer.Ordinal).ToList();
        bool sustainedCorrectness = failures.Count > 0 && previousComplete && previous.All(p => p.Verdict!.CorrectnessFailures.Count > 0);
        bool regressedAny = comparisons.Any(c => c.Regressed);
        RunStatus status = failures.Count > 0 ? RunStatus.CorrectnessFailed
            : regressedAny ? RunStatus.Regressed
            : collecting ? RunStatus.CollectingBaseline
            : RunStatus.Pass;

        var reasons = new List<string>();
        foreach (MetricComparison c in comparisons.Where(c => c.Regressed))
        {
            reasons.Add(FormattableString.Invariant(
                $"{c.Metric}: {c.Current:0.###} vs baseline {c.Baseline:0.###} ({c.Worse * 100:0.0}% worse, tolerance {c.Tolerance * 100:0.#}%){(c.Sustained ? $", sustained over {policy.SustainedRuns} runs" : string.Empty)}"));
        }

        foreach (string failure in failures)
        {
            reasons.Add(FormattableString.Invariant($"correctness counter {failure} = {current.Counters[failure]}{(sustainedCorrectness ? $", sustained over {policy.SustainedRuns} runs" : string.Empty)}"));
        }

        if (collecting && failures.Count == 0)
        {
            reasons.Add(FormattableString.Invariant($"collecting baseline: {baseline.Count} of {policy.Baseline.MinimumRuns} accepted runs in this series"));
        }

        return new RegressionVerdict
        {
            RunId = current.RunId,
            Series = current.Series,
            SeriesLabel = current.SeriesLabel,
            Status = status,
            // Product decision: a correctness counter above 0 is never noise, so it alerts on the first run;
            // performance regressions alert only when sustained.
            Alert = comparisons.Any(c => c.Sustained) || failures.Count > 0,
            Reasons = reasons,
            BaselineRunIds = [.. baseline.Select(b => b.RunId)],
            PreviousRunId = series.LastOrDefault()?.RunId,
            Comparisons = comparisons,
            Counters = current.Counters,
            CorrectnessFailures = failures,
            SustainedCorrectnessFailure = sustainedCorrectness,
        };
    }

    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("No values.", nameof(values));
        }

        double[] sorted = [.. values.Order()];
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
