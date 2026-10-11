using Opportunity.Application.Audit;
using Opportunity.Core.Productions;
using Opportunity.Core.Redactions;

namespace Opportunity.Application.PrivilegeLogs;

/// <summary>How a log entry's document was kept from the receiving party (stored as smallint).</summary>
public enum PrivilegeLogEntryTreatment : short
{
    Withheld = 1,
    Redacted = 2,

    /// <summary>Produced with privacy redactions only; listed only when the template includes them.</summary>
    RedactedPrivacy = 3,
}

public enum PrivilegeLogSource : short
{
    Production = 1,
    Snapshot = 2,
}

public sealed record PrivilegeLogTemplateRecord(
    Guid WorkspaceId,
    Guid TemplateId,
    string Name,
    string DefinitionJson,
    long Version,
    Guid ModifiedBy,
    string ModifiedByDisplay,
    DateTimeOffset ModifiedAt);

public enum PrivilegeLogTemplateWriteStatus
{
    Applied,
    NotFound,
    NameConflict,
    VersionConflict,
}

public sealed record PrivilegeLogTemplateWrite(PrivilegeLogTemplateWriteStatus Status, PrivilegeLogTemplateRecord? Template = null);

/// <summary>What to read candidates from: a production (members, their families, an optional review set) or a frozen set.</summary>
public sealed record PrivilegeLogCandidateQuery(Guid WorkspaceId, Guid? ProductionId, Guid SnapshotId, Guid? ScopeSnapshotId, int Limit);

/// <summary>The reason of a produced redaction (as of the version frozen with the production).</summary>
public sealed record PrivilegeLogRedactionReason(string Code, string Name, RedactionReasonCategory Category);

/// <summary>A production member's frozen state.</summary>
public sealed record PrivilegeLogMember(
    long Sequence,
    ProductionOutputKind Output,
    string? ProdBegBates,
    string? ProdEndBates,
    int RedactionCount,
    IReadOnlyList<PrivilegeLogRedactionReason> RedactionReasons);

/// <summary>
/// A document that may belong on the log, in log order: a production member with produced redactions or a withheld
/// placeholder, a non-produced document coded Withhold (production families and review set), or a frozen-set member
/// coded Withhold or Redact. The rules (<c>PrivilegeLogRules</c>) decide; the caller filters by visibility first.
/// </summary>
/// <param name="StatusKey">The built-in key of the document's Privilege Status choice, if any.</param>
public sealed record PrivilegeLogCandidate(
    Guid DocumentId,
    string ControlNumber,
    Guid FamilyId,
    int FamilySequence,
    string? StatusKey,
    PrivilegeLogMember? Member);

/// <summary>A family member: its control number and, for a production member, its Bates numbers.</summary>
public sealed record PrivilegeLogFamilyMember(Guid FamilyId, Guid DocumentId, int FamilySequence, string? ProdBegBates, string? ProdEndBates);

/// <summary>A version to store: the series it extends and everything its files are rendered from.</summary>
public sealed record NewPrivilegeLogVersion
{
    public required Guid WorkspaceId { get; init; }

    public required Guid LogId { get; init; }

    public required string SeriesKey { get; init; }

    public required PrivilegeLogSource Source { get; init; }

    public Guid? ProductionId { get; init; }

    public required Guid SnapshotId { get; init; }

    public Guid? ScopeSnapshotId { get; init; }

    public Guid? TemplateId { get; init; }

    public required string TemplateName { get; init; }

    public required string TemplateDefinition { get; init; }

    public required string Metadata { get; init; }

    public required byte[] ContentSha256 { get; init; }

    public required byte[] CsvSha256 { get; init; }

    public required long CsvBytes { get; init; }

    public required byte[] XlsxSha256 { get; init; }

    public required long XlsxBytes { get; init; }

    public required int Withheld { get; init; }

    public required int Redacted { get; init; }

    public required int Excluded { get; init; }

    public required IReadOnlyList<int> FieldIds { get; init; }

    public required Guid GeneratedBy { get; init; }

    public required string GeneratedByDisplay { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<PrivilegeLogEntryRow> Entries { get; init; }
}

public sealed record PrivilegeLogEntryRow(int Ordinal, Guid DocumentId, PrivilegeLogEntryTreatment Treatment, IReadOnlyList<string> Cells);

public sealed record PrivilegeLogRecord
{
    public required Guid WorkspaceId { get; init; }

    public required Guid LogId { get; init; }

    public required string SeriesKey { get; init; }

    public required int Version { get; init; }

    public required PrivilegeLogSource Source { get; init; }

    public Guid? ProductionId { get; init; }

    public required Guid SnapshotId { get; init; }

    public Guid? ScopeSnapshotId { get; init; }

    public Guid? TemplateId { get; init; }

    public required string TemplateName { get; init; }

    public required string TemplateDefinition { get; init; }

    public required string Metadata { get; init; }

    public required byte[] ContentSha256 { get; init; }

    public required byte[] CsvSha256 { get; init; }

    public required long CsvBytes { get; init; }

    public required byte[] XlsxSha256 { get; init; }

    public required long XlsxBytes { get; init; }

    public required int EntryCount { get; init; }

    public required int Withheld { get; init; }

    public required int Redacted { get; init; }

    public required int Excluded { get; init; }

    public required IReadOnlyList<int> FieldIds { get; init; }

    public required Guid GeneratedBy { get; init; }

    public required string GeneratedByDisplay { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>The stored version, and whether the series' latest version already had this content (nothing was added).</summary>
public sealed record PrivilegeLogCreation(PrivilegeLogRecord Log, bool Unchanged);

/// <summary>
/// PostgreSQL storage of privilege log templates and versions (V0059). Every call runs in the workspace's RLS context.
/// Versions and entries are immutable; a generation's version, entries and <c>Privilege.LogGenerated</c> audit event are
/// written in one transaction.
/// </summary>
public interface IPrivilegeLogStore
{
    Task<IReadOnlyList<PrivilegeLogTemplateRecord>> ListTemplatesAsync(Guid workspaceId, CancellationToken cancellationToken = default);

    Task<PrivilegeLogTemplateRecord?> GetTemplateAsync(Guid workspaceId, Guid templateId, CancellationToken cancellationToken = default);

    /// <summary>Inserts the template and its audit event; a name used by another template (without case) is a conflict.</summary>
    Task<PrivilegeLogTemplateWrite> CreateTemplateAsync(
        Guid workspaceId, Guid templateId, string name, string definitionJson, Guid userId, string userDisplay, AuditEvent audit,
        CancellationToken cancellationToken = default);

    Task<PrivilegeLogTemplateWrite> UpdateTemplateAsync(
        Guid workspaceId, Guid templateId, long expectedVersion, string name, string definitionJson, Guid userId, string userDisplay, AuditEvent audit,
        CancellationToken cancellationToken = default);

    /// <summary>The candidates in log order, at most <see cref="PrivilegeLogCandidateQuery.Limit"/>, read in one snapshot.</summary>
    Task<IReadOnlyList<PrivilegeLogCandidate>> ReadCandidatesAsync(PrivilegeLogCandidateQuery query, CancellationToken cancellationToken = default);

    /// <summary>The live members of the families, with their Bates numbers in the production when one is given.</summary>
    Task<IReadOnlyList<PrivilegeLogFamilyMember>> ReadFamilyMembersAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> familyIds, Guid? productionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the next version of the series under a per-series lock, unless the latest version has the same content
    /// SHA-256: then that version is returned unchanged. <paramref name="audit"/> is written either way.
    /// </summary>
    Task<PrivilegeLogCreation> CreateVersionAsync(NewPrivilegeLogVersion version, AuditEvent audit, CancellationToken cancellationToken = default);

    Task<PrivilegeLogRecord?> GetAsync(Guid workspaceId, Guid logId, CancellationToken cancellationToken = default);

    /// <summary>Versions newest first, optionally of one production or frozen set, after the (generated at, id) position.</summary>
    Task<IReadOnlyList<PrivilegeLogRecord>> ListAsync(
        Guid workspaceId, Guid? productionId, Guid? snapshotId, (DateTimeOffset At, Guid LogId)? after, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Entries after <paramref name="afterOrdinal"/> in log order.</summary>
    Task<IReadOnlyList<PrivilegeLogEntryRow>> ReadEntriesAsync(
        Guid workspaceId, Guid logId, int afterOrdinal, int limit, CancellationToken cancellationToken = default);

    /// <summary>Every document a version lists.</summary>
    Task<IReadOnlyList<Guid>> ReadDocumentIdsAsync(Guid workspaceId, Guid logId, CancellationToken cancellationToken = default);
}
