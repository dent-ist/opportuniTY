namespace Opportunity.Contracts.Api;

/// <summary>A check of the production QC gate (E12-T07).</summary>
public enum ProductionQcCheckResource
{
    WithheldWithoutPlaceholder = 1,
    PlaceholderNoLongerWithheld = 2,
    RedactWithoutRedactions = 3,
    RedactedNative = 4,
    RedactionsNotProducible = 5,
    RenderFailure = 6,
    PageCountChanged = 7,
    BatesOverlap = 8,
    PrivilegeConflicts = 9,
    DesignationNotProducible = 10,
    Inaccessible = 11,
    IncompleteFamily = 12,
    TextMissing = 13,
    BlankConfidentiality = 14,
}

/// <summary>Blocking checks refuse finalization when they fail; warnings must be acknowledged.</summary>
public enum ProductionQcSeverityResource
{
    Blocking,
    Warning,
}

/// <summary>A check's result in one QC run.</summary>
public enum ProductionQcStatusResource
{
    Passed,
    Failed,

    /// <summary>Failed and finalized with an authorized override (its reason is in the report).</summary>
    Overridden,

    /// <summary>A warning not acknowledged.</summary>
    Warning,

    /// <summary>A warning acknowledged at finalization.</summary>
    Acknowledged,
}

/// <summary>Why the QC gate ran: on request, or inside a finalization.</summary>
public enum ProductionQcPurposeResource
{
    Check,
    Finalization,
}

public enum ProductionQcOutcomeResource
{
    Passed,
    Blocked,
}

/// <param name="Check">A check that may be overridden (renderFailure, redactWithoutRedactions, privilegeConflicts).</param>
/// <param name="Reason">Why the production may go out with the failure (1–2,000 characters); printed in the QC report.</param>
public sealed record ProductionQcOverrideRequest(ProductionQcCheckResource Check, string Reason);

/// <param name="Documents">Members the check found, documents you may not view included (they are never listed).</param>
/// <param name="Remedy">What to do about a failure or warning.</param>
/// <param name="OverrideReason">The reason of the override, when the check was overridden at finalization.</param>
public sealed record ProductionQcCheckResultResource(
    ProductionQcCheckResource Check,
    string Title,
    ProductionQcSeverityResource Severity,
    bool Overridable,
    ProductionQcStatusResource Status,
    long Documents,
    string Remedy,
    string? OverrideReason);

/// <summary>
/// A run of the production QC gate (E12-T07): every check with its status and document count. A finalized production
/// keeps the run of its finalization (named in its manifest); the document-level exceptions are listed by
/// <c>…/qc/exceptions</c> and in the QC report (CSV or PDF) without the documents you may not view.
/// </summary>
/// <param name="ReportSha256">SHA-256 of the run's canonical report JSON, as the manifest records it.</param>
public sealed record ProductionQcReportResource(
    Guid QcRunId,
    Guid ProductionId,
    ProductionQcPurposeResource Purpose,
    ProductionQcOutcomeResource Outcome,
    DateTimeOffset RunAt,
    Guid RunBy,
    bool WarningsAcknowledged,
    string ReportSha256,
    IReadOnlyList<ProductionQcCheckResultResource> Checks);

/// <summary>A document-level exception of a QC run.</summary>
/// <param name="Sequence">The member's position in production order.</param>
/// <param name="Detail">What the check found (for example <c>PagesWithoutImage:2</c>, <c>AnotherPageSet</c>, the overlapping production).</param>
public sealed record ProductionQcExceptionResource(
    ProductionQcCheckResource Check,
    long Sequence,
    Guid DocumentId,
    string? ControlNumber,
    string? ProdBegBates,
    string? ProdEndBates,
    string? Detail);

/// <summary>A page of a QC run's exceptions; documents you may not view are left out and counted (as the Bates lookup does).</summary>
public sealed record ProductionQcExceptionPageResource(
    Guid QcRunId,
    IReadOnlyList<ProductionQcExceptionResource> Items,
    string? NextCursor,
    long Restricted);
