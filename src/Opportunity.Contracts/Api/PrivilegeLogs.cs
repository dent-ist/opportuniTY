using System.Text.Json.Serialization;

namespace Opportunity.Contracts.Api;

/// <summary>What a privilege log column holds (E13-T03).</summary>
public enum PrivilegeLogColumnKind
{
    /// <summary>The log identifier: the produced Bates number (or the placeholder's Bates), else a Priv ID (<c>PRIV0001</c>).</summary>
    PrivId,

    /// <summary>The first produced Bates number (empty for a document that was not produced).</summary>
    BegBates,

    /// <summary>The last produced Bates number.</summary>
    EndBates,

    ControlNumber,

    /// <summary>First and last log identifier of the document's family (members you may see that are produced or logged).</summary>
    FamilyRange,

    /// <summary><c>Withheld</c>, <c>Redacted</c> or <c>Redacted (Privacy)</c>.</summary>
    Treatment,

    /// <summary>Privilege Basis; for a redacted document without one, the privilege redaction reasons.</summary>
    Basis,

    /// <summary>The Subject field's value, else the file name.</summary>
    SubjectOrFileName,

    /// <summary>The names of the reasons of the document's produced redactions.</summary>
    RedactionReasons,

    /// <summary>A field's value (<see cref="PrivilegeLogColumn.FieldId"/>).</summary>
    Field,
}

/// <summary>Built-in column sets (E13-T03 formats 1 and 2; categorical logs are deferred, Q-20).</summary>
public enum PrivilegeLogPreset
{
    /// <summary>Priv ID, Date, Author, From, To, CC, BCC, Subject/File Name, Doc Type, Basis, Description, Attorneys, Family Range, Withheld/Redacted.</summary>
    DocumentByDocument,

    /// <summary>The same without the reviewer-written Description and Attorneys Involved columns.</summary>
    MetadataOnly,
}

/// <param name="Header">Column header in the files (default: the kind's name, or the field's name).</param>
/// <param name="FieldId">The field of a <see cref="PrivilegeLogColumnKind.Field"/> column.</param>
public sealed record PrivilegeLogColumn(PrivilegeLogColumnKind Kind, string? Header = null, int? FieldId = null);

/// <summary>
/// A recorded exclusion rule (e.g. post-complaint communications with outside counsel): a document matching every given
/// condition is left off the log, and the rule with the number of documents it excluded is recorded in the log metadata.
/// </summary>
/// <param name="Label">Why the documents are excluded, as stated in the log metadata (1–200 characters).</param>
/// <param name="DateFieldId">The date field <see cref="OnOrAfter"/> and <see cref="Before"/> test (default Document Date).</param>
/// <param name="OnOrAfter">Matches documents dated on or after this day (in the template's time zone).</param>
/// <param name="Before">Matches documents dated before this day.</param>
/// <param name="LogCategoryChoiceIds">Matches documents whose Log Category is one of these choices.</param>
/// <param name="AttorneysInvolved">Matches documents whose Attorneys Involved holds one of these names (compared without case).</param>
public sealed record PrivilegeLogExclusionRule(
    string Label,
    int? DateFieldId = null,
    DateOnly? OnOrAfter = null,
    DateOnly? Before = null,
    IReadOnlyList<int>? LogCategoryChoiceIds = null,
    IReadOnlyList<string>? AttorneysInvolved = null);

/// <summary>
/// A privilege log template (E13-T03): the columns and how values are written. Absent members take their defaults; a
/// stored template has every member filled in (canonical form).
/// </summary>
/// <param name="Columns">Columns in order (1–40). Default: the preset's columns resolved in this workspace.</param>
/// <param name="Preset">Fills <see cref="Columns"/> when none are given (default document-by-document).</param>
/// <param name="PrivIdPrefix">Prefix of the Priv IDs of withheld documents without a Bates number (default <c>PRIV</c>).</param>
/// <param name="PrivIdStart">First Priv ID number (default 1).</param>
/// <param name="PrivIdPadding">Digits of the Priv ID number (1–10, default 4).</param>
/// <param name="DateFormat">.NET-style pattern of date values (default <c>yyyy-MM-dd</c>).</param>
/// <param name="TimeZone">IANA zone dates are written and exclusion rules compared in (default <c>UTC</c>).</param>
/// <param name="MultiValueSeparator">Separator of multiple values in one cell (default <c>; </c>).</param>
/// <param name="IncludePrivacyRedactions">
/// List produced documents whose redactions are all for privacy (never privilege) as <c>Redacted (Privacy)</c>; by
/// default they are left out (a privacy redaction log is not a privilege log).
/// </param>
/// <param name="ExclusionRules">Recorded exclusion rules (at most 20).</param>
public sealed record PrivilegeLogTemplateDefinition(
    IReadOnlyList<PrivilegeLogColumn>? Columns = null,
    PrivilegeLogPreset? Preset = null,
    string? PrivIdPrefix = null,
    long? PrivIdStart = null,
    int? PrivIdPadding = null,
    string? DateFormat = null,
    string? TimeZone = null,
    string? MultiValueSeparator = null,
    bool IncludePrivacyRedactions = false,
    IReadOnlyList<PrivilegeLogExclusionRule>? ExclusionRules = null);

/// <summary>Body of <c>POST</c> and <c>PUT …/privilege-log-templates</c>.</summary>
public sealed record PrivilegeLogTemplateRequest(string Name, PrivilegeLogTemplateDefinition? Definition = null);

public sealed record PrivilegeLogTemplateResource(
    Guid TemplateId,
    string Name,
    PrivilegeLogTemplateDefinition Definition,
    ReviewerResource ModifiedBy,
    DateTimeOffset ModifiedAt,
    long Version);

/// <summary>A preset resolved in this workspace (columns whose fields the workspace lacks are left out).</summary>
public sealed record PrivilegeLogPresetResource(PrivilegeLogPreset Preset, string Name, PrivilegeLogTemplateDefinition Definition);

/// <summary><c>GET …/privilege-log-templates</c>: the workspace's templates by name and the built-in presets.</summary>
public sealed record PrivilegeLogTemplateList(IReadOnlyList<PrivilegeLogTemplateResource> Items, IReadOnlyList<PrivilegeLogPresetResource> Presets);

/// <summary>
/// Body of <c>POST …/privilege-logs</c>: generate a log from a finalized production (<see cref="ProductionId"/>, with
/// an optional review set <see cref="SnapshotId"/> whose withheld documents were not produced), from a frozen set
/// (<see cref="SnapshotId"/> alone) or from a saved search (<see cref="SavedSearchId"/> alone: run now as you see it and
/// frozen), with a template (<see cref="TemplateId"/>) or a preset (default document-by-document).
/// </summary>
public sealed record GeneratePrivilegeLogRequest(
    Guid? ProductionId = null,
    Guid? SnapshotId = null,
    Guid? TemplateId = null,
    PrivilegeLogPreset? Preset = null,
    Guid? SavedSearchId = null);

public enum PrivilegeLogSourceKind
{
    Production,
    Snapshot,
    SavedSearch,
}

public enum PrivilegeLogTreatment
{
    Withheld,
    Redacted,
    RedactedPrivacy,
}

/// <summary>An exclusion rule as applied: its conditions in words and how many documents it left off the log.</summary>
public sealed record PrivilegeLogAppliedRule(
    string Label,
    string? DateField,
    DateOnly? OnOrAfter,
    DateOnly? Before,
    IReadOnlyList<string> LogCategories,
    IReadOnlyList<string> AttorneysInvolved,
    int ExcludedDocuments);

/// <summary>
/// The frozen metadata of a log version: everything its files state besides the rows. Holds no times or user names, so
/// two generations over the same inputs have the same metadata and files.
/// </summary>
public sealed record PrivilegeLogMetadata(
    int FormatVersion,
    PrivilegeLogSourceKind Source,
    Guid? ProductionId,
    string? ProductionName,
    int? ProductionVersion,
    Guid SnapshotId,
    Guid? ReviewSetSnapshotId,
    Guid? TemplateId,
    string TemplateName,
    IReadOnlyList<string> Columns,
    bool PrivacyRedactionsIncluded,
    IReadOnlyList<PrivilegeLogAppliedRule> ExclusionRules,
    int Entries,
    int Withheld,
    int Redacted,
    int RedactedPrivacy,
    int ExcludedByRules,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SavedSearchId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SavedSearchName = null);

/// <summary>A log file's SHA-256 (hex) and size.</summary>
public sealed record PrivilegeLogFileResource(string Format, string Sha256, long Bytes);

/// <summary>
/// One version of a privilege log. <see cref="ContentSha256"/> identifies its content (metadata and rows); the files'
/// own SHA-256 are what a download is verified against before it is sent.
/// </summary>
/// <param name="SavedSearchId">The saved search of a saved-search log.</param>
/// <param name="DescriptionColumn">
/// The index of the column that holds the Privilege Description field (the reviewer-written per-document description,
/// Q-20), if the version has one: what the description of an entry is edited through.
/// </param>
public sealed record PrivilegeLogResource(
    Guid LogId,
    int Version,
    PrivilegeLogSourceKind Source,
    Guid? ProductionId,
    Guid SnapshotId,
    Guid? ReviewSetSnapshotId,
    Guid? TemplateId,
    string TemplateName,
    string ContentSha256,
    IReadOnlyList<PrivilegeLogFileResource> Files,
    PrivilegeLogMetadata Metadata,
    ReviewerResource GeneratedBy,
    DateTimeOffset GeneratedAt,
    Guid? SavedSearchId = null,
    int? DescriptionColumn = null);

/// <summary>Response of <c>POST …/privilege-logs</c>: the version, and whether it is the unchanged latest one.</summary>
public sealed record PrivilegeLogGenerationResource(PrivilegeLogResource Log, bool Unchanged);

/// <summary>One row of a log version, its cells in column order.</summary>
public sealed record PrivilegeLogEntryResource(int Ordinal, Guid DocumentId, PrivilegeLogTreatment Treatment, IReadOnlyList<string> Cells);

public enum PrivilegeLogChangeKind
{
    /// <summary>Listed now, not in the earlier version.</summary>
    Added,

    /// <summary>In the earlier version, not listed now.</summary>
    Removed,

    /// <summary>Listed in both, with different cells or treatment.</summary>
    Changed,
}

/// <summary>A cell of an entry that differs between the versions (by column header).</summary>
public sealed record PrivilegeLogCellChange(string Column, string Before, string After);

/// <summary>
/// How one document's entry differs between two versions. <see cref="Cells"/> is the row as listed now (an added or
/// changed entry) or as it was listed (a removed one); <see cref="Ordinal"/> and <see cref="PreviousOrdinal"/> are its
/// positions in the two versions.
/// </summary>
public sealed record PrivilegeLogEntryChange(
    PrivilegeLogChangeKind Kind,
    Guid DocumentId,
    int? Ordinal,
    int? PreviousOrdinal,
    PrivilegeLogTreatment Treatment,
    IReadOnlyList<string> Cells,
    IReadOnlyList<PrivilegeLogCellChange> ChangedCells);

/// <summary>
/// <c>GET …/privilege-logs/{logId}/changes</c>: what changed since an earlier version (by default the previous version
/// of the same log), entry by entry and keyed by document: added, removed and changed entries in log order (removed
/// ones after the others, in their earlier order). Cells are compared by column header; columns only one version has
/// are named in <see cref="ColumnsAdded"/> and <see cref="ColumnsRemoved"/>. Without an earlier version
/// <see cref="PreviousLogId"/> is null and nothing is listed.
/// </summary>
public sealed record PrivilegeLogChangesResource(
    Guid LogId,
    int Version,
    Guid? PreviousLogId,
    int? PreviousVersion,
    int Added,
    int Removed,
    int Changed,
    int Unchanged,
    IReadOnlyList<string> ColumnsAdded,
    IReadOnlyList<string> ColumnsRemoved,
    IReadOnlyList<PrivilegeLogEntryChange> Items,
    string? NextCursor);
