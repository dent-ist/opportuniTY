using Opportunity.Application.Search.TermReports;
using Opportunity.Core.Storage;

namespace Opportunity.Application.Defensibility;

/// <summary>The defensibility reports (E14-T05) and the pack that bundles them.</summary>
public enum DefensibilityReportKind
{
    ReviewSummary,
    ProductionHistory,
    SearchTermHits,
    ChainOfCustody,
    Pack,
}

// ---------------------------------------------------------------------------------------------------------------------
// Review population and coding summary by reviewer and date
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// The review population as of the report's point: the documents in scope (a snapshot's members, or every document the
/// workspace held then), those a person coded (interactively or by a bulk edit they started) and those nobody coded.
/// </summary>
public sealed record ReviewPopulation(long Documents, long Reviewed, long NotReviewed, long SystemCodedOnly, long BulkSkips);

/// <summary>One reviewer's coding up to the as-of point: documents changed and field changes by how they were made.</summary>
public sealed record ReviewerCoding(
    Guid ActorId,
    string? DisplayName,
    long Documents,
    long Changes,
    long InteractiveChanges,
    long BulkChanges,
    long SystemChanges,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt);

/// <summary>One reviewer's coding on one UTC day.</summary>
public sealed record ReviewerDayCoding(
    DateOnly Date, Guid ActorId, string? DisplayName, long Documents, long Changes, long InteractiveChanges, long BulkChanges, long SystemChanges);

public sealed record ReviewSummaryData(
    ReviewPopulation Population, IReadOnlyList<ReviewerCoding> Reviewers, IReadOnlyList<ReviewerDayCoding> Days, bool DaysTruncated);

/// <summary>A snapshot that scopes a review summary (a frozen document set; ADR-002).</summary>
public sealed record DefensibilitySnapshot(Guid SnapshotId, string Name, string Purpose, long Documents, DateTimeOffset? MaterializedAt, string? RootSha256);

// ---------------------------------------------------------------------------------------------------------------------
// Production history
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>A completed volume run of a production: counts, the manifest hash and the burn-in verification (E12-T05/T06).</summary>
public sealed record ProductionVolumeHistory(
    Guid VolumeId,
    string SettingsJson,
    DateTimeOffset CompletedAt,
    long Documents,
    long Pages,
    long Files,
    long Bytes,
    byte[] ManifestSha256,
    bool? VerificationPassed,
    long? VerifiedDocuments,
    byte[]? VerificationReportSha256);

/// <summary>One check of the finalization's QC run (E12-T07) as its canonical report states it.</summary>
public sealed record ProductionQcCheckHistory(string Check, string Severity, string Status, long Documents);

/// <summary>The QC run the finalization recorded (its canonical report is kept verbatim with its SHA-256).</summary>
public sealed record ProductionQcHistory(
    Guid QcRunId, DateTimeOffset RunAt, string Outcome, bool WarningsAcknowledged, string ReportJson, byte[] ReportSha256,
    IReadOnlyList<ProductionQcCheckHistory> Checks);

/// <summary>
/// A produced production (finalized by the as-of point) as it stood then: a void after the point is not shown. The
/// manifest is the canonical JSON frozen at finalization; its SHA-256, the specification's and the Bates assignments'
/// tie the history to the production records.
/// </summary>
public sealed record ProductionHistoryEntry(
    Guid ProductionId,
    Guid LineageId,
    int Version,
    string Name,
    Guid SnapshotId,
    DateTimeOffset FinalizedAt,
    Guid FinalizedBy,
    string? FinalizedByDisplay,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    string? BatesBegin,
    string? BatesEnd,
    long Documents,
    long Units,
    byte[] SpecificationSha256,
    byte[]? AssignmentsSha256,
    string Manifest,
    byte[] ManifestSha256,
    ProductionQcHistory? Qc,
    IReadOnlyList<ProductionVolumeHistory> Volumes)
{
    public bool Voided => VoidedAt is not null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Search term hit reports
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>A completed search term report run with its terms: it is tied to its snapshot and search generation (Q-30).</summary>
public sealed record TermHitReportEntry(SearchTermReportRecord Report, IReadOnlyList<SearchTermRecord> Terms);

// ---------------------------------------------------------------------------------------------------------------------
// Per-document chain of custody
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>An import that created or changed the document: its load file (with the hash recorded when it was uploaded) and row.</summary>
public sealed record CustodyImport(
    Guid ImportId,
    Guid JobId,
    string Name,
    string Mode,
    string LoadFileName,
    string LoadFileKey,
    byte[] LoadFileSha256,
    long LoadFileSize,
    long RowNumber,
    string Action,
    Guid StartedBy,
    string? StartedByDisplay,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>A stored object of the document: the hash registered when it was written (the import hash for originals).</summary>
public sealed record CustodyObject(
    Guid ObjectId,
    ObjectArea Area,
    string LogicalKey,
    byte[] Sha256,
    long SizeBytes,
    string? ContentType,
    StoredObjectState State,
    DateTimeOffset CreatedAt,
    Guid? CreatedByJobId,
    bool Current,
    Guid? PageSetId,
    int? PageOrdinal);

/// <summary>A page set of the document: imported images or a rendition (renderer, version, settings hash).</summary>
public sealed record CustodyPageSet(
    Guid PageSetId, string Source, Guid? ImportJobId, string? Renderer, string? RendererVersion, byte[]? RenderSettingsSha256, int Pages, string Status,
    DateTimeOffset CreatedAt, bool Active);

/// <summary>An overlay import that changed the document (ADR-009 §6): the fields it changed and the version it produced.</summary>
public sealed record CustodyOverlay(Guid ImportId, long RowNumber, long DocumentVersion, DateTimeOffset OccurredAt, IReadOnlyList<string> Changed);

/// <summary>A production the document was produced in by the as-of point, with its numbers and completed volumes.</summary>
public sealed record CustodyProduction(
    Guid ProductionId,
    string Name,
    int Version,
    DateTimeOffset FinalizedAt,
    DateTimeOffset? VoidedAt,
    string Output,
    string? BegBates,
    string? EndBates,
    IReadOnlyList<Guid> Volumes);

/// <summary>What the store reads for a document's chain of custody as of a point.</summary>
public sealed record ChainOfCustodyData(
    Guid DocumentId,
    string ControlNumber,
    DateTimeOffset CreatedAt,
    byte[]? Md5,
    byte[]? Sha1,
    byte[]? Sha256,
    string? UpstreamHash,
    string? FileName,
    long? FileSize,
    IReadOnlyList<CustodyImport> Imports,
    IReadOnlyList<CustodyObject> Objects,
    bool ObjectsTruncated,
    IReadOnlyList<CustodyPageSet> PageSets,
    IReadOnlyList<CustodyOverlay> Overlays,
    IReadOnlyList<CustodyProduction> Productions);

/// <summary>How the bytes in storage compared with the registered hash when the report re-read them.</summary>
public enum ObjectVerificationStatus
{
    /// <summary>The bytes hash to the registered SHA-256.</summary>
    Match,

    /// <summary>The bytes hash to something else (raises <c>Integrity.HashMismatch</c>).</summary>
    Mismatch,

    /// <summary>The object is not in storage.</summary>
    Missing,

    /// <summary>Not re-read: beyond the per-report limit, or object storage is not configured for this host.</summary>
    NotVerified,
}

/// <param name="Sha256">The hash of the bytes read now; null when they could not be read.</param>
public sealed record ObjectVerification(ObjectVerificationStatus Status, byte[]? Sha256, DateTimeOffset? VerifiedAt);

/// <summary>
/// PostgreSQL reads for the defensibility reports (E14-T05), each inside the workspace's RLS context and as of a point in
/// time: rows written after it (productions finalized or voided, volume runs, imports, artifacts) are left out, so the same
/// point gives the same report. The review summary reads the coding history through <c>ICodingRepository</c>. It never
/// authorizes; the service does.
/// </summary>
public interface IDefensibilityReportStore
{
    /// <summary>The snapshot when it is Ready (its members are kept); null otherwise.</summary>
    Task<DefensibilitySnapshot?> GetSnapshotAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default);

    /// <summary>Productions finalized at or before <paramref name="asOf"/>, by finalization time.</summary>
    Task<IReadOnlyList<ProductionHistoryEntry>> ReadProductionHistoryAsync(Guid workspaceId, DateTimeOffset asOf, CancellationToken cancellationToken = default);

    /// <summary>Search term reports whose current run completed at or before <paramref name="asOf"/>, by completion.</summary>
    Task<IReadOnlyList<TermHitReportEntry>> ReadTermHitReportsAsync(Guid workspaceId, DateTimeOffset asOf, int limit, CancellationToken cancellationToken = default);

    /// <summary>The document's chain of custody as of <paramref name="asOf"/>; null when the document does not exist.</summary>
    Task<ChainOfCustodyData?> ReadChainOfCustodyAsync(
        Guid workspaceId, Guid documentId, DateTimeOffset asOf, int maxObjects, CancellationToken cancellationToken = default);

    /// <summary>The search generation counter now (the latest committed change that created search work).</summary>
    Task<long> ReadSearchGenerationAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}
