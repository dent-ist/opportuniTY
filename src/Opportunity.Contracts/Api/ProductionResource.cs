namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/productions</c>: a draft production of a frozen set (purpose
/// Production) or of a saved search (frozen now, for you). Give exactly one of <see cref="SnapshotId"/>,
/// <see cref="SavedSearchId"/> and <see cref="PreviousVersionId"/>.
/// </summary>
/// <param name="Name">Display name; default "Production yyyy-MM-dd HH:mm UTC".</param>
/// <param name="PreviousVersionId">
/// A finalized or voided production to supersede: the new draft is its next version, with the same frozen set and
/// (unless <see cref="Specification"/> is given) the same specification. A finalized production never changes; a
/// change is a new version with new Bates numbers.
/// </param>
public sealed record CreateProductionRequest(
    string? Name = null,
    ProductionSpecification? Specification = null,
    Guid? SnapshotId = null,
    Guid? SavedSearchId = null,
    Guid? PreviousVersionId = null);

/// <summary>Body of <c>PUT …/productions/{productionId}</c> (draft only, with <c>If-Match</c>).</summary>
/// <param name="SnapshotId">Another Ready frozen set (purpose Production); null keeps the current one.</param>
public sealed record UpdateProductionRequest(ProductionSpecification Specification, string? Name = null, Guid? SnapshotId = null);

/// <summary>Body of <c>POST …/productions/{productionId}/void</c>.</summary>
public sealed record VoidProductionRequest(string Reason);

/// <summary>
/// Optional body of <c>POST …/productions/{productionId}/finalize</c>. Finalization runs the QC gate (E12-T07): a failed
/// blocking check refuses it (409 with the QC report) unless the check may be overridden and an override with a reason
/// is given; warnings must be acknowledged.
/// </summary>
/// <param name="PrivilegeConflictOverride">
/// Finalize even if unresolved family or duplicate privilege conflicts exist (E13-T02): needs <c>PrivilegeLog.Generate</c>
/// besides <c>Production.Finalize</c>; the reason is recorded in the manifest and audited. The same as a
/// <see cref="QcOverrides"/> entry for <c>privilegeConflicts</c>.
/// </param>
/// <param name="QcOverrides">
/// Overrides of failed QC checks that may be overridden, each with its reason (1–2,000 characters): recorded in the QC
/// report and the manifest and audited (<c>Production.QcOverride</c>). Used only when the check fails.
/// </param>
/// <param name="AcknowledgeWarnings">Finalize despite QC warnings (incomplete families, missing text, blank confidentiality).</param>
public sealed record FinalizeProductionRequest(
    PrivilegeConflictOverrideRequest? PrivilegeConflictOverride = null,
    IReadOnlyList<ProductionQcOverrideRequest>? QcOverrides = null,
    bool AcknowledgeWarnings = false);

/// <param name="Reason">Why the production may go out with the conflicts (1–2,000 characters).</param>
public sealed record PrivilegeConflictOverrideRequest(string Reason);

/// <summary>
/// How a production is produced (E12-T02). Frozen when the production is finalized; recorded in its manifest with the
/// software versions. Absent members take their defaults; the stored specification has every member filled in.
/// </summary>
/// <param name="DefaultOutput">How documents whose file type no rule names are produced (default <c>image</c>).</param>
/// <param name="FileTypeRules">Per file extension: produce as images, natively (with a slip sheet) or as a placeholder.</param>
/// <param name="IncludeText">Deliver extracted text files (regenerated from redacted output for redacted documents).</param>
/// <param name="Designations">
/// Confidentiality designations (E12-T04): the designation field, its levels with the legend stamped for each, and the
/// family rule. Frozen per document when the production is finalized.
/// </param>
/// <param name="Placeholders">The texts of generated pages: withheld placeholders, technical issues and native slip sheets (E12-T05).</param>
/// <param name="RedactionSetId">
/// The Redaction Set burned into the produced images (E12-T05); null burns the workspace's Default set. Each member's
/// redactions are frozen with the production at finalization.
/// </param>
/// <param name="WithheldDocuments">
/// Documents coded Privilege Status = Withhold (E12-T07): <c>block</c> (default) keeps them in the production and the QC
/// gate refuses to finalize; <c>placeholder</c> produces each as a "Withheld" placeholder taking one Bates number.
/// </param>
public sealed record ProductionSpecification(
    ProductionBatesSettings Bates,
    ProductionImageSettings? Images = null,
    ProductionOutputResource? DefaultOutput = null,
    IReadOnlyList<ProductionFileTypeRule>? FileTypeRules = null,
    bool IncludeText = true,
    ProductionLoadFileSettings? LoadFile = null,
    ProductionEndorsementSettings? Endorsements = null,
    ProductionDesignationSettings? Designations = null,
    ProductionPlaceholderSettings? Placeholders = null,
    Guid? RedactionSetId = null,
    WithheldDocumentsResource? WithheldDocuments = null);

/// <summary>How a production treats documents coded Privilege Status = Withhold (E12-T07).</summary>
public enum WithheldDocumentsResource
{
    /// <summary>They stay members; the QC gate blocks finalization until they leave the frozen set or their call changes.</summary>
    Block,

    /// <summary>Each is produced as a "Withheld" placeholder (one Bates number, one DAT row).</summary>
    Placeholder,
}

/// <summary>
/// The centred text of generated pages (E12-T05). Each may use <c>{bates}</c>, <c>{confidentiality}</c> and
/// <c>{production}</c>; endorsements are stamped on generated pages as on every other page.
/// </summary>
/// <param name="Withheld">A member produced as a placeholder (default "Withheld – Privileged").</param>
/// <param name="TechnicalIssue">A page that cannot be imaged (default "Technical Issue").</param>
/// <param name="NativeSlipSheet">The slip sheet of a member produced natively (default "Document Produced in Native Format").</param>
public sealed record ProductionPlaceholderSettings(string? Withheld = null, string? TechnicalIssue = null, string? NativeSlipSheet = null);

/// <summary>
/// Bates numbering: <c>prefix + start padded to padding digits + suffix</c>, e.g. <c>ABC0000001</c>. Numbers are unique
/// per workspace and prefix (compared without case) across every production; a start number inside a range another
/// production holds is refused.
/// </summary>
/// <param name="Prefix">1–30 letters, digits, <c>_ - .</c>, starting with a letter or digit.</param>
/// <param name="StartNumber">First number (default 1).</param>
/// <param name="Padding">Digits (1–12, default 7).</param>
/// <param name="Suffix">Optional, up to 30 letters, digits, <c>_ - .</c>.</param>
/// <param name="Level">Page (one number per page, default) or document (one per document; pages get <c>.0001</c> suffixes).</param>
public sealed record ProductionBatesSettings(
    string Prefix,
    long StartNumber = 1,
    int Padding = 7,
    string? Suffix = null,
    BatesLevelResource Level = BatesLevelResource.Page);

/// <summary>Bates numbering level.</summary>
public enum BatesLevelResource
{
    Page,
    Document,
}

/// <summary>How a document is produced.</summary>
public enum ProductionOutputResource
{
    Image,
    Native,
    Placeholder,
}

/// <summary>Image file format.</summary>
public enum ProductionImageFormatResource
{
    /// <summary>Single-page TIFF, CCITT Group 4 (black and white).</summary>
    TiffG4,

    /// <summary>JPEG (colour).</summary>
    Jpeg,
}

/// <summary>Image output: format for black-and-white pages, format for colour pages, resolution (default TIFF G4 / JPEG, 300 DPI, Q-21).</summary>
/// <param name="Dpi">Resolution of generated pages (slip sheets, placeholders); produced pages keep their source image's resolution.</param>
/// <param name="ColorFileTypes">
/// Extensions (without the dot) whose pages are produced in <see cref="ColorFormat"/>; every other page in
/// <see cref="Format"/>. Chosen by file type, not by detection (Q-21). Default: common photo and presentation types.
/// </param>
public sealed record ProductionImageSettings(
    ProductionImageFormatResource? Format = null,
    ProductionImageFormatResource? ColorFormat = null,
    int? Dpi = null,
    IReadOnlyList<string>? ColorFileTypes = null);

/// <summary>A per-file-type rule: extensions without the dot, compared without case.</summary>
public sealed record ProductionFileTypeRule(IReadOnlyList<string> Extensions, ProductionOutputResource Output);

/// <summary>
/// Load-file options, as for exports: DAT columns in order, delimiter preset, encodings, path separator and volume
/// naming, plus the date format and time zone dates are written in (Q-28). Default columns: Control Number.
/// </summary>
/// <param name="DateFormat">.NET-style date pattern of the date columns (default <c>yyyy-MM-dd</c>).</param>
/// <param name="TimeZone">IANA time zone of the date columns (default <c>UTC</c>).</param>
public sealed record ProductionLoadFileSettings(
    IReadOnlyList<ExportFieldRequest>? Fields = null,
    string? Delimiters = null,
    string? Encoding = null,
    string? TextEncoding = null,
    string? PathSeparator = null,
    ExportVolumeRequest? Volume = null,
    string? DateFormat = null,
    string? TimeZone = null);

/// <summary>Where an endorsement is stamped on each page.</summary>
public enum EndorsementPositionResource
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>
/// Page endorsements: a text template per position. Templates may use <c>{bates}</c> (the page's Bates label),
/// <c>{confidentiality}</c> (the document's designation) and <c>{production}</c> (the production name).
/// </summary>
/// <param name="FontSize">Points (6–24, default 10).</param>
/// <param name="ExpandCanvas">
/// Add a band above and below the page for the endorsements instead of stamping over its content (default true), so
/// the image is never overwritten.
/// </param>
/// <param name="Margin">Distance of the endorsements from the page edge, in points (0–72, default 18 = a quarter inch).</param>
public sealed record ProductionEndorsementSettings(
    IReadOnlyList<ProductionEndorsement>? Items = null,
    int? FontSize = null,
    bool? ExpandCanvas = null,
    int? Margin = null);

public sealed record ProductionEndorsement(EndorsementPositionResource Position, string Template);

/// <summary>How a production member's designation is decided.</summary>
public enum DesignationFamilyRuleResource
{
    /// <summary>Every member of a family is produced with the highest designation in the family (default).</summary>
    HighestInFamily,

    /// <summary>Each document keeps its own designation.</summary>
    Document,
}

/// <summary>
/// Confidentiality designations of a production (E12-T04). The designation field is a single-choice, security-affecting
/// confidentiality field (default: the workspace's only one, e.g. Confidentiality Designation). Levels list every
/// choice of the field from lowest to highest with the legend stamped as <c>{confidentiality}</c> and written to the
/// load file; the default is the field's choice order with each choice's name as its legend, except "None", whose
/// legend is empty (nothing is stamped).
/// </summary>
/// <param name="FieldId">The designation field; null when the workspace has none (no document is designated).</param>
public sealed record ProductionDesignationSettings(
    int? FieldId = null,
    DesignationFamilyRuleResource? FamilyRule = null,
    IReadOnlyList<ProductionDesignationLevel>? Levels = null);

/// <summary>A designation level: a choice of the designation field and its legend (empty: nothing is stamped).</summary>
public sealed record ProductionDesignationLevel(int ChoiceId, string? Legend = null);

/// <summary>Why a member carries its designation.</summary>
public enum DesignationSourceResource
{
    /// <summary>Nothing is stamped (no value, or a level with an empty legend).</summary>
    None,

    /// <summary>The document's own designation.</summary>
    Document,

    /// <summary>The highest designation in its family.</summary>
    Family,

    /// <summary>Overridden for this production with a reason.</summary>
    Override,
}

/// <summary>
/// A member's designation in a production: its own coded value, what it is (or was, once finalized) produced with, and
/// why. For a draft the values are computed from the current coding; a finalized production shows the values frozen at
/// finalization.
/// </summary>
/// <param name="OwnChoiceId">The document's coded designation now (null when not coded).</param>
/// <param name="ChoiceId">The designation produced.</param>
/// <param name="Legend">The legend stamped on every page and written to the load file.</param>
/// <param name="OverrideReason">The reason given for an override.</param>
public sealed record ProductionDesignationResource(
    long Sequence,
    Guid DocumentId,
    string? ControlNumber,
    string? ProdBegBates,
    string? ProdEndBates,
    int? OwnChoiceId,
    string? OwnDesignation,
    int? ChoiceId,
    string Legend,
    DesignationSourceResource Source,
    string? OverrideReason);

/// <summary>Body of <c>PUT …/productions/{productionId}/designation-overrides/{documentId}</c> (draft only).</summary>
/// <param name="ChoiceId">The designation to produce the document with (a choice of the designation field); null: none.</param>
/// <param name="Reason">Why the family rule is overridden (1–2000 characters; audited).</param>
public sealed record DesignationOverrideRequest(int? ChoiceId, string Reason);

/// <summary>A production's designation override for one document.</summary>
public sealed record DesignationOverrideResource(Guid DocumentId, int? ChoiceId, string Legend, string Reason, Guid CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// A finalized production's re-designation report (E12-T04): the produced documents whose designation under the
/// production's rule would now differ from the one they were produced with, in production order, with their Bates
/// ranges. The overlay load file (<c>…/redesignation-overlay</c>) carries the same rows.
/// </summary>
/// <param name="RestrictedCount">Changed documents you may not view: counted, never listed (Q-52).</param>
public sealed record RedesignationReportResource(
    Guid ProductionId,
    IReadOnlyList<RedesignationResource> Items,
    string? Next,
    long RestrictedCount);

/// <param name="ProducedLegend">The legend the document was produced with.</param>
/// <param name="CurrentLegend">The legend it would carry now.</param>
public sealed record RedesignationResource(
    long Sequence,
    Guid DocumentId,
    string? ControlNumber,
    string ProdBegBates,
    string ProdEndBates,
    int? ProducedChoiceId,
    string ProducedLegend,
    int? CurrentChoiceId,
    string CurrentLegend);

/// <summary>Lifecycle of a production.</summary>
public enum ProductionStatusResource
{
    /// <summary>Editable; Bates numbers may be allocated, and are released again if the draft is discarded (Q-54).</summary>
    Draft,

    /// <summary>Produced: specification, membership and Bates numbers are frozen for the life of the matter.</summary>
    Finalized,

    /// <summary>A finalized production withdrawn; its Bates numbers are never issued again.</summary>
    Voided,

    /// <summary>A draft thrown away; its Bates numbers (never produced) are free again (Q-54).</summary>
    Discarded,
}

/// <summary>State of the Bates allocation of a draft.</summary>
public enum BatesAllocationStateResource
{
    None,
    Allocating,
    Allocated,
    Failed,
}

/// <summary>A production (E12-T02/T03): frozen set + versioned specification + Bates numbers.</summary>
/// <param name="LineageId">Shared by every version of the same production (the first version's id).</param>
/// <param name="SpecificationSha256">SHA-256 of the stored canonical specification JSON.</param>
/// <param name="Manifest">The canonical manifest JSON written at finalization (specification, hashes, software versions).</param>
/// <param name="BatesJob">The allocation job, while or after allocating.</param>
public sealed record ProductionResource(
    Guid ProductionId,
    Guid WorkspaceId,
    Guid LineageId,
    int Version,
    string Name,
    Guid SnapshotId,
    ProductionStatusResource Status,
    ProductionSpecification Specification,
    string SpecificationSha256,
    ProductionBatesResource Bates,
    string? Manifest,
    string? ManifestSha256,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? FinalizedAt,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    DateTimeOffset? DiscardedAt,
    JobResource? BatesJob);

/// <summary>The production's Bates range and allocation state.</summary>
/// <param name="Reason">Why the allocation failed (e.g. an overlap with another production's range).</param>
/// <param name="AssignmentsSha256">SHA-256 over every document's numbers in production order (the manifest's).</param>
public sealed record ProductionBatesResource(
    BatesAllocationStateResource State,
    string? Reason,
    string? First,
    string? Last,
    long? FirstNumber,
    long? LastNumber,
    long? Documents,
    long? Numbers,
    string? AssignmentsSha256,
    ProductionIntegrityResource? Integrity);

/// <summary>The integrity check run after every allocation (E12-T03).</summary>
/// <param name="Problems">Empty when passed: duplicates, gaps, non-adjacent families or attachment ranges found.</param>
public sealed record ProductionIntegrityResource(
    bool Passed,
    long Documents,
    long Numbers,
    long Placeholders,
    long NativeSlipSheets,
    long Gaps,
    IReadOnlyList<string> Problems);

/// <summary>One document's Bates numbers in a production (ProdBegBates … ProdEndAttach), in production order.</summary>
/// <param name="Designation">The legend frozen at finalization (null before; empty when nothing is stamped).</param>
public sealed record ProductionDocumentResource(
    long Sequence,
    Guid DocumentId,
    string? ControlNumber,
    ProductionOutputResource Output,
    int Numbers,
    string? ProdBegBates,
    string? ProdEndBates,
    string? ProdBegAttach,
    string? ProdEndAttach,
    string? Designation = null);

/// <summary>The productions holding a Bates number (or a document), oldest first.</summary>
/// <param name="RestrictedCount">Matches on documents you may not view: counted, never listed (Q-52).</param>
public sealed record BatesLookupResource(IReadOnlyList<BatesLookupMatch> Items, TotalCount Total, long RestrictedCount);

public sealed record BatesLookupMatch(
    Guid ProductionId,
    string ProductionName,
    int ProductionVersion,
    ProductionStatusResource ProductionStatus,
    Guid DocumentId,
    string? ControlNumber,
    string ProdBegBates,
    string ProdEndBates,
    string ProdBegAttach,
    string ProdEndAttach);

/// <summary>
/// Result of re-verifying a production against its manifest: the stored specification, frozen set and Bates
/// assignment are recomputed and compared. Per-file SHA-256 of regenerated volume files is checked once volumes are
/// generated (E12-T05).
/// </summary>
public sealed record ProductionVerificationResource(bool Consistent, IReadOnlyList<ProductionDifference> Differences, ProductionIntegrityResource Integrity);

public sealed record ProductionDifference(string Item, string? Expected, string? Actual);
