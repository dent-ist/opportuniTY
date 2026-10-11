using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Opportunity.Core.Productions;

namespace Opportunity.Application.Productions;

/// <summary>
/// The checks of the production QC gate (E12-T07). Stored as smallint in <c>production_qc_exception.check_code</c>;
/// values are fixed forever. The order of <see cref="ProductionQcRules.Checks"/> is the report's order.
/// </summary>
public enum ProductionQcCheck : short
{
    /// <summary>A member coded Privilege Status = Withhold is produced, not replaced by a placeholder (Q-77).</summary>
    WithheldWithoutPlaceholder = 1,

    /// <summary>A member the allocation replaced by a withheld placeholder is no longer coded Withhold (re-allocate).</summary>
    PlaceholderNoLongerWithheld = 2,

    /// <summary>A member coded Privilege Status = Redact has no redactions in the production's Redaction Set.</summary>
    RedactWithoutRedactions = 3,

    /// <summary>A redacted member would be produced natively (Q-22; the burn-in verification's NativeShipped).</summary>
    RedactedNative = 4,

    /// <summary>A redacted member's redactions lie on another page set, or on a page it does not have (burn-in PageMissing).</summary>
    RedactionsNotProducible = 5,

    /// <summary>A member produced as images has pages without a stored image, or a native member no native (Technical Issue pages).</summary>
    RenderFailure = 6,

    /// <summary>A member's page count changed since its Bates numbers were allocated (re-allocate).</summary>
    PageCountChanged = 7,

    /// <summary>A member's numbers overlap another production's live Bates range of the prefix.</summary>
    BatesOverlap = 8,

    /// <summary>A member's family or duplicate group has an unresolved privilege conflict (E13-T02).</summary>
    PrivilegeConflicts = 9,

    /// <summary>A member's designation cannot be produced as specified (E12-T04).</summary>
    DesignationNotProducible = 10,

    /// <summary>The person finalizing may not produce the member (authorization re-check per document, Q-15).</summary>
    Inaccessible = 11,

    /// <summary>Warning: the member's family has documents that are not in the production.</summary>
    IncompleteFamily = 12,

    /// <summary>Warning: the member has no extracted text although the production delivers text.</summary>
    TextMissing = 13,

    /// <summary>Warning: the member has no confidentiality designation although the production designates.</summary>
    BlankConfidentiality = 14,
}

public enum ProductionQcSeverity
{
    /// <summary>A failure blocks finalization (unless the check may be overridden and an authorized override is given).</summary>
    Blocking,

    /// <summary>Finalization needs the warnings acknowledged.</summary>
    Warning,
}

public enum ProductionQcStatus
{
    Passed,
    Failed,

    /// <summary>Failed, but finalized with an authorized override and its reason.</summary>
    Overridden,

    /// <summary>A warning not (yet) acknowledged.</summary>
    Warning,

    /// <summary>A warning acknowledged at finalization.</summary>
    Acknowledged,
}

/// <summary>Why the gate ran. Stored as smallint; values are fixed forever.</summary>
public enum ProductionQcPurpose : short
{
    /// <summary>On request (the validation summary before finalizing).</summary>
    Check = 1,

    /// <summary>Inside a finalization: a passed run is the one the production's manifest names.</summary>
    Finalization = 2,
}

/// <summary>Stored as smallint; values are fixed forever.</summary>
public enum ProductionQcOutcome : short
{
    Passed = 1,
    Blocked = 2,
}

/// <param name="Key">The check's name in the API, the report and audit (camelCase).</param>
/// <param name="Overridable">Whether an authorized person may finalize despite a failure, with a reason.</param>
public sealed record ProductionQcCheckDefinition(
    ProductionQcCheck Check, string Key, ProductionQcSeverity Severity, bool Overridable, string Title, string Remedy);

/// <summary>An override of one failed check at finalization.</summary>
public sealed record ProductionQcOverride(ProductionQcCheck Check, string Reason);

/// <summary>A member the person finalizing may not produce (authorization re-check, Q-15).</summary>
public readonly record struct ProductionQcMember(long Sequence, Guid DocumentId);

/// <summary>What one run of the gate checks and how a finalization answers its findings.</summary>
/// <param name="Inaccessible">Members the person finalizing failed the per-document authorization re-check for.</param>
/// <param name="RedactionSetId">The production's Redaction Set (null: the workspace's Default set).</param>
public sealed record ProductionQcRequest(
    Guid QcRunId,
    ProductionQcPurpose Purpose,
    Guid RunBy,
    DateTimeOffset RunAt,
    IReadOnlyList<ProductionQcMember> Inaccessible,
    IReadOnlyList<ProductionQcOverride> Overrides,
    bool AcknowledgeWarnings,
    DesignationPlan Designations,
    Guid? RedactionSetId,
    bool IncludeText);

/// <summary>One check's result: its document count over every member (documents the caller may not see included).</summary>
public sealed record ProductionQcCheckResult(ProductionQcCheckDefinition Definition, ProductionQcStatus Status, long Documents, string? OverrideReason);

/// <summary>A run of the gate with its canonical report (what <c>production_qc_run</c> stores and the manifest names).</summary>
public sealed record ProductionQcResult(
    Guid QcRunId,
    Guid ProductionId,
    ProductionQcPurpose Purpose,
    ProductionQcOutcome Outcome,
    DateTimeOffset RunAt,
    Guid RunBy,
    bool WarningsAcknowledged,
    IReadOnlyList<ProductionQcCheckResult> Checks,
    string ReportJson,
    byte[] ReportSha256)
{
    public bool Passed => Outcome == ProductionQcOutcome.Passed;

    public IEnumerable<ProductionQcCheckResult> Failed => Checks.Where(c => c.Status == ProductionQcStatus.Failed);

    public IEnumerable<ProductionQcCheckResult> Overridden => Checks.Where(c => c.Status == ProductionQcStatus.Overridden);

    public IEnumerable<ProductionQcCheckResult> Warnings => Checks.Where(c => c.Status is ProductionQcStatus.Warning or ProductionQcStatus.Acknowledged);

    public ProductionQcCheckResult this[ProductionQcCheck check] => Checks.First(c => c.Definition.Check == check);
}

/// <summary>A stored run (latest first when listed).</summary>
public sealed record ProductionQcRunRecord(Guid QcRunId, Guid ProductionId, ProductionQcPurpose Purpose, ProductionQcOutcome Outcome, Guid RunBy,
    DateTimeOffset RunAt, string ReportJson, byte[] ReportSha256);

/// <summary>A document-level exception of a run, with what identifies the member in the production.</summary>
public sealed record ProductionQcExceptionRow(
    ProductionQcCheck Check, long Sequence, Guid DocumentId, string? ControlNumber, string? ProdBegBates, string? ProdEndBates, string? Detail);

/// <summary>Keyset position in a run's exceptions (report order: check, then production order).</summary>
public readonly record struct ProductionQcExceptionCursor(short Check, long Sequence);

/// <summary>
/// The rules of the production QC gate (E12-T07): the checks with their severity and whether they may be overridden,
/// how a run's document counts and a finalization's overrides and acknowledgement become each check's status and the
/// run's outcome, and the run's canonical report JSON (fixed member order, UTF-8, no whitespace) with its SHA-256.
/// </summary>
public static class ProductionQcRules
{
    public const int SchemaVersion = 1;

    public const int MaxOverrideReasonLength = 2_000;

    /// <summary>Every check, in report order.</summary>
    public static IReadOnlyList<ProductionQcCheckDefinition> Checks { get; } =
    [
        new(ProductionQcCheck.WithheldWithoutPlaceholder, "withheldWithoutPlaceholder", ProductionQcSeverity.Blocking, false,
            "Withheld documents without a placeholder",
            "Take them out of the frozen set, change their privilege call, or produce withheld documents as placeholders and allocate Bates numbers again."),
        new(ProductionQcCheck.PlaceholderNoLongerWithheld, "placeholderNoLongerWithheld", ProductionQcSeverity.Blocking, false,
            "Withheld placeholders for documents no longer withheld",
            "Their privilege call changed after the Bates numbers were allocated; allocate Bates numbers again."),
        new(ProductionQcCheck.RedactWithoutRedactions, "redactWithoutRedactions", ProductionQcSeverity.Blocking, true,
            "Documents coded Redact without redactions",
            "Draw their redactions in the production's Redaction Set or change their privilege call."),
        new(ProductionQcCheck.RedactedNative, "redactedNative", ProductionQcSeverity.Blocking, false,
            "Redacted documents produced natively",
            "A redacted document is never produced natively; produce its file type as images and allocate Bates numbers again."),
        new(ProductionQcCheck.RedactionsNotProducible, "redactionsNotProducible", ProductionQcSeverity.Blocking, false,
            "Redactions not on the produced pages",
            "Their redactions were drawn on another page set or on pages the document no longer has; review them."),
        new(ProductionQcCheck.RenderFailure, "renderFailure", ProductionQcSeverity.Blocking, true,
            "Documents that cannot be imaged",
            "Pages without an image (or a native that is not stored) would be produced as Technical Issue pages; re-render or re-import them."),
        new(ProductionQcCheck.PageCountChanged, "pageCountChanged", ProductionQcSeverity.Blocking, false,
            "Page counts changed since allocation",
            "The documents were re-rendered after the Bates numbers were allocated; allocate Bates numbers again."),
        new(ProductionQcCheck.BatesOverlap, "batesOverlap", ProductionQcSeverity.Blocking, false,
            "Bates numbers already used",
            "The numbers overlap another production's range of the prefix; choose a start number after the numbers already used."),
        new(ProductionQcCheck.PrivilegeConflicts, "privilegeConflicts", ProductionQcSeverity.Blocking, true,
            "Unresolved privilege conflicts",
            "Resolve the family or duplicate privilege conflicts (Searches › Privilege Conflicts) or override with a reason (needs PrivilegeLog.Generate)."),
        new(ProductionQcCheck.DesignationNotProducible, "designationNotProducible", ProductionQcSeverity.Blocking, false,
            "Designations that cannot be produced",
            "List every designation in the specification's levels and stamp {confidentiality} with an endorsement."),
        new(ProductionQcCheck.Inaccessible, "inaccessible", ProductionQcSeverity.Blocking, false,
            "Documents you may not produce",
            "Someone who may produce every document finalizes the production, or the documents leave the frozen set."),
        new(ProductionQcCheck.IncompleteFamily, "incompleteFamily", ProductionQcSeverity.Warning, false,
            "Incomplete families",
            "Some family members are not in the production; acknowledge to finalize as is."),
        new(ProductionQcCheck.TextMissing, "textMissing", ProductionQcSeverity.Warning, false,
            "Documents without extracted text",
            "They are produced without a text file; acknowledge to finalize as is."),
        new(ProductionQcCheck.BlankConfidentiality, "blankConfidentiality", ProductionQcSeverity.Warning, false,
            "Documents without a confidentiality designation",
            "They carry no designation; acknowledge to finalize as is."),
    ];

    public static ProductionQcCheckDefinition Definition(ProductionQcCheck check) => Checks.First(c => c.Check == check);

    public static ProductionQcCheckDefinition? Find(string? key) =>
        key is null ? null : Checks.FirstOrDefault(c => string.Equals(c.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns a run's document counts per check into statuses and the outcome. A failed blocking check is Overridden
    /// only at finalization with an override of an overridable check; warnings are Acknowledged only at finalization
    /// with the acknowledgement. A check run (purpose Check) is Blocked by failures only; a finalization also by
    /// unacknowledged warnings.
    /// </summary>
    public static ProductionQcResult Evaluate(ProductionRecord production, ProductionQcRequest request, IReadOnlyDictionary<ProductionQcCheck, long> counts)
    {
        ArgumentNullException.ThrowIfNull(production);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(counts);
        var finalizing = request.Purpose == ProductionQcPurpose.Finalization;
        var results = new List<ProductionQcCheckResult>(Checks.Count);
        foreach (var definition in Checks)
        {
            var documents = counts.GetValueOrDefault(definition.Check);
            var overrideReason = finalizing && definition.Overridable
                ? request.Overrides.FirstOrDefault(o => o.Check == definition.Check)?.Reason
                : null;
            var status = documents == 0
                ? ProductionQcStatus.Passed
                : definition.Severity == ProductionQcSeverity.Warning
                    ? finalizing && request.AcknowledgeWarnings ? ProductionQcStatus.Acknowledged : ProductionQcStatus.Warning
                    : overrideReason is not null ? ProductionQcStatus.Overridden : ProductionQcStatus.Failed;
            results.Add(new ProductionQcCheckResult(definition, status, documents, status == ProductionQcStatus.Overridden ? overrideReason : null));
        }

        var blocked = results.Any(r => r.Status == ProductionQcStatus.Failed) || (finalizing && results.Any(r => r.Status == ProductionQcStatus.Warning));
        var outcome = blocked ? ProductionQcOutcome.Blocked : ProductionQcOutcome.Passed;
        var acknowledged = finalizing && request.AcknowledgeWarnings;
        var json = Serialize(production, request, outcome, acknowledged, results);
        return new ProductionQcResult(request.QcRunId, production.ProductionId, request.Purpose, outcome, request.RunAt, request.RunBy, acknowledged, results,
            json, SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>A stored run's result, read back from its report.</summary>
    public static ProductionQcResult Read(ProductionQcRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var document = JsonDocument.Parse(run.ReportJson);
        var root = document.RootElement;
        var checks = new List<ProductionQcCheckResult>();
        foreach (var item in root.GetProperty("checks").EnumerateArray())
        {
            if (Find(item.GetProperty("check").GetString()) is not { } definition)
            {
                continue;
            }

            var status = Enum.Parse<ProductionQcStatus>(item.GetProperty("status").GetString()!, ignoreCase: true);
            var reason = item.TryGetProperty("override", out var o) && o.ValueKind == JsonValueKind.Object ? o.GetProperty("reason").GetString() : null;
            checks.Add(new ProductionQcCheckResult(definition, status, item.GetProperty("documents").GetInt64(), reason));
        }

        return new ProductionQcResult(run.QcRunId, run.ProductionId, run.Purpose, run.Outcome, run.RunAt, run.RunBy,
            root.GetProperty("warningsAcknowledged").GetBoolean(), checks, run.ReportJson, run.ReportSha256);
    }

    /// <summary>Audit details of a run: outcome, report hash and the checks that did not pass with their counts.</summary>
    public static Dictionary<string, string?> AuditDetails(ProductionQcResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ProductionId"] = result.ProductionId.ToString(),
            ["QcRunId"] = result.QcRunId.ToString(),
            ["Purpose"] = result.Purpose == ProductionQcPurpose.Finalization ? "Finalization" : "Check",
            ["Outcome"] = result.Outcome.ToString(),
            ["ReportSha256"] = Convert.ToHexStringLower(result.ReportSha256),
            ["Failed"] = string.Join(',', result.Failed.Select(c => c.Definition.Key)),
            ["Overridden"] = string.Join(',', result.Overridden.Select(c => c.Definition.Key)),
            ["Warnings"] = string.Join(',', result.Warnings.Select(c => c.Definition.Key)),
            ["WarningsAcknowledged"] = result.WarningsAcknowledged ? "true" : "false",
        };
        foreach (var check in result.Checks.Where(c => c.Documents > 0))
        {
            details["Documents." + check.Definition.Key] = check.Documents.ToString(CultureInfo.InvariantCulture);
        }

        return details;
    }

    private static string Serialize(
        ProductionRecord production, ProductionQcRequest request, ProductionQcOutcome outcome, bool acknowledged, List<ProductionQcCheckResult> results)
    {
        var format = new BatesFormat(production.BatesPrefix, production.BatesPadding, production.BatesSuffix, BatesNumberingLevel.Page);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", SchemaVersion);
            json.WriteString("kind", "productionQc");
            json.WriteString("qcRunId", request.QcRunId.ToString("D"));
            json.WriteStartObject("production");
            json.WriteString("productionId", production.ProductionId.ToString("D"));
            json.WriteString("lineageId", production.LineageId.ToString("D"));
            json.WriteNumber("version", production.Version);
            json.WriteString("name", production.Name);
            json.WriteString("snapshotId", production.SnapshotId.ToString("D"));
            json.WriteString("specificationSha256", Convert.ToHexStringLower(production.SpecificationSha256));
            json.WriteString("batesFirst", production.BatesFirst is { } f ? format.Format(f) : null);
            json.WriteString("batesLast", production.BatesLast is { } l && l >= 1 ? format.Format(l) : null);
            json.WriteNumber("documents", production.BatesDocuments ?? 0);
            json.WriteString("assignmentsSha256", production.AssignmentsSha256 is { } a ? Convert.ToHexStringLower(a) : null);
            json.WriteEndObject();
            json.WriteString("purpose", request.Purpose == ProductionQcPurpose.Finalization ? "finalization" : "check");
            json.WriteString("runAt", request.RunAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("runBy", request.RunBy.ToString("D"));
            json.WriteString("outcome", outcome == ProductionQcOutcome.Passed ? "passed" : "blocked");
            json.WriteBoolean("warningsAcknowledged", acknowledged);
            json.WriteStartArray("checks");
            foreach (var result in results)
            {
                json.WriteStartObject();
                json.WriteString("check", result.Definition.Key);
                json.WriteString("severity", result.Definition.Severity == ProductionQcSeverity.Blocking ? "blocking" : "warning");
                json.WriteBoolean("overridable", result.Definition.Overridable);
                json.WriteString("status", JsonNamingPolicy.CamelCase.ConvertName(result.Status.ToString()));
                json.WriteNumber("documents", result.Documents);
                if (result.OverrideReason is { } reason)
                {
                    json.WriteStartObject("override");
                    json.WriteString("reason", reason);
                    json.WriteString("by", request.RunBy.ToString("D"));
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteNull("override");
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
