namespace Opportunity.Contracts.Api;

/// <summary>
/// What every defensibility report states about its data (E14-T05). The report reads only what was recorded at or
/// before <see cref="AsOf"/>, so generating it again with the same <c>asOf</c> gives the same <see cref="Fingerprint"/>.
/// </summary>
/// <param name="Report">ReviewSummary | ProductionHistory | SearchTermHits | ChainOfCustody.</param>
/// <param name="AsOf">The data-as-of point (UTC): the requested <c>asOf</c>, or the moment of generation.</param>
/// <param name="SnapshotId">The snapshot that scopes the report, when one does.</param>
/// <param name="SearchGeneration">The workspace's search generation when the report was generated as of now; null for an earlier point.</param>
/// <param name="Fingerprint">SHA-256 (hex) of the report's canonical as-of content (identifiers, counts, hashes and times).</param>
/// <param name="AuditEventId">The <c>Audit.DefensibilityReportGenerated</c> event of this generation.</param>
public sealed record DefensibilityStatementResource(
    string Report,
    DateTimeOffset AsOf,
    Guid? SnapshotId,
    long? SearchGeneration,
    DateTimeOffset GeneratedAt,
    string GeneratedBy,
    string Fingerprint,
    Guid AuditEventId);

/// <summary>
/// <c>GET …/defensibility/review-summary</c>: the review population (documents in scope, reviewed by a person, not
/// reviewed) and the coding by reviewer and by reviewer and UTC day, from the coding history up to the as-of point.
/// </summary>
/// <param name="Snapshot">The frozen set that scopes the population; null for every document the workspace held.</param>
/// <param name="DaysTruncated">More reviewer days exist than are listed (the totals cover all).</param>
public sealed record ReviewSummaryResource(
    DefensibilityStatementResource Statement,
    DefensibilitySnapshotResource? Snapshot,
    ReviewPopulationResource Population,
    IReadOnlyList<ReviewerCodingResource> Reviewers,
    IReadOnlyList<ReviewerDayResource> Days,
    bool DaysTruncated);

public sealed record DefensibilitySnapshotResource(Guid SnapshotId, string Name, string Purpose, long Documents, DateTimeOffset? MaterializedAt);

/// <param name="Reviewed">Documents a person coded (interactively, or by a bulk edit they started).</param>
/// <param name="SystemCodedOnly">Documents only system rules (for example propagation) coded.</param>
/// <param name="BulkSkips">Field changes bulk edits skipped because someone else had changed the field (Q-07).</param>
public sealed record ReviewPopulationResource(long Documents, long Reviewed, long NotReviewed, long SystemCodedOnly, long BulkSkips);

/// <param name="Documents">Documents the reviewer changed.</param>
/// <param name="Changes">Field changes (interactive + bulk + system rule).</param>
public sealed record ReviewerCodingResource(
    Guid UserId, string? DisplayName, long Documents, long Changes, long Interactive, long Bulk, long SystemRule, DateTimeOffset FirstAt,
    DateTimeOffset LastAt);

/// <param name="Date">UTC day, yyyy-MM-dd.</param>
public sealed record ReviewerDayResource(
    string Date, Guid UserId, string? DisplayName, long Documents, long Changes, long Interactive, long Bulk, long SystemRule);

/// <summary>
/// <c>GET …/defensibility/production-history</c>: every production finalized by the as-of point, as it stood then, with
/// its Bates range, counts, the hashes of its specification, Bates assignments and finalization manifest, the
/// finalization's QC result and its completed volumes.
/// </summary>
public sealed record ProductionHistoryResource(DefensibilityStatementResource Statement, IReadOnlyList<ProductionHistoryEntryResource> Productions);

/// <param name="Status">Finalized | Voided (as of the point).</param>
/// <param name="Pages">Bates units (pages, or one per placeholder or native).</param>
public sealed record ProductionHistoryEntryResource(
    Guid ProductionId,
    string Name,
    int Version,
    string Status,
    Guid SnapshotId,
    DateTimeOffset FinalizedAt,
    string FinalizedBy,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    string? BatesBegin,
    string? BatesEnd,
    long Documents,
    long Pages,
    string SpecificationSha256,
    string? AssignmentsSha256,
    string ManifestSha256,
    ProductionQcHistoryResource? Qc,
    IReadOnlyList<ProductionVolumeHistoryResource> Volumes);

/// <param name="Outcome">Passed | Blocked.</param>
public sealed record ProductionQcHistoryResource(
    Guid QcRunId, DateTimeOffset RunAt, string Outcome, bool WarningsAcknowledged, string ReportSha256, IReadOnlyList<ProductionQcCheckHistoryResource> Checks);

/// <param name="Status">passed | failed | overridden | warning | acknowledged.</param>
public sealed record ProductionQcCheckHistoryResource(string Check, string Severity, string Status, long Documents);

/// <param name="Verification">Passed | Failed | NotRecorded (burn-in verification, E12-T06).</param>
public sealed record ProductionVolumeHistoryResource(
    Guid VolumeId,
    string? Name,
    DateTimeOffset CompletedAt,
    long Documents,
    long Pages,
    long Files,
    long Bytes,
    string ManifestSha256,
    string Verification,
    long? VerifiedDocuments,
    string? VerificationReportSha256);

/// <summary>
/// <c>GET …/defensibility/search-term-hits</c>: every search term report whose current run completed by the as-of point,
/// each tied to its snapshot and search generation, with the index state and lag at execution and its counts.
/// </summary>
/// <param name="Truncated">More reports exist than are listed (the oldest are listed).</param>
public sealed record SearchTermHitsResource(DefensibilityStatementResource Statement, IReadOnlyList<SearchTermHitReportResource> Reports, bool Truncated);

/// <param name="Run">The report's run (a rerun replaces the counts).</param>
/// <param name="IndexCurrent">False when changes were still being made searchable during the run.</param>
/// <param name="IndexPendingChanges">Changes not yet searchable when the run started; null for runs before the lag was kept.</param>
/// <param name="IndexLagSeconds">Age of the oldest of them when the run started; null for runs before the lag was kept.</param>
public sealed record SearchTermHitReportResource(
    Guid ReportId,
    string Name,
    int Run,
    string Scope,
    Guid? SnapshotId,
    long? SearchGeneration,
    bool? IndexCurrent,
    long? IndexPendingChanges,
    double? IndexLagSeconds,
    string ExecutedBy,
    DateTimeOffset? ExecutedAt,
    DateTimeOffset? CompletedAt,
    SearchTermReportTotals Totals,
    IReadOnlyList<SearchTermReportTerm> Terms);

/// <summary>
/// <c>GET …/documents/{documentId}/chain-of-custody</c>: where the document came from (import, load file and row), the
/// hashes recorded at import next to the hashes of the bytes re-read from storage now, its derived artifacts (page sets,
/// renditions, overlays) and the productions it was produced in, as of the point.
/// </summary>
/// <param name="ImportHashes">The native's hashes as computed while it was imported.</param>
/// <param name="Verified">Objects and load files re-read from storage for this report.</param>
/// <param name="Mismatches">Of those, the ones whose bytes no longer match (or are missing): each raises Integrity.HashMismatch.</param>
public sealed record ChainOfCustodyResource(
    DefensibilityStatementResource Statement,
    Guid DocumentId,
    string ControlNumber,
    string? FileName,
    long? FileSize,
    DateTimeOffset CreatedAt,
    CustodyHashesResource ImportHashes,
    int Verified,
    int Mismatches,
    IReadOnlyList<CustodyImportResource> Imports,
    IReadOnlyList<CustodyObjectResource> Objects,
    bool ObjectsTruncated,
    IReadOnlyList<CustodyPageSetResource> PageSets,
    IReadOnlyList<CustodyOverlayResource> Overlays,
    IReadOnlyList<CustodyProductionResource> Productions);

/// <param name="Upstream">The hash the load file supplied (lower-case hex), when it named one.</param>
public sealed record CustodyHashesResource(string? Sha256, string? Sha1, string? Md5, string? Upstream);

/// <param name="Status">Match | Mismatch | Missing | NotVerified.</param>
/// <param name="Sha256">SHA-256 of the bytes read from storage now; null when they were not read.</param>
public sealed record CustodyVerificationResource(string Status, string? Sha256, DateTimeOffset? VerifiedAt);

/// <param name="Action">Imported | Overlaid | Skipped.</param>
/// <param name="LoadFileSha256">The load file's SHA-256 recorded when it was uploaded.</param>
public sealed record CustodyImportResource(
    Guid ImportId,
    string Name,
    string Mode,
    string LoadFileName,
    string LoadFileSha256,
    long LoadFileSize,
    CustodyVerificationResource LoadFileVerification,
    long RowNumber,
    string Action,
    string StartedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    Guid JobId);

/// <param name="Area">Native | Text | Image | Rendition | Production | Export | …</param>
/// <param name="Current">The document's current native, text or a page of its active page set (now).</param>
/// <param name="RecordedSha256">SHA-256 recorded when the object was stored (at import for originals).</param>
public sealed record CustodyObjectResource(
    Guid ObjectId,
    string Area,
    bool Current,
    Guid? PageSetId,
    int? Page,
    long SizeBytes,
    string? ContentType,
    DateTimeOffset StoredAt,
    Guid? JobId,
    string RecordedSha256,
    CustodyVerificationResource Verification);

/// <param name="Source">Imported | Rendered.</param>
public sealed record CustodyPageSetResource(
    Guid PageSetId, string Source, string? Renderer, string? RendererVersion, int Pages, string Status, DateTimeOffset CreatedAt, bool Active);

public sealed record CustodyOverlayResource(Guid ImportId, long RowNumber, long DocumentVersion, DateTimeOffset OverlaidAt, IReadOnlyList<string> Changed);

/// <param name="Output">Image | Native | Placeholder.</param>
public sealed record CustodyProductionResource(
    Guid ProductionId, string Name, int Version, DateTimeOffset FinalizedAt, DateTimeOffset? VoidedAt, string Output, string? BegBates, string? EndBates,
    IReadOnlyList<Guid> VolumeIds);
