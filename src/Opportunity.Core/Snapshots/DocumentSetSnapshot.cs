using Opportunity.Core.Security;

namespace Opportunity.Core.Snapshots;

/// <summary>
/// Lifecycle of a materialized <c>DocumentSetSnapshot</c> (ADR-002 §5). Stored as text. Membership is written only
/// while <see cref="Materializing"/> and published atomically with the move to <see cref="Ready"/>; from then on it
/// never changes. <see cref="Expired"/> keeps the header (counts and hashes) as a tombstone after retention removed
/// the membership pages.
/// </summary>
public enum SnapshotStatus
{
    Materializing,
    Ready,
    Failed,
    Expired,
}

/// <summary>What the snapshot is frozen for. Decides the permission needed to create it and its retention class.</summary>
public enum SnapshotPurpose
{
    /// <summary>Target of a bulk coding job (Q-07 baselines); retained 90 days after its jobs end (ADR-002 §9).</summary>
    BulkCoding,

    /// <summary>Export membership (Q-15 re-check at execution); retained for the life of the matter once used.</summary>
    Export,

    /// <summary>Production membership (Q-08); retained for the life of the matter once used.</summary>
    Production,

    /// <summary>Scope of a saved search-term report (Q-30); retained for the life of the matter once used.</summary>
    Report,
}

/// <summary>Where the candidate set came from. Stored as text.</summary>
public enum SnapshotSourceKind
{
    /// <summary>A query-language search, paged under a point-in-time reader (ADR-002 §5.1).</summary>
    Query,

    /// <summary>A bounded list of explicit document IDs (never a large client list, ADR-002).</summary>
    DocumentIds,

    /// <summary>Another Ready snapshot of the same workspace (a saved selection), re-authorized for the new creator.</summary>
    Snapshot,
}

/// <summary>Why a member is in the snapshot (ADR-002 §5.2.4). Stored as smallint; values are fixed forever.</summary>
public enum SnapshotInclusionReason : short
{
    /// <summary>Matched the query.</summary>
    Hit = 1,

    /// <summary>Named explicitly by the creator.</summary>
    Explicit = 2,

    /// <summary>Family expansion (E09-T03).</summary>
    Family = 3,

    /// <summary>Duplicate expansion (E09-T03).</summary>
    Duplicate = 4,

    /// <summary>Email-thread expansion (E09-T03).</summary>
    Thread = 5,
}

/// <summary>
/// How membership is represented. ADR-002 §6 interim choice (b): ordered member pages in PostgreSQL. Stored as text;
/// E10-T03's benchmark may add representations without changing callers.
/// </summary>
public enum MaterializationStrategy
{
    PgMemberPages,
}

/// <summary>One member: its dense ordinal (1…N), the document and the Q-07 baseline version read at freeze time.</summary>
public readonly record struct SnapshotMember(long Ordinal, Guid DocumentId, long BaselineVersion, SnapshotInclusionReason Reason);

public static class SnapshotRules
{
    /// <summary>Members per membership page (ADR-002 §6 (b)); fixed per snapshot and recorded in its header.</summary>
    public const int DefaultPageSize = 1_000;

    /// <summary>Upper bound of an explicit ID list in one request (ADR-002: never large client ID lists).</summary>
    public const int MaxExplicitDocumentIds = 10_000;

    public const int MaxNameLength = 200;

    public const int MaxQueryLength = 20_000;

    /// <summary>The permission a creator needs for <paramref name="purpose"/>; membership is checked with the same one.</summary>
    /// <remarks>
    /// Break-glass (Q-45) only lifts restriction classes and walls for read permissions, so a snapshot that is going
    /// to be coded, exported or produced is filtered as the creator would be without break-glass.
    /// </remarks>
    public static Permission RequiredPermission(SnapshotPurpose purpose) => purpose switch
    {
        SnapshotPurpose.BulkCoding => Permission.CodingBulk,
        SnapshotPurpose.Export => Permission.ExportCreate,
        SnapshotPurpose.Production => Permission.ProductionCreate,
        SnapshotPurpose.Report => Permission.SearchExecute,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown snapshot purpose."),
    };

    /// <summary>
    /// The document-level permission each member is authorized with. Reports only show counts and IDs of documents
    /// the creator may view; every other purpose acts on the documents with its own permission.
    /// </summary>
    public static Permission MemberPermission(SnapshotPurpose purpose) =>
        purpose == SnapshotPurpose.Report ? Permission.DocumentView : RequiredPermission(purpose);

    /// <summary>
    /// ADR-002 §9: once referenced, export, production and report snapshots stay for the life of the matter; bulk
    /// coding snapshots expire a retention period after their jobs finished.
    /// </summary>
    public static bool RetainedForMatter(SnapshotPurpose purpose) => purpose is not SnapshotPurpose.BulkCoding;

    /// <summary>A default display name such as "Mass Edit 2026-10-03 10:42" (UX note on E10-T02: a named "Frozen set").</summary>
    public static string DefaultName(SnapshotPurpose purpose, DateTimeOffset createdAt) =>
        (purpose switch
        {
            SnapshotPurpose.BulkCoding => "Mass Edit",
            SnapshotPurpose.Export => "Export",
            SnapshotPurpose.Production => "Production",
            SnapshotPurpose.Report => "Search Terms Report",
            _ => "Frozen set",
        }) + " " + createdAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
}
