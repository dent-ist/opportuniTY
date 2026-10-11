using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Opportunity.Application.Defensibility;
using Opportunity.Application.Search.TermReports;
using Opportunity.Core.SearchTermReports;
using Opportunity.Core.Text;
using Opportunity.Production.Exports;

namespace Opportunity.Production.Defensibility;

/// <summary>
/// The defensibility reports as CSV (E14-T05): RFC 4180, UTF-8 with a byte-order mark, UTC times; text a spreadsheet
/// would run as a formula gets a leading apostrophe. Each file opens with the report's statement (workspace, as-of
/// point, scope, generation, fingerprint, audit event) so a printed copy stands on its own, then its tables separated
/// by blank lines.
/// </summary>
public static class DefensibilityCsv
{
    public const string ContentType = "text/csv; charset=utf-8";

    public static string FileName(DefensibilityStatement statement, string? suffix = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var name = statement.Report switch
        {
            DefensibilityReportKind.ReviewSummary => "review-summary",
            DefensibilityReportKind.ProductionHistory => "production-history",
            DefensibilityReportKind.SearchTermHits => "search-term-hits",
            DefensibilityReportKind.ChainOfCustody => "chain-of-custody",
            _ => "defensibility-pack",
        };
        return name + (suffix is null ? string.Empty : "-" + suffix) + "-asof-" + Stamp(statement.AsOf) + ".csv";
    }

    public static byte[] ReviewSummary(ReviewSummaryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = Statement(report.Statement, "Review population and coding summary",
            report.Snapshot is { } s ? $"Snapshot: {s.Name} ({s.SnapshotId:D}, {N(s.Documents)} documents)" : "Workspace: every document held at the as-of point");
        var population = report.Data.Population;
        text.Blank();
        text.Row("Documents in scope", N(population.Documents));
        text.Row("Reviewed (coded by a person)", N(population.Reviewed));
        text.Row("Not reviewed", N(population.NotReviewed));
        text.Row("Coded by system rules only", N(population.SystemCodedOnly));
        text.Row("Bulk edits skipped for concurrent edits", N(population.BulkSkips));
        text.Row("Counts", "Every coding change up to the as-of point is counted, whoever can see the document now; reviewers are named by user ID.");
        text.Blank();
        text.Row("Reviewer ID", "Reviewer", "Documents coded", "Field changes", "Interactive", "Bulk", "System rule", "First coding (UTC)",
            "Last coding (UTC)");
        foreach (var r in report.Data.Reviewers)
        {
            text.Row(r.ActorId.ToString("D"), r.DisplayName, N(r.Documents), N(r.Changes), N(r.InteractiveChanges), N(r.BulkChanges),
                N(r.SystemChanges), Time(r.FirstAt), Time(r.LastAt));
        }

        text.Blank();
        text.Row("Date (UTC)", "Reviewer ID", "Reviewer", "Documents coded", "Field changes", "Interactive", "Bulk", "System rule");
        foreach (var d in report.Data.Days)
        {
            text.Row(d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.ActorId.ToString("D"), d.DisplayName, N(d.Documents),
                N(d.Changes), N(d.InteractiveChanges), N(d.BulkChanges), N(d.SystemChanges));
        }

        if (report.Data.DaysTruncated)
        {
            text.Row("Note", $"Only the first {N(report.Data.Days.Count)} reviewer days are listed; the totals above include all.");
        }

        return text.Bytes();
    }

    public static byte[] ProductionHistory(ProductionHistoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = Statement(report.Statement, "Production history", "Productions finalized by the as-of point");
        text.Blank();
        text.Row("Production ID", "Production", "Version", "Status", "Snapshot ID", "Finalized (UTC)", "Finalized by", "Voided (UTC)",
            "Void reason", "First Bates", "Last Bates", "Documents", "Pages", "Specification SHA-256", "Bates assignments SHA-256",
            "Manifest SHA-256", "QC outcome", "QC run (UTC)", "QC report SHA-256", "QC checks", "Volumes");
        foreach (var p in report.Productions)
        {
            text.Row(p.ProductionId.ToString("D"), p.Name, N(p.Version), p.Voided ? "Voided" : "Finalized", p.SnapshotId.ToString("D"),
                Time(p.FinalizedAt), p.FinalizedByDisplay ?? p.FinalizedBy.ToString("D"), Time(p.VoidedAt), p.VoidReason, p.BatesBegin, p.BatesEnd,
                N(p.Documents), N(p.Units), Hex(p.SpecificationSha256), Hex(p.AssignmentsSha256), Hex(p.ManifestSha256), p.Qc?.Outcome,
                Time(p.Qc?.RunAt), Hex(p.Qc?.ReportSha256), p.Qc is { } qc ? QcChecks(qc) : "No QC run recorded", N(p.Volumes.Count));
        }

        text.Blank();
        text.Row("Production ID", "Volume ID", "Volume", "Completed (UTC)", "Documents", "Pages", "Files", "Bytes", "Volume manifest SHA-256",
            "Burn-in verification", "Verified documents", "Verification report SHA-256");
        foreach (var p in report.Productions)
        {
            foreach (var v in p.Volumes)
            {
                text.Row(p.ProductionId.ToString("D"), v.VolumeId.ToString("D"), VolumeName(v.SettingsJson), Time(v.CompletedAt), N(v.Documents),
                    N(v.Pages), N(v.Files), N(v.Bytes), Hex(v.ManifestSha256),
                    v.VerificationPassed switch { true => "Passed", false => "Failed", null => "Not recorded" },
                    v.VerifiedDocuments is { } n ? N(n) : null, Hex(v.VerificationReportSha256));
            }
        }

        return text.Bytes();
    }

    public static byte[] SearchTermHits(SearchTermHitsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = Statement(report.Statement, "Search term hit reports", "Search term reports whose current run completed by the as-of point");
        text.Row("Counts", "Each report's counts are its executor's, over its frozen snapshot at its search generation; a rerun replaces them.");
        if (report.Truncated)
        {
            text.Row("Note", $"Only the first {N(report.Reports.Count)} reports are listed.");
        }

        text.Blank();
        text.Row("Report ID", "Report", "Run", "Scope", "Snapshot ID", "Search generation", "Index current during run",
            "Changes not yet searchable at execution", "Index lag at execution (s)", "Executed by", "Executed (UTC)", "Completed (UTC)", "Term",
            "Expression", "Documents with hits", "Documents with hits, including family", "Unique hits", "Unique hits, including family",
            "Documents in scope", "Documents without hits", "Error");
        foreach (var entry in report.Reports)
        {
            var r = entry.Report;
            string?[] head =
            [
                r.ReportId.ToString("D"), r.Name, N(r.RunCount), Scope(r), r.SnapshotId?.ToString("D"), r.SearchGeneration is { } g ? N(g) : null,
                r.IndexCurrent switch { true => "Yes", false => "No", null => null }, r.IndexPendingChanges is { } p ? N(p) : null,
                r.IndexLag is { } lag ? Seconds(lag) : null, r.ExecutedByDisplay, Time(r.ExecutedAt), Time(r.CompletedAt),
            ];
            foreach (var t in entry.Terms)
            {
                text.Row([.. head, t.Name, t.Expression, Opt(t.DocumentsWithHits), Opt(t.DocumentsWithHitsIncludingFamily), Opt(t.UniqueHits),
                    Opt(t.UniqueHitsIncludingFamily), null, null, t.Error is { } e ? $"{e.Code}: {e.Message}" : null]);
            }

            text.Row([.. head, "(all terms)", null, Opt(r.DocumentsWithHits), Opt(r.DocumentsWithHitsIncludingFamily), null, null,
                Opt(r.DocumentsInScope), Opt(r.DocumentsWithoutHits), null]);
        }

        return text.Bytes();
    }

    public static byte[] ChainOfCustody(ChainOfCustodyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var d = report.Data;
        var text = Statement(report.Statement, "Chain of custody", $"Document {d.ControlNumber} ({d.DocumentId:D})");
        text.Blank();
        text.Row("Control number", d.ControlNumber);
        text.Row("Document ID", d.DocumentId.ToString("D"));
        text.Row("File name", d.FileName);
        text.Row("File size (bytes)", d.FileSize is { } size ? N(size) : null);
        text.Row("Created (UTC)", Time(d.CreatedAt));
        text.Row("Native SHA-256 at import", Hex(d.Sha256));
        text.Row("Native SHA-1 at import", Hex(d.Sha1));
        text.Row("Native MD5 at import", Hex(d.Md5));
        text.Row("Upstream hash (load file)", d.UpstreamHash);
        text.Row("Re-verified", $"{N(report.Verified)} object(s) re-read from storage; {N(report.Mismatches)} did not match.");
        if (d.ObjectsTruncated)
        {
            text.Row("Note", $"Only the first {N(d.Objects.Count)} stored objects are listed.");
        }

        text.Blank();
        text.Row("Import ID", "Import", "Mode", "Load file", "Load file SHA-256 at upload", "Load file SHA-256 re-verified", "Verification",
            "Row", "Action", "Started (UTC)", "Completed (UTC)", "Started by", "Job ID");
        foreach (var i in report.Imports)
        {
            var import = i.Import;
            text.Row(import.ImportId.ToString("D"), import.Name, import.Mode, import.LoadFileName, Hex(import.LoadFileSha256), Hex(i.Verification.Sha256),
                i.Verification.Status.ToString(), N(import.RowNumber), import.Action, Time(import.StartedAt), Time(import.CompletedAt),
                import.StartedByDisplay ?? import.StartedBy.ToString("D"), import.JobId.ToString("D"));
        }

        text.Blank();
        text.Row("Object ID", "Area", "Current", "Page set ID", "Page", "Size (bytes)", "Content type", "Stored (UTC)", "Job ID",
            "SHA-256 recorded at import", "SHA-256 re-verified from storage", "Verification", "Verified (UTC)");
        foreach (var o in report.Objects)
        {
            var x = o.Stored;
            text.Row(x.ObjectId.ToString("D"), x.Area.ToString(), x.Current ? "Yes" : "No", x.PageSetId?.ToString("D"),
                x.PageOrdinal is { } page ? N(page) : null, N(x.SizeBytes), x.ContentType, Time(x.CreatedAt), x.CreatedByJobId?.ToString("D"),
                Hex(x.Sha256), Hex(o.Verification.Sha256), o.Verification.Status.ToString(), Time(o.Verification.VerifiedAt));
        }

        text.Blank();
        text.Row("Page set ID", "Source", "Import job ID", "Renderer", "Renderer version", "Render settings SHA-256", "Pages", "Status",
            "Created (UTC)", "Active");
        foreach (var p in d.PageSets)
        {
            text.Row(p.PageSetId.ToString("D"), p.Source, p.ImportJobId?.ToString("D"), p.Renderer, p.RendererVersion, Hex(p.RenderSettingsSha256),
                N(p.Pages), p.Status, Time(p.CreatedAt), p.Active ? "Yes" : "No");
        }

        text.Blank();
        text.Row("Overlay import ID", "Row", "Document version", "Overlaid (UTC)", "Changed");
        foreach (var o in d.Overlays)
        {
            text.Row(o.ImportId.ToString("D"), N(o.RowNumber), N(o.DocumentVersion), Time(o.OccurredAt), string.Join("; ", o.Changed));
        }

        text.Blank();
        text.Row("Production ID", "Production", "Version", "Finalized (UTC)", "Voided (UTC)", "Output", "ProdBegBates", "ProdEndBates", "Volume IDs");
        foreach (var p in d.Productions)
        {
            text.Row(p.ProductionId.ToString("D"), p.Name, N(p.Version), Time(p.FinalizedAt), Time(p.VoidedAt), p.Output, p.BegBates, p.EndBates,
                string.Join("; ", p.Volumes.Select(v => v.ToString("D"))));
        }

        return text.Bytes();
    }

    /// <summary>The volume name a volume run's frozen export settings give it; null when they cannot be read.</summary>
    public static string? VolumeName(string settingsJson)
    {
        try
        {
            return ExportSettings.Deserialize(settingsJson).VolumeName;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    internal static string QcChecks(ProductionQcHistory qc) =>
        string.Join("; ", qc.Checks.Where(c => c.Status != "passed").Select(c => $"{c.Check}: {c.Status} ({N(c.Documents)})")) is { Length: > 0 } s
            ? s
            : "All checks passed";

    public static string Scope(SearchTermReportRecord report) => report.ScopeKind switch
    {
        SearchTermReportScopeKind.SavedSearch => "Saved search: " + (report.ScopeName ?? report.ScopeId?.ToString()),
        SearchTermReportScopeKind.Snapshot => "Snapshot: " + (report.ScopeName ?? report.ScopeId?.ToString()),
        _ => "Workspace",
    };

    internal static string? Hex(byte[]? value) => value is null ? null : Convert.ToHexStringLower(value);

    internal static string? Time(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    internal static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    internal static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Seconds(TimeSpan value) => Math.Round(value.TotalSeconds, 3).ToString(CultureInfo.InvariantCulture);

    private static string? Opt(long? value) => value is { } v ? N(v) : null;

    private static CsvText Statement(DefensibilityStatement statement, string title, string scope)
    {
        var text = new CsvText();
        text.Row("Report", title);
        text.Row("Workspace", statement.WorkspaceId.ToString("D"));
        text.Row("As of (UTC)", Time(statement.AsOf), statement.AsOf.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        text.Row("Scope", scope);
        text.Row("Snapshot ID", statement.SnapshotId?.ToString("D"));
        text.Row("Search generation", statement.SearchGeneration is { } g ? N(g) : "Not recorded (report as of an earlier point)");
        text.Row("Generated (UTC)", Time(statement.GeneratedAt));
        text.Row("Generated by", statement.GeneratedBy);
        text.Row("Fingerprint (SHA-256)", statement.Fingerprint);
        text.Row("Audit event", statement.AuditEventId.ToString("D"));
        text.Row("Reproduce", "Generate the report again with this as-of value: the same point gives the same fingerprint.");
        return text;
    }

    private sealed class CsvText
    {
        private readonly StringBuilder _text = new();

        public void Row(params string?[] cells) => _text.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

        public void Blank() => _text.Append("\r\n");

        public byte[] Bytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_text.ToString())];
    }

    private static string Cell(string? value)
    {
        var v = value ?? string.Empty;
        v = FormulaNeutralizer.Neutralize(v);

        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}

/// <summary>
/// The canonical as-of content of each report, hashed into its fingerprint (SHA-256, lower-case hex): one line per fact,
/// tab-separated invariant values (GUIDs "D", UTC times "O", hashes hex). Display names and re-verification results are
/// left out because they are not as of the point.
/// </summary>
public static class DefensibilityFingerprint
{
    public static string ReviewSummary(Guid workspaceId, DateTimeOffset asOf, Guid? snapshotId, ReviewSummaryData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var c = new Canonical("reviewSummary", workspaceId, asOf);
        var p = data.Population;
        c.Line("scope", snapshotId?.ToString("D") ?? "workspace");
        c.Line("population", p.Documents, p.Reviewed, p.NotReviewed, p.SystemCodedOnly, p.BulkSkips);
        foreach (var r in data.Reviewers)
        {
            c.Line("reviewer", r.ActorId, r.Documents, r.Changes, r.InteractiveChanges, r.BulkChanges, r.SystemChanges, r.FirstAt, r.LastAt);
        }

        foreach (var d in data.Days)
        {
            c.Line("day", d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.ActorId, d.Documents, d.Changes, d.InteractiveChanges,
                d.BulkChanges, d.SystemChanges);
        }

        return c.Hash();
    }

    public static string ProductionHistory(Guid workspaceId, DateTimeOffset asOf, IReadOnlyList<ProductionHistoryEntry> productions)
    {
        ArgumentNullException.ThrowIfNull(productions);
        var c = new Canonical("productionHistory", workspaceId, asOf);
        foreach (var p in productions)
        {
            c.Line("production", p.ProductionId, p.LineageId, p.Version, p.Name, p.SnapshotId, p.FinalizedAt, p.FinalizedBy, p.VoidedAt, p.VoidReason,
                p.BatesBegin, p.BatesEnd, p.Documents, p.Units, p.SpecificationSha256, p.AssignmentsSha256, p.ManifestSha256);
            if (p.Qc is { } qc)
            {
                c.Line("qc", qc.QcRunId, qc.RunAt, qc.Outcome, qc.ReportSha256);
            }

            foreach (var v in p.Volumes)
            {
                c.Line("volume", v.VolumeId, v.CompletedAt, v.Documents, v.Pages, v.Files, v.Bytes, v.ManifestSha256, v.VerificationPassed,
                    v.VerifiedDocuments, v.VerificationReportSha256);
            }
        }

        return c.Hash();
    }

    public static string SearchTermHits(Guid workspaceId, DateTimeOffset asOf, IReadOnlyList<TermHitReportEntry> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var c = new Canonical("searchTermHits", workspaceId, asOf);
        foreach (var e in reports)
        {
            var r = e.Report;
            c.Line("report", r.ReportId, r.RunCount, r.JobId, r.ScopeKind.ToString(), r.ScopeId, r.SnapshotId, r.SearchGeneration, r.IndexCurrent,
                r.IndexPendingChanges, r.IndexLag is { } lag ? (long)lag.TotalMilliseconds : null, r.ExecutedBy, r.ExecutedAt, r.CompletedAt,
                r.DocumentsInScope, r.DocumentsWithHits, r.DocumentsWithHitsIncludingFamily, r.DocumentsWithoutHits, r.ExcludedNoAccess);
            foreach (var t in e.Terms)
            {
                c.Line("term", t.TermNo, t.TermId, t.Name, t.Expression, t.Error?.Code, t.DocumentsWithHits, t.DocumentsWithHitsIncludingFamily,
                    t.UniqueHits, t.UniqueHitsIncludingFamily);
            }
        }

        return c.Hash();
    }

    public static string ChainOfCustody(Guid workspaceId, DateTimeOffset asOf, ChainOfCustodyData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var c = new Canonical("chainOfCustody", workspaceId, asOf);
        c.Line("document", data.DocumentId, data.ControlNumber, data.CreatedAt, data.Md5, data.Sha1, data.Sha256, data.UpstreamHash);
        foreach (var i in data.Imports)
        {
            c.Line("import", i.ImportId, i.JobId, i.Mode, i.LoadFileName, i.LoadFileSha256, i.LoadFileSize, i.RowNumber, i.Action, i.StartedBy,
                i.StartedAt, i.CompletedAt);
        }

        foreach (var o in data.Objects)
        {
            c.Line("object", o.ObjectId, o.Area.ToString(), o.Sha256, o.SizeBytes, o.ContentType, o.CreatedAt, o.CreatedByJobId, o.PageSetId, o.PageOrdinal);
        }

        foreach (var p in data.PageSets)
        {
            c.Line("pageSet", p.PageSetId, p.Source, p.ImportJobId, p.Renderer, p.RendererVersion, p.RenderSettingsSha256, p.CreatedAt);
        }

        foreach (var o in data.Overlays)
        {
            c.Line("overlay", o.ImportId, o.RowNumber, o.DocumentVersion, o.OccurredAt, string.Join(";", o.Changed));
        }

        foreach (var p in data.Productions)
        {
            c.Line("production", p.ProductionId, p.Version, p.FinalizedAt, p.VoidedAt, p.Output, p.BegBates, p.EndBates,
                string.Join(";", p.Volumes.Select(v => v.ToString("D"))));
        }

        return c.Hash();
    }

    /// <summary>The fingerprint of a pack: its point and the fingerprints of the reports it holds, in order.</summary>
    public static string Pack(Guid workspaceId, DateTimeOffset asOf, IEnumerable<string> fingerprints)
    {
        var c = new Canonical("pack", workspaceId, asOf);
        foreach (var f in fingerprints)
        {
            c.Line("report", f);
        }

        return c.Hash();
    }

    private sealed class Canonical
    {
        private readonly StringBuilder _text = new();

        public Canonical(string report, Guid workspaceId, DateTimeOffset asOf) => Line("opportunity.defensibility/1", report, workspaceId, asOf);

        public void Line(params object?[] values) => _text.AppendJoin('\t', values.Select(Value)).Append('\n');

        public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_text.ToString())));

        private static string Value(object? value) => value switch
        {
            null => string.Empty,
            string s => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal),
            Guid g => g.ToString("D"),
            DateTimeOffset t => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            byte[] b => Convert.ToHexStringLower(b),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
