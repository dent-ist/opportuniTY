using System.Globalization;
using System.Net;
using System.Text;

using Opportunity.Benchmarks.Workloads;

namespace Opportunity.Benchmarks.Regression;

/// <summary>
/// The 90-day trend dashboard (E18-T06): one self-contained HTML page (no scripts, no external assets) with line charts
/// for search p95 by bucket, coding→searchable p95, index lag and bulk/stage throughput of one series, every point
/// hoverable, regressed runs ringed, and a data table under each chart.
/// </summary>
public static class TrendDashboard
{
    private const int Width = 720;
    private const int Height = 240;
    private const int Left = 56;
    private const int Right = 16;
    private const int Top = 12;
    private const int Bottom = 28;
    private const int MaxSeries = 8;

    public static string Render(IReadOnlyList<HistoryEntry> history, string series, DateTime nowUtc, int days)
    {
        ArgumentNullException.ThrowIfNull(history);
        DateTime from = nowUtc.AddDays(-days);
        var runs = history.Where(h => h.Series == series && h.StartedUtc >= from).OrderBy(h => h.StartedUtc).ToList();
        string label = runs.LastOrDefault()?.SeriesLabel ?? series;
        var html = new StringBuilder();
        html.Append(Head);
        html.Append(CultureInfo.InvariantCulture, $"<h1>Nightly scale regression</h1><p class=\"sub\">{E(label)} · last {days} days · {runs.Count} run(s) · generated {nowUtc:yyyy-MM-dd HH:mm} UTC</p>");

        Chart(html, "Search p95 by bucket, simple classes (idle)", "ms", runs, Matching(runs, "search.idle.", ".p95", only: ClassesOf(GateClass.Simple)));
        Chart(html, "Search p95 by bucket, complex classes (idle)", "ms", runs, Matching(runs, "search.idle.", ".p95", only: ClassesOf(GateClass.Complex)));
        Chart(html, "Search p95, simple and complex, idle vs during bulk tagging", "ms", runs,
        [
            .. Matching(runs, "search.idle.", ".p95", only: ["simple", "complex"]).Select(s => s with { Name = "idle " + s.Name }),
            .. Matching(runs, "search.bulk.", ".p95", only: ["simple", "complex"]).Select(s => s with { Name = "bulk " + s.Name }),
        ]);
        Chart(html, "Coding → searchable p95", "ms", runs, Matching(runs, "coding-to-searchable.", ".p95"));
        Chart(html, "Search-index lag p95", "s", runs, Matching(runs, "index-lag.", ".p95"));
        Chart(html, "Throughput by stage", "docs/s", runs, Matching(runs, "stage.", ".docs-per-second", exclude: ["generate"]));

        var others = history.Where(h => h.Series != series && h.StartedUtc >= from).GroupBy(h => h.Series).ToList();
        if (others.Count > 0)
        {
            html.Append("<h2>Other series (not compared)</h2><table><thead><tr><th>Series</th><th>Runs</th><th>Last run</th></tr></thead><tbody>");
            foreach (var other in others)
            {
                html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(other.Last().SeriesLabel)}</td><td class=\"n\">{other.Count()}</td><td>{other.Max(h => h.StartedUtc):yyyy-MM-dd}</td></tr>");
            }

            html.Append("</tbody></table>");
        }

        html.Append("<p class=\"note\">Ringed points are runs judged regressed (kept out of the baseline). Runs are only compared within one series: same runner shape, corpus and scenario version.</p></main></body></html>\n");
        return html.ToString();
    }

    private sealed record Line(string Name, string Metric);

    private static string[] ClassesOf(GateClass gate) => [.. QueryTaxonomy.Classes.Where(c => c.Gate == gate).Select(c => c.Id)];

    private static List<Line> Matching(List<HistoryEntry> runs, string prefix, string suffix, string[]? exclude = null, string[]? only = null) =>
        [.. runs.SelectMany(r => r.Metrics.Keys)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(suffix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(k => new Line(k[prefix.Length..^suffix.Length], k))
            .Where(l => (exclude is null || !exclude.Contains(l.Name)) && (only is null || only.Contains(l.Name)))
            .OrderBy(l => only is null ? 0 : Array.IndexOf(only, l.Name))
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .Take(MaxSeries)];

    private static void Chart(StringBuilder html, string title, string unit, List<HistoryEntry> runs, List<Line> lines)
    {
        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(title)} <span class=\"unit\">({E(unit)})</span></h2>");
        var points = runs.SelectMany(r => lines.Where(l => r.Metrics.ContainsKey(l.Metric)).Select(l => r.Metrics[l.Metric])).ToList();
        if (runs.Count == 0 || lines.Count == 0 || points.Count == 0)
        {
            html.Append("<p class=\"empty\">No data yet.</p></section>");
            return;
        }

        html.Append("<ul class=\"legend\">");
        for (int i = 0; i < lines.Count; i++)
        {
            html.Append(CultureInfo.InvariantCulture, $"<li><span class=\"sw s{i + 1}\"></span>{E(lines[i].Name)}</li>");
        }

        html.Append("</ul>");
        double max = Nice(points.Max());
        DateTime t0 = runs[0].StartedUtc;
        double span = Math.Max(1, (runs[^1].StartedUtc - t0).TotalSeconds);
        double X(DateTime t) => runs.Count == 1 ? Left + ((Width - Left - Right) / 2.0) : Left + ((t - t0).TotalSeconds / span * (Width - Left - Right));
        double Y(double v) => Top + ((Height - Top - Bottom) * (1 - (v / max)));

        html.Append(CultureInfo.InvariantCulture, $"<svg viewBox=\"0 0 {Width} {Height}\" role=\"img\" aria-label=\"{E(title)} over time, in {E(unit)}\">");
        for (int g = 0; g <= 4; g++)
        {
            double v = max * g / 4;
            html.Append(CultureInfo.InvariantCulture, $"<line class=\"grid\" x1=\"{Left}\" x2=\"{Width - Right}\" y1=\"{F(Y(v))}\" y2=\"{F(Y(v))}\"/>");
            html.Append(CultureInfo.InvariantCulture, $"<text class=\"axis\" x=\"{Left - 6}\" y=\"{F(Y(v) + 4)}\" text-anchor=\"end\">{Short(v)}</text>");
        }

        html.Append(CultureInfo.InvariantCulture, $"<text class=\"axis\" x=\"{Left}\" y=\"{Height - 8}\">{runs[0].StartedUtc:MM-dd}</text>");
        html.Append(CultureInfo.InvariantCulture, $"<text class=\"axis\" x=\"{Width - Right}\" y=\"{Height - 8}\" text-anchor=\"end\">{runs[^1].StartedUtc:MM-dd}</text>");
        for (int i = 0; i < lines.Count; i++)
        {
            var series = runs.Where(r => r.Metrics.ContainsKey(lines[i].Metric)).ToList();
            string path = string.Join(' ', series.Select(r => $"{F(X(r.StartedUtc))},{F(Y(r.Metrics[lines[i].Metric]))}"));
            html.Append(CultureInfo.InvariantCulture, $"<polyline class=\"line s{i + 1}\" points=\"{path}\"/>");
            foreach (HistoryEntry r in series)
            {
                bool regressed = r.Verdict?.Regressed.Contains(lines[i].Metric, StringComparer.Ordinal) == true;
                html.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"dot s{i + 1}{(regressed ? " bad" : string.Empty)}\" cx=\"{F(X(r.StartedUtc))}\" cy=\"{F(Y(r.Metrics[lines[i].Metric]))}\" r=\"4\"><title>{E(lines[i].Name)}: {r.Metrics[lines[i].Metric].ToString("0.###", CultureInfo.InvariantCulture)} {E(unit)}\n{r.StartedUtc:yyyy-MM-dd HH:mm} UTC · {E(r.GitSha[..Math.Min(8, r.GitSha.Length)])}{(regressed ? "\nregressed" : string.Empty)}</title></circle>");
            }
        }

        html.Append("</svg>");
        html.Append("<details><summary>Table</summary><table><thead><tr><th>Run (UTC)</th><th>Commit</th>");
        foreach (Line line in lines)
        {
            html.Append(CultureInfo.InvariantCulture, $"<th>{E(line.Name)}</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (HistoryEntry r in Enumerable.Reverse(runs))
        {
            string run = r.Url is null ? $"{r.StartedUtc:yyyy-MM-dd HH:mm}" : $"<a href=\"{E(r.Url)}\">{r.StartedUtc:yyyy-MM-dd HH:mm}</a>";
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{run}</td><td><code>{E(r.GitSha[..Math.Min(8, r.GitSha.Length)])}</code></td>");
            foreach (Line line in lines)
            {
                html.Append(CultureInfo.InvariantCulture, $"<td class=\"n\">{(r.Metrics.TryGetValue(line.Metric, out double v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "—")}</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table></details></section>");
    }

    private static double Nice(double max)
    {
        if (max <= 0)
        {
            return 1;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.First(s => s * magnitude >= max);
        return step * magnitude;
    }

    private static string Short(double v) => v >= 10_000 ? (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string E(string text) => WebUtility.HtmlEncode(text);

    // Categorical slots in fixed order (dataviz reference palette), light and dark steps; text never wears series colours.
    private const string Head = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Scale regression trends</title>
        <style>
        :root { color-scheme: light; --surface: #fcfcfb; --text: #0b0b0b; --text-2: #52514e; --grid: #e4e3df; --ring: #0b0b0b;
          --s1: #2a78d6; --s2: #eb6834; --s3: #1baf7a; --s4: #eda100; --s5: #e87ba4; --s6: #008300; --s7: #6250d6; --s8: #e34948; }
        @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { color-scheme: dark; --surface: #1a1a19; --text: #f0efec; --text-2: #c3c2b7;
          --grid: #33332f; --ring: #f0efec; --s1: #3987e5; --s2: #d95926; --s3: #199e70; --s4: #c98500; --s5: #d55181; --s6: #008300; --s7: #9085e9; --s8: #e66767; } }
        :root[data-theme="dark"] { color-scheme: dark; --surface: #1a1a19; --text: #f0efec; --text-2: #c3c2b7; --grid: #33332f; --ring: #f0efec;
          --s1: #3987e5; --s2: #d95926; --s3: #199e70; --s4: #c98500; --s5: #d55181; --s6: #008300; --s7: #9085e9; --s8: #e66767; }
        body { margin: 0; background: var(--surface); color: var(--text); font: 14px/1.45 system-ui, sans-serif; }
        main { max-width: 760px; margin: 0 auto; padding: 16px; }
        h1 { font-size: 20px; margin: 8px 0 0; } h2 { font-size: 15px; margin: 24px 0 6px; }
        .sub, .note, .empty, .axis, .unit { color: var(--text-2); } .unit { font-weight: normal; } .axis { font-size: 11px; fill: var(--text-2); }
        svg { width: 100%; height: auto; display: block; } .grid { stroke: var(--grid); stroke-width: 1; }
        .line { fill: none; stroke-width: 2; } .dot { stroke: var(--surface); stroke-width: 2; } .dot.bad { stroke: var(--ring); stroke-width: 2.5; }
        .legend { list-style: none; padding: 0; margin: 0 0 4px; display: flex; flex-wrap: wrap; gap: 4px 14px; color: var(--text-2); }
        .sw { display: inline-block; width: 12px; height: 3px; border-radius: 2px; margin-right: 6px; vertical-align: middle; }
        table { border-collapse: collapse; width: 100%; font-size: 12px; overflow-x: auto; display: block; }
        th, td { padding: 3px 8px; border-bottom: 1px solid var(--grid); text-align: left; } td.n { text-align: right; font-variant-numeric: tabular-nums; }
        a { color: inherit; }
        .s1 { stroke: var(--s1); background: var(--s1); fill: var(--s1); } .s2 { stroke: var(--s2); background: var(--s2); fill: var(--s2); }
        .s3 { stroke: var(--s3); background: var(--s3); fill: var(--s3); } .s4 { stroke: var(--s4); background: var(--s4); fill: var(--s4); }
        .s5 { stroke: var(--s5); background: var(--s5); fill: var(--s5); } .s6 { stroke: var(--s6); background: var(--s6); fill: var(--s6); }
        .s7 { stroke: var(--s7); background: var(--s7); fill: var(--s7); } .s8 { stroke: var(--s8); background: var(--s8); fill: var(--s8); }
        .line.s1, .line.s2, .line.s3, .line.s4, .line.s5, .line.s6, .line.s7, .line.s8 { fill: none; }
        .dot.s1, .dot.s2, .dot.s3, .dot.s4, .dot.s5, .dot.s6, .dot.s7, .dot.s8 { stroke: var(--surface); } .dot.bad { stroke: var(--ring); }
        </style></head><body><main>
        """;
}
