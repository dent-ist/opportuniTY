using Opportunity.Application.Defensibility;

namespace Opportunity.Production.Defensibility;

public enum DefensibilityFormat
{
    Json,
    Csv,
    Pack,
}

/// <summary>
/// What every defensibility report states about its data (E14-T05): the point it is as of (UTC; a request without one
/// is as of the moment it was made), the snapshot that scopes it when one does, the search generation counter when the
/// report was generated as of now, and the fingerprint: the SHA-256 of its canonical as-of content (identifiers,
/// counts, hashes and times; not display names, which can change, and not re-verification results, which are taken
/// now). Generating the same report for the same point gives the same fingerprint.
/// </summary>
public sealed record DefensibilityStatement(
    DefensibilityReportKind Report,
    Guid WorkspaceId,
    DateTimeOffset AsOf,
    Guid? SnapshotId,
    long? SearchGeneration,
    DateTimeOffset GeneratedAt,
    string GeneratedBy,
    string Fingerprint,
    Guid AuditEventId);

public sealed record ReviewSummaryReport(DefensibilityStatement Statement, DefensibilitySnapshot? Snapshot, ReviewSummaryData Data);

public sealed record ProductionHistoryReport(DefensibilityStatement Statement, IReadOnlyList<ProductionHistoryEntry> Productions);

/// <param name="Truncated">More completed reports exist than the report lists (the oldest are listed).</param>
public sealed record SearchTermHitsReport(DefensibilityStatement Statement, IReadOnlyList<TermHitReportEntry> Reports, bool Truncated);

/// <summary>A stored object with the result of re-reading its bytes from object storage now.</summary>
public sealed record VerifiedCustodyObject(CustodyObject Stored, ObjectVerification Verification)
{
    public bool Matches => Verification.Status == ObjectVerificationStatus.Match;
}

/// <summary>An import with the result of re-reading its load file from object storage now.</summary>
public sealed record VerifiedCustodyImport(CustodyImport Import, ObjectVerification Verification);

/// <summary>
/// A document's chain of custody as of a point (E14-T05): where it came from (import, load file and row), the hashes
/// recorded at import next to the hashes of the bytes in storage now, its derived artifacts (page sets, renditions,
/// overlays) and the productions it was produced in.
/// </summary>
public sealed record ChainOfCustodyReport(
    DefensibilityStatement Statement,
    ChainOfCustodyData Data,
    IReadOnlyList<VerifiedCustodyImport> Imports,
    IReadOnlyList<VerifiedCustodyObject> Objects)
{
    public int Verified => Objects.Count(o => o.Verification.Status != ObjectVerificationStatus.NotVerified)
        + Imports.Count(i => i.Verification.Status != ObjectVerificationStatus.NotVerified);

    public int Mismatches => Objects.Count(o => o.Verification.Status is ObjectVerificationStatus.Mismatch or ObjectVerificationStatus.Missing)
        + Imports.Count(i => i.Verification.Status is ObjectVerificationStatus.Mismatch or ObjectVerificationStatus.Missing);
}

public enum DefensibilityOutcomeStatus
{
    Ok,
    NotFound,
    Forbidden,
}

public sealed record ChainOfCustodyOutcome(DefensibilityOutcomeStatus Status, ChainOfCustodyReport? Report = null);

public sealed record ReviewSummaryOutcome(DefensibilityOutcomeStatus Status, ReviewSummaryReport? Report = null);
