namespace Opportunity.Contracts.Api;

/// <summary>
/// Body of <c>POST /api/v1/workspaces/{workspaceId}/exports</c>: export a Ready <c>Export</c> frozen set to a load-file
/// volume (<c>VOL001</c> with <c>DATA</c>, <c>NATIVES</c>, <c>TEXT</c> and <c>IMAGES</c> folders).
/// </summary>
/// <param name="SnapshotId">The frozen set (purpose Export) to export; its membership and order are the volume's.</param>
/// <param name="Fields">
/// DAT columns in order: a field by id, or a relationship/path column, each with an optional header (default: the
/// field name). Native and text path columns are appended when those files are included and not listed.
/// </param>
/// <param name="Name">Display name; default "Export yyyy-MM-dd HH:mm UTC".</param>
/// <param name="Delimiters">DAT delimiter preset, as for imports: <c>concordance</c> (default), <c>concordance-pilcrow</c> or <c>csv</c>.</param>
/// <param name="Encoding">DAT encoding: <c>utf-8</c> (with byte-order mark, default), <c>utf-16le</c> or <c>windows-1252</c>.</param>
/// <param name="TextEncoding">Extracted-text file encoding: <c>utf-8</c> (default) or <c>utf-16le</c> (with byte-order mark).</param>
/// <param name="PathSeparator">Separator of the relative paths in the DAT and OPT: <c>\</c> (default) or <c>/</c>.</param>
public sealed record CreateExportRequest(
    Guid SnapshotId,
    IReadOnlyList<ExportFieldRequest> Fields,
    string? Name = null,
    string? Delimiters = null,
    string? Encoding = null,
    bool IncludeNatives = true,
    bool IncludeText = true,
    bool IncludeImages = true,
    ExportVolumeRequest? Volume = null,
    string? PathSeparator = null,
    string? TextEncoding = null);

/// <summary>One DAT column: give exactly one of <paramref name="FieldId"/> and <paramref name="Column"/>.</summary>
/// <param name="Header">The column header; default the field name or the column's conventional header.</param>
public sealed record ExportFieldRequest(int? FieldId = null, ExportColumnResource? Column = null, string? Header = null);

/// <summary>Volume naming (<c>VOL</c> + start <c>1</c> padded to <c>3</c> → <c>VOL001</c>) and files per subfolder.</summary>
public sealed record ExportVolumeRequest(string? Prefix = null, int? Start = null, int? Padding = null, int? MaxFilesPerFolder = null);

/// <summary>DAT columns that are not plain field values.</summary>
public enum ExportColumnResource
{
    /// <summary>The control number of the family's top-level parent (default header <c>FamilyID</c>).</summary>
    FamilyId,

    /// <summary>The control number of the immediate parent; empty for top-level documents (default header <c>ParentID</c>).</summary>
    ParentId,

    /// <summary>Relative path of the exported native (default header <c>NativePath</c>).</summary>
    NativePath,

    /// <summary>Relative path of the exported extracted text (default header <c>TextPath</c>).</summary>
    TextPath,

    /// <summary>Productions only (E12-T05): the document's first Bates number (default header <c>ProdBegBates</c>).</summary>
    ProdBegBates,

    /// <summary>Productions only: the document's last Bates number (default header <c>ProdEndBates</c>).</summary>
    ProdEndBates,

    /// <summary>Productions only: the first Bates number of the document's family (default header <c>ProdBegAttach</c>).</summary>
    ProdBegAttach,

    /// <summary>Productions only: the last Bates number of the document's family (default header <c>ProdEndAttach</c>).</summary>
    ProdEndAttach,

    /// <summary>Productions only: the designation legend stamped on the pages (default header <c>Confidentiality</c>).</summary>
    Confidentiality,

    /// <summary>Productions only: <c>Yes</c> when redactions were burned into the produced images (default header <c>Redacted</c>).</summary>
    Redacted,

    /// <summary>Productions only: the number of produced images (default header <c>PageCount</c>).</summary>
    ProducedPages,
}

/// <summary>An export of a frozen set and its job (ADR-002, ADR-015 Q-15).</summary>
/// <param name="Report">Counts and the manifest checksum; null until the export completed.</param>
public sealed record ExportResource(
    Guid ExportId,
    Guid WorkspaceId,
    string Name,
    Guid SnapshotId,
    ExportResourceStatus Status,
    string? StatusReason,
    ExportSettingsResource Settings,
    ExportReportResource? Report,
    JobResource Job,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>The settings the export was frozen with.</summary>
public sealed record ExportSettingsResource(
    IReadOnlyList<ExportColumnSettingResource> Fields,
    string Delimiters,
    string Encoding,
    string TextEncoding,
    bool IncludeNatives,
    bool IncludeText,
    bool IncludeImages,
    string VolumeName,
    int MaxFilesPerFolder,
    string PathSeparator);

public sealed record ExportColumnSettingResource(int? FieldId, ExportColumnResource? Column, string Header);

/// <param name="DocumentsExcluded">Members removed by the access re-check at execution (Q-15); see the exclusions list.</param>
/// <param name="ManifestSha256">SHA-256 of <c>MANIFEST.json</c>, which lists every delivered file with its SHA-256.</param>
public sealed record ExportReportResource(
    long DocumentsExported,
    long DocumentsExcluded,
    long Natives,
    long Texts,
    long Images,
    long Pages,
    long Files,
    long TotalBytes,
    string ManifestSha256);

public enum ExportResourceStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>One delivered file of a completed export; download it by <see cref="FileId"/>.</summary>
/// <param name="Path">Relative path inside the package, <c>/</c>-separated (e.g. <c>VOL001/NATIVES/NATIVE0001/ABC0001.pdf</c>).</param>
public sealed record ExportFileResource(Guid FileId, string Path, ExportFileResourceKind Kind, long SizeBytes, string Sha256);

public enum ExportFileResourceKind
{
    Native,
    Text,
    Image,
    Dat,
    Opt,
    Manifest,
    Report,
}

/// <summary>A frozen-set member the export left out because the initiator could no longer access it (Q-15).</summary>
/// <param name="Reason">Always the generic <c>AccessChanged</c>; the precise reason is in the audit trail only.</param>
public sealed record ExportExclusionResource(long Ordinal, Guid DocumentId, string? ControlNumber, string Reason);
