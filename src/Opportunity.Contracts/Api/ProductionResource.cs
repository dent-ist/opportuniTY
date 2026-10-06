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
/// How a production is produced (E12-T02). Frozen when the production is finalized; recorded in its manifest with the
/// software versions. Absent members take their defaults; the stored specification has every member filled in.
/// </summary>
/// <param name="DefaultOutput">How documents whose file type no rule names are produced (default <c>image</c>).</param>
/// <param name="FileTypeRules">Per file extension: produce as images, natively (with a slip sheet) or as a placeholder.</param>
/// <param name="IncludeText">Deliver extracted text files (regenerated from redacted output for redacted documents).</param>
public sealed record ProductionSpecification(
    ProductionBatesSettings Bates,
    ProductionImageSettings? Images = null,
    ProductionOutputResource? DefaultOutput = null,
    IReadOnlyList<ProductionFileTypeRule>? FileTypeRules = null,
    bool IncludeText = true,
    ProductionLoadFileSettings? LoadFile = null,
    ProductionEndorsementSettings? Endorsements = null);

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
public sealed record ProductionImageSettings(
    ProductionImageFormatResource? Format = null,
    ProductionImageFormatResource? ColorFormat = null,
    int? Dpi = null);

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
/// <param name="ExpandCanvas">Add a margin to the page instead of stamping over its content (default true).</param>
public sealed record ProductionEndorsementSettings(
    IReadOnlyList<ProductionEndorsement>? Items = null,
    int? FontSize = null,
    bool? ExpandCanvas = null);

public sealed record ProductionEndorsement(EndorsementPositionResource Position, string Template);

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
public sealed record ProductionDocumentResource(
    long Sequence,
    Guid DocumentId,
    string? ControlNumber,
    ProductionOutputResource Output,
    int Numbers,
    string? ProdBegBates,
    string? ProdEndBates,
    string? ProdBegAttach,
    string? ProdEndAttach);

/// <summary>A Bates number (or document) found in a production of the workspace.</summary>
public sealed record BatesLookupResource(IReadOnlyList<BatesLookupMatch> Matches, long RestrictedCount);

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
