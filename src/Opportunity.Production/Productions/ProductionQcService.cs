using System.Globalization;
using System.Text;

using Opportunity.Application.Audit;
using Opportunity.Application.Authorization;
using Opportunity.Application.Productions;
using Opportunity.Contracts.Api;
using Opportunity.Core.Productions;
using Opportunity.Core.Security;
using Opportunity.Production.Exports;

namespace Opportunity.Production.Productions;

/// <summary>How a finalization answers the QC gate (E12-T07): overrides of failed checks and the warning acknowledgement.</summary>
public sealed record ProductionFinalizeOptions(IReadOnlyList<ProductionQcOverride> Overrides, bool AcknowledgeWarnings)
{
    public static ProductionFinalizeOptions None { get; } = new([], false);
}

/// <summary>
/// The production QC gate's use cases (E12-T07): running it on request, reading the last run, its document-level
/// exceptions and the QC report (CSV and PDF). Members the caller may not view are never listed; they are counted, as
/// the Bates lookup does. A finalized production keeps the run of its finalization, which its manifest names.
/// </summary>
public sealed partial class ProductionService
{
    /// <summary>Exception rows printed per check in the PDF report; the CSV lists every one.</summary>
    public const int PdfExceptionsPerCheck = 500;

    private const int QcPage = 2_000;

    /// <summary>Runs the QC gate over an allocated draft now (purpose Check) and stores the run; audited <c>Production.QcRun</c>.</summary>
    public async Task<ProductionOutcome> RunQcAsync(SecurityPrincipal principal, Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (await productions.GetAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.NotFound);
        }

        if (current.Status != ProductionStatus.Draft || current.BatesState != BatesAllocationState.Allocated)
        {
            return new ProductionOutcome(ProductionOutcomeStatus.Conflict, current,
                Reason: current.Status == ProductionStatus.Draft
                    ? "Allocate Bates numbers before running the QC checks."
                    : "The production is not a draft; its finalization's QC report is kept with it.");
        }

        var qc = await QcRequestAsync(principal, current, ProductionQcPurpose.Check, [], false, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var result = await productions.RunQcAsync(workspaceId, productionId, qc,
            UserEvent(principal, AuditTaxonomy.Production.QcRun, new Dictionary<string, string?>()), cancellationToken).ConfigureAwait(false);
        return Outcome(result, null) with { Qc = result.Qc };
    }

    /// <summary>The production's latest QC run (a finalized production's: the run it was finalized with).</summary>
    public async Task<ProductionQcResult?> GetQcAsync(Guid workspaceId, Guid productionId, CancellationToken cancellationToken = default) =>
        await productions.GetLatestQcRunAsync(workspaceId, productionId, cancellationToken).ConfigureAwait(false) is { } run
            ? ProductionQcRules.Read(run)
            : null;

    /// <summary>
    /// A page of a run's exceptions the caller may view, after <paramref name="after"/>; the others in the scanned range
    /// are counted. <c>Next</c> is where the next page starts (null at the end).
    /// </summary>
    public async Task<(IReadOnlyList<ProductionQcExceptionRow> Rows, long Restricted, ProductionQcExceptionCursor? Next)> ListQcExceptionsAsync(
        SecurityPrincipal principal, Guid workspaceId, Guid qcRunId, ProductionQcCheck? check, ProductionQcExceptionCursor? after, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var rows = await productions.ReadQcExceptionsAsync(workspaceId, qcRunId, check, after, limit, cancellationToken).ConfigureAwait(false);
        var visible = await VisibleAsync(principal, workspaceId, rows.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
        var next = rows.Count == limit ? new ProductionQcExceptionCursor((short)rows[^1].Check, rows[^1].Sequence) : (ProductionQcExceptionCursor?)null;
        return ([.. rows.Where(r => visible.Contains(r.DocumentId))], rows.Count(r => !visible.Contains(r.DocumentId)), next);
    }

    /// <summary>The audit event of a QC report download (written before the first byte).</summary>
    public AuditEvent QcReportExportAudit(SecurityPrincipal principal, ProductionRecord production, ProductionQcResult qc, string format)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(qc);
        return Resource(UserEvent(principal, AuditTaxonomy.Production.Downloaded, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = production.ProductionId.ToString(),
            ["File"] = "QcReport",
            ["Format"] = format,
            ["QcRunId"] = qc.QcRunId.ToString(),
            ["QcReportSha256"] = Convert.ToHexStringLower(qc.ReportSha256),
        }), production);
    }

    /// <summary>The QC report's file name: <c>&lt;production name&gt;_v&lt;version&gt;_qc-report.&lt;extension&gt;</c>.</summary>
    public static string QcReportFileName(ProductionRecord production, string extension)
    {
        ArgumentNullException.ThrowIfNull(production);
        var safe = new string([.. production.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]).Trim('_', '.');
        return string.Create(CultureInfo.InvariantCulture,
            $"{(safe.Length > 0 ? safe[..Math.Min(safe.Length, 80)] : "production")}_v{production.Version}_qc-report.{extension}");
    }

    /// <summary>
    /// The QC report as CSV (UTF-8 with byte-order mark, RFC 4180, formula-neutralized): the production and run, every
    /// check with its status, document count and override reason, then every exception the caller may view (check, then
    /// production order), then per check the count of exceptions left out because the caller may not view them.
    /// </summary>
    public async Task WriteQcReportCsvAsync(
        SecurityPrincipal principal, ProductionRecord production, ProductionQcResult qc, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(qc);
        ArgumentNullException.ThrowIfNull(output);
        var text = new StringBuilder();
        text.Append(LoadFileText.CsvRow("Section", "Item", "Severity", "Status", "Documents", "ProdBegBates", "ProdEndBates", "ControlNumber", "Detail"));
        foreach (var (item, value) in RunFacts(production, qc))
        {
            text.Append(LoadFileText.CsvRow("Production", item, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, value));
        }

        foreach (var check in qc.Checks)
        {
            text.Append(LoadFileText.CsvRow("Check", check.Definition.Key, Severity(check), Status(check), Invariant(check.Documents), string.Empty,
                string.Empty, string.Empty, check.OverrideReason is { } reason ? "Override: " + reason : check.Definition.Title));
        }

        await output.WriteAsync(Encoding.UTF8.GetPreamble(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), cancellationToken).ConfigureAwait(false);
        var restricted = await ScanExceptionsAsync(principal, production.WorkspaceId, qc, async (row, check) =>
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes(LoadFileText.CsvRow("Exception", check.Definition.Key, Severity(check), Status(check), string.Empty,
                row.ProdBegBates ?? string.Empty, row.ProdEndBates ?? string.Empty, row.ControlNumber ?? string.Empty, row.Detail ?? string.Empty)),
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        var tail = new StringBuilder();
        foreach (var (check, count) in restricted.Where(r => r.Value > 0).OrderBy(r => r.Key))
        {
            var definition = ProductionQcRules.Definition(check);
            tail.Append(LoadFileText.CsvRow("NotListed", definition.Key, string.Empty, string.Empty, Invariant(count), string.Empty, string.Empty,
                string.Empty, "Documents you may not view are counted, not listed."));
        }

        await output.WriteAsync(Encoding.UTF8.GetBytes(tail.ToString()), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The QC report as PDF: the production and run, every check with its status, count, remedy or override reason, and
    /// the exceptions the caller may view (at most <see cref="PdfExceptionsPerCheck"/> per check; the CSV lists all).
    /// </summary>
    public async Task WriteQcReportPdfAsync(
        SecurityPrincipal principal, ProductionRecord production, ProductionQcResult qc, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(qc);
        ArgumentNullException.ThrowIfNull(output);
        var pdf = new PdfTextDocument { Footer = "opportuniTY production QC report · " + production.Name };
        pdf.Add("Production QC report", 16, bold: true);
        pdf.Space();
        foreach (var (item, value) in RunFacts(production, qc))
        {
            pdf.Add(item + ": " + value);
        }

        pdf.Space(10);
        pdf.Add("Checks", 12, bold: true);
        foreach (var check in qc.Checks)
        {
            pdf.Add(string.Create(CultureInfo.InvariantCulture,
                $"[{Status(check).ToUpperInvariant()}] {check.Definition.Title} ({Severity(check)}): {check.Documents} document(s)"), bold: check.Status != ProductionQcStatus.Passed);
            if (check.OverrideReason is { } reason)
            {
                pdf.Add("Overridden with the reason: " + reason, indent: 14);
            }
            else if (check.Status is ProductionQcStatus.Failed or ProductionQcStatus.Warning)
            {
                pdf.Add(check.Definition.Remedy, indent: 14);
            }
        }

        var printed = new Dictionary<ProductionQcCheck, int>();
        var omitted = new Dictionary<ProductionQcCheck, long>();
        var exceptions = new List<(ProductionQcCheckResult Check, ProductionQcExceptionRow Row)>();
        var restricted = await ScanExceptionsAsync(principal, production.WorkspaceId, qc, (row, check) =>
        {
            var key = check.Definition.Check;
            if (printed.GetValueOrDefault(key) < PdfExceptionsPerCheck)
            {
                printed[key] = printed.GetValueOrDefault(key) + 1;
                exceptions.Add((check, row));
            }
            else
            {
                omitted[key] = omitted.GetValueOrDefault(key) + 1;
            }

            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        pdf.Space(10);
        pdf.Add("Exceptions", 12, bold: true);
        if (exceptions.Count == 0 && restricted.Values.All(v => v == 0))
        {
            pdf.Add("No document-level exceptions.");
        }

        foreach (var group in qc.Checks.Where(c => c.Documents > 0))
        {
            var key = group.Definition.Check;
            pdf.Space();
            pdf.Add(string.Create(CultureInfo.InvariantCulture, $"{group.Definition.Title} ({Status(group)}, {group.Documents} document(s))"), 10, bold: true);
            foreach (var (_, row) in exceptions.Where(e => e.Check.Definition.Check == key))
            {
                var range = row.ProdBegBates is null ? string.Empty : row.ProdBegBates == row.ProdEndBates ? row.ProdBegBates : row.ProdBegBates + "–" + row.ProdEndBates;
                pdf.Add(string.Join("   ", new[] { range, row.ControlNumber ?? string.Empty, row.Detail ?? string.Empty }.Where(v => v.Length > 0)), 8, indent: 14);
            }

            if (omitted.GetValueOrDefault(key) is var more and > 0)
            {
                pdf.Add(string.Create(CultureInfo.InvariantCulture, $"… and {more} more (see the CSV report)."), 8, indent: 14);
            }

            if (restricted.GetValueOrDefault(key) is var hidden and > 0)
            {
                pdf.Add(string.Create(CultureInfo.InvariantCulture, $"{hidden} document(s) you may not view are counted, not listed."), 8, indent: 14);
            }
        }

        pdf.WriteTo(output);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates finalization overrides: known, overridable checks, each once, with a reason of 1–2,000 characters.</summary>
    private static (IReadOnlyList<ProductionQcOverride> Overrides, ProductionOutcome? Invalid) ValidateOverrides(IReadOnlyList<ProductionQcOverride>? overrides)
    {
        var valid = new List<ProductionQcOverride>();
        var list = overrides ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"qcOverrides[{i}]");
            var item = list[i];
            if (item is null || !Enum.IsDefined(item.Check))
            {
                return ([], ProductionOutcome.Invalid(key + ".check", "Name a QC check."));
            }

            var definition = ProductionQcRules.Definition(item.Check);
            var reason = item.Reason?.Trim() ?? string.Empty;
            if (definition.Check == ProductionQcCheck.PrivilegeConflicts && list.Count == 1 && reason.Length is 0 or > ProductionQcRules.MaxOverrideReasonLength)
            {
                // The E13-T02 request member keeps its own error key.
                return ([], ProductionOutcome.Invalid("privilegeConflictOverride.reason",
                    $"Give the reason for overriding the privilege conflicts (1 to {ProductionQcRules.MaxOverrideReasonLength} characters)."));
            }

            if (!definition.Overridable)
            {
                return ([], ProductionOutcome.Invalid(key + ".check", $"The check '{definition.Key}' cannot be overridden; {definition.Remedy}"));
            }

            if (reason.Length is 0 or > ProductionQcRules.MaxOverrideReasonLength)
            {
                return ([], ProductionOutcome.Invalid(key + ".reason",
                    $"Give the reason for overriding '{definition.Key}' (1 to {ProductionQcRules.MaxOverrideReasonLength} characters)."));
            }

            if (valid.Any(v => v.Check == item.Check))
            {
                return ([], ProductionOutcome.Invalid(key + ".check", $"The check '{definition.Key}' is overridden twice."));
            }

            valid.Add(new ProductionQcOverride(item.Check, reason));
        }

        return (valid, null);
    }

    /// <summary>
    /// The gate's request: the members the caller fails the per-document authorization re-check for (Q-15: the right to
    /// produce each document, as a volume run re-checks its initiator), and what the specification decides.
    /// </summary>
    private async Task<ProductionQcRequest> QcRequestAsync(
        SecurityPrincipal principal, ProductionRecord production, ProductionQcPurpose purpose, IReadOnlyList<ProductionQcOverride> overrides,
        bool acknowledgeWarnings, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var denied = new List<ProductionQcMember>();
        long after = 0;
        while (true)
        {
            var page = await productions.ReadDocumentsAsync(production.WorkspaceId, production.ProductionId, after, QcPage, cancellationToken).ConfigureAwait(false);
            if (page.Count > 0)
            {
                var decisions = await authorization.AuthorizeManyAsync(principal, production.WorkspaceId, Permission.ProductionCreate,
                    [.. page.Select(m => m.DocumentId)], DenialAudit.Summary, cancellationToken).ConfigureAwait(false);
                denied.AddRange(page.Where(m => !decisions.TryGetValue(m.DocumentId, out var d) || !d.IsAllowed)
                    .Select(m => new ProductionQcMember(m.Sequence, m.DocumentId)));
            }

            if (page.Count < QcPage)
            {
                break;
            }

            after = page[^1].Sequence;
        }

        var specification = ProductionSpecificationRules.Deserialize(production.SpecificationJson);
        return new ProductionQcRequest(Guid.CreateVersion7(), purpose, principal.UserId, at, denied, overrides, acknowledgeWarnings, PlanOf(production),
            specification.RedactionSetId, specification.IncludeText);
    }

    /// <summary>
    /// A blocked finalization: the most specific earlier answer when its check failed (withheld members, Bates overlap,
    /// privilege conflicts, designations), else the QC gate's, always with the run.
    /// </summary>
    private static ProductionOutcome Blocked(ProductionWriteResult result, BatesFormat format)
    {
        var qc = result.Qc!;
        bool Failed(ProductionQcCheck check) => qc[check].Status == ProductionQcStatus.Failed;
        var others = qc.Failed.Count() - 1;
        var more = others > 0 ? " Other QC checks failed too; see the QC report." : " See the QC report.";
        if (Failed(ProductionQcCheck.WithheldWithoutPlaceholder))
        {
            return new ProductionOutcome(ProductionOutcomeStatus.PrivilegeWithheld, result.Production, Qc: qc,
                Reason: "Documents in this production are coded Privilege Status = Withhold. Take them out of the frozen set, change their "
                    + "privilege call or produce withheld documents as placeholders, then allocate Bates numbers again." + more);
        }

        if (Failed(ProductionQcCheck.BatesOverlap) && result.Conflicts is { Count: > 0 })
        {
            var overlap = Outcome(result with { Status = ProductionWriteStatus.BatesConflict }, format);
            return overlap with { Reason = overlap.Reason + more, Qc = qc };
        }

        if (Failed(ProductionQcCheck.PrivilegeConflicts))
        {
            return new ProductionOutcome(ProductionOutcomeStatus.PrivilegeConflicts, result.Production, Qc: qc,
                Reason: "Documents in this production have unresolved family or duplicate privilege conflicts. Resolve them (see the privilege "
                    + "conflicts report for this production) or finalize with an override and a reason." + more);
        }

        if (Failed(ProductionQcCheck.DesignationNotProducible))
        {
            return new ProductionOutcome(ProductionOutcomeStatus.DesignationRefused, result.Production, Qc: qc,
                Reason: "Documents in this production carry a confidentiality designation the specification's levels do not list, or are designated "
                    + "while no endorsement stamps {confidentiality}. Update the specification, then finalize." + more);
        }

        var failed = qc.Failed.Select(c => c.Definition.Title).ToList();
        var reason = failed.Count > 0
            ? "The QC checks blocked the finalization: " + string.Join("; ", failed) + ". Fix them, or override those that may be overridden with a reason."
            : "The QC checks found warnings (" + string.Join("; ", qc.Warnings.Select(c => c.Definition.Title))
                + "). Acknowledge them to finalize the production as it is.";
        return new ProductionOutcome(ProductionOutcomeStatus.QcBlocked, result.Production, Reason: reason, Qc: qc);
    }

    /// <summary>
    /// Streams a run's exceptions in report order to <paramref name="visit"/>, only those whose document the caller may
    /// view; returns per check how many were left out.
    /// </summary>
    private async Task<Dictionary<ProductionQcCheck, long>> ScanExceptionsAsync(
        SecurityPrincipal principal, Guid workspaceId, ProductionQcResult qc, Func<ProductionQcExceptionRow, ProductionQcCheckResult, Task> visit,
        CancellationToken cancellationToken)
    {
        var restricted = new Dictionary<ProductionQcCheck, long>();
        ProductionQcExceptionCursor? after = null;
        while (true)
        {
            var rows = await productions.ReadQcExceptionsAsync(workspaceId, qc.QcRunId, null, after, QcPage, cancellationToken).ConfigureAwait(false);
            var visible = await VisibleAsync(principal, workspaceId, rows.Select(r => r.DocumentId), cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (visible.Contains(row.DocumentId))
                {
                    await visit(row, qc[row.Check]).ConfigureAwait(false);
                }
                else
                {
                    restricted[row.Check] = restricted.GetValueOrDefault(row.Check) + 1;
                }
            }

            if (rows.Count < QcPage)
            {
                return restricted;
            }

            after = new ProductionQcExceptionCursor((short)rows[^1].Check, rows[^1].Sequence);
        }
    }

    private static IEnumerable<(string Item, string Value)> RunFacts(ProductionRecord production, ProductionQcResult qc)
    {
        var format = new BatesFormat(production.BatesPrefix, production.BatesPadding, production.BatesSuffix, BatesNumberingLevel.Page);
        yield return ("Production", production.Name);
        yield return ("Version", Invariant(production.Version));
        yield return ("ProductionId", production.ProductionId.ToString("D"));
        yield return ("BatesRange", production.BatesFirst is { } f && production.BatesLast is { } l ? format.Format(f) + "-" + format.Format(l) : string.Empty);
        yield return ("Documents", production.BatesDocuments is { } d ? Invariant(d) : string.Empty);
        yield return ("QcRunId", qc.QcRunId.ToString("D"));
        yield return ("Purpose", qc.Purpose == ProductionQcPurpose.Finalization ? "Finalization" : "Check");
        yield return ("Outcome", qc.Passed ? "Passed" : "Blocked");
        yield return ("RunAt", qc.RunAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        yield return ("RunBy", qc.RunBy.ToString("D"));
        yield return ("WarningsAcknowledged", qc.WarningsAcknowledged ? "Yes" : "No");
        yield return ("ReportSha256", Convert.ToHexStringLower(qc.ReportSha256));
    }

    private static string Severity(ProductionQcCheckResult check) => check.Definition.Severity == ProductionQcSeverity.Blocking ? "Blocking" : "Warning";

    private static string Status(ProductionQcCheckResult check) => check.Status.ToString();

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    /// <summary>The API shape of a run.</summary>
    public static ProductionQcReportResource QcResource(ProductionQcResult qc)
    {
        ArgumentNullException.ThrowIfNull(qc);
        return new ProductionQcReportResource(
            qc.QcRunId,
            qc.ProductionId,
            qc.Purpose == ProductionQcPurpose.Finalization ? ProductionQcPurposeResource.Finalization : ProductionQcPurposeResource.Check,
            qc.Passed ? ProductionQcOutcomeResource.Passed : ProductionQcOutcomeResource.Blocked,
            qc.RunAt,
            qc.RunBy,
            qc.WarningsAcknowledged,
            Convert.ToHexStringLower(qc.ReportSha256),
            [.. qc.Checks.Select(c => new ProductionQcCheckResultResource(
                (ProductionQcCheckResource)(short)c.Definition.Check,
                c.Definition.Title,
                c.Definition.Severity == ProductionQcSeverity.Blocking ? ProductionQcSeverityResource.Blocking : ProductionQcSeverityResource.Warning,
                c.Definition.Overridable,
                Enum.Parse<ProductionQcStatusResource>(c.Status.ToString()),
                c.Documents,
                c.Definition.Remedy,
                c.OverrideReason))]);
    }
}
