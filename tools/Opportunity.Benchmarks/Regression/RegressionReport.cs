using System.Globalization;
using System.Text;

namespace Opportunity.Benchmarks.Regression;

/// <summary>The Markdown summary of a verdict (CI job summary and issue body).</summary>
public static class RegressionReport
{
    public static string Markdown(RegressionVerdict verdict, HistoryEntry run, RegressionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(policy);
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"## Nightly scale regression: {Headline(verdict)}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- Run `{run.RunId}` at `{run.GitSha[..Math.Min(12, run.GitSha.Length)]}`{(run.Url is null ? string.Empty : $" ([workflow run]({run.Url}))")}");
        md.AppendLine(CultureInfo.InvariantCulture, $"- Series `{verdict.Series}`: {verdict.SeriesLabel}{(run.Rebaseline ? " — **new baseline started by this run**" : string.Empty)}");
        md.AppendLine(CultureInfo.InvariantCulture,
            $"- Baseline: median of {verdict.BaselineRunIds.Count} accepted run(s) (window {policy.Baseline.Window}, minimum {policy.Baseline.MinimumRuns}); tolerance {Percent(policy.Tolerance)}; alert when sustained over {policy.SustainedRuns} consecutive runs");
        md.AppendLine();
        if (verdict.Reasons.Count > 0)
        {
            foreach (string reason in verdict.Reasons)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"- {reason}");
            }

            md.AppendLine();
        }

        md.AppendLine("| Gated metric | Current | Baseline | Worse by | |");
        md.AppendLine("|---|---:|---:|---:|---|");
        foreach (MetricComparison c in verdict.Comparisons.Where(c => c.Gated))
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| `{c.Metric}` | {Number(c.Current)} | {(c.Baseline is { } b ? Number(b) : "—")} | {(c.Worse is { } w ? Signed(w) : "—")} | {Mark(c)} |");
        }

        md.AppendLine();
        md.AppendLine("| Correctness counter | Value |");
        md.AppendLine("|---|---:|");
        foreach ((string name, long value) in verdict.Counters.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| `{name}` | {value}{(value > 0 ? " FAIL" : string.Empty)} |");
        }

        md.AppendLine();
        md.AppendLine("<details><summary>All recorded metrics</summary>");
        md.AppendLine();
        md.AppendLine("| Metric | Current | Baseline |");
        md.AppendLine("|---|---:|---:|");
        foreach (MetricComparison c in verdict.Comparisons.Where(c => !c.Gated))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| `{c.Metric}` | {Number(c.Current)} | {(c.Baseline is { } b ? Number(b) : "—")} |");
        }

        md.AppendLine();
        md.AppendLine("</details>");
        md.AppendLine();
        md.AppendLine("Latencies in ms, index lag and stage durations in s, throughput in docs/s. \"Worse by\" is relative to the baseline in the metric's bad direction; negative means better.");
        return md.ToString();
    }

    public static string Headline(RegressionVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return (verdict.Status, verdict.Alert) switch
        {
            (_, true) => "ALERT (sustained regression)",
            (RunStatus.CorrectnessFailed, _) => "correctness failure",
            (RunStatus.Regressed, _) => "regressed (first night, no alert yet)",
            (RunStatus.CollectingBaseline, _) => "collecting baseline",
            _ => "pass",
        };
    }

    private static string Mark(MetricComparison c) => c switch
    {
        { Sustained: true } => "**sustained regression**",
        { Regressed: true } => "regressed",
        { Baseline: null } => "no baseline",
        _ => "ok",
    };

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Signed(double fraction) => (fraction >= 0 ? "+" : string.Empty) + (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Percent(double fraction) => (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
