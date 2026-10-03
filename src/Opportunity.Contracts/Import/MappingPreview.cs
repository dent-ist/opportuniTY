using System.Text.Json.Nodes;

namespace Opportunity.Contracts.Import;

/// <summary>
/// The <c>request</c> part of <c>POST …/import-mapping-previews</c> (multipart, next to the <c>file</c> part holding the
/// DAT or its leading bytes). Give a saved <see cref="ProfileId"/>, an inline <see cref="Profile"/> or neither (auto-map
/// only).
/// </summary>
public sealed record MappingPreviewRequest
{
    public Guid? ProfileId { get; init; }

    public ImportProfileDefinition? Profile { get; init; }

    /// <summary>Auto-map columns the profile does not mention (exact names and aliases).</summary>
    public bool AutoMap { get; init; } = true;

    /// <summary>Data rows to coerce, 1–100.</summary>
    public int Rows { get; init; } = 20;

    /// <summary>The file part holds only the leading bytes of the DAT, so its last record may be cut off and is dropped.</summary>
    public bool SampleIsPartial { get; init; }
}

public enum MatchKind
{
    /// <summary>Set by the profile or the user.</summary>
    Profile,

    /// <summary>The header equals the target name (case-insensitive).</summary>
    ExactName,

    /// <summary>The header equals the target name ignoring spaces, underscores and punctuation (<c>DATE_SENT</c>).</summary>
    NormalizedName,

    /// <summary>The header is a known alias of the target (<see cref="TargetPreview.Alias"/>).</summary>
    Alias,
}

public enum ColumnStatus
{
    Mapped,

    /// <summary>Explicitly "Do not import".</summary>
    Ignored,

    /// <summary>No mapping row and the profile ignores unmapped columns.</summary>
    Unmapped,

    /// <summary>No mapping row; stored as metadata in a new Text field.</summary>
    StoredAsNewTextField,

    /// <summary>A time column merged into a date column.</summary>
    MergedIntoDate,
}

/// <summary>How a profile's field reference was found in this workspace.</summary>
public enum TargetResolution
{
    Resolved,

    /// <summary>Custom field found by name; its id differs from the profile's (copied profile).</summary>
    ResolvedByName,

    /// <summary>Custom field found by id; it was renamed since the profile was saved.</summary>
    ResolvedById,

    /// <summary>A new field reuses an existing field of the same name and type.</summary>
    ExistingField,

    /// <summary>The load will create the field.</summary>
    WillCreate,

    /// <summary>Not usable; see the mapping issues.</summary>
    Unresolved,
}

public enum MappingIssueSeverity
{
    Warning,
    Error,
}

/// <summary>A profile- or column-level problem. Errors block the load; warnings are shown.</summary>
public sealed record MappingIssue(MappingIssueSeverity Severity, string Code, string Message, string? Column = null);

/// <summary>A delimiter as practitioners exchange it: the character, its code point and decimal code ("020/254/174").</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Wire names of the delimiter notation.")]
public sealed record DelimiterInfo(string Char, string Codepoint, int Decimal);

public sealed record LoadFilePreviewInfo(
    string Encoding,
    string EncodingSource,
    string Delimiters,
    DelimiterInfo Column,
    DelimiterInfo? Quote,
    DelimiterInfo? Newline,
    DelimiterInfo MultiValue,
    DelimiterInfo NestedValue,
    IReadOnlyList<string> Header,
    bool MisdecodeSuspected,
    IReadOnlyList<MappingIssue> ParserIssues);

public sealed record TargetPreview(
    MappingTarget Target,
    string Label,
    string Type,
    bool IsMultiValue,
    TargetResolution Resolution,
    MatchKind MatchedBy,
    string? Alias);

public sealed record ColumnPreview(
    string Column,
    int Index,
    ColumnStatus Status,
    IReadOnlyList<TargetPreview> Targets,
    string? MergedInto,
    IReadOnlyList<string> SampleValues,
    int ValueCount,
    int BlankCount,
    int ErrorCount,
    int WarningCount);

public sealed record CellError(string Code, string Message);

/// <summary>One coerced value of a preview row: <see cref="Value"/> is the canonical stored value, <see cref="Raw"/> the original string.</summary>
public sealed record CellPreview(
    string Column,
    string Target,
    string? Raw,
    JsonNode? Value,
    CellError? Error,
    IReadOnlyList<string> Warnings);

public sealed record RowPreview(
    long RowNumber,
    long LineNumber,
    string? ControlNumber,
    bool Rejected,
    IReadOnlyList<MappingIssue> ParserIssues,
    IReadOnlyList<CellPreview> Cells,
    int ErrorCount);

/// <summary>A field the load will create, with the choices it would add ("will create 12 choices").</summary>
public sealed record NewFieldPreview(string Name, ImportFieldType Type, bool IsMultiValue, int ChoicesToCreate, IReadOnlyList<string> SampleChoices);

public sealed record MappingPreviewResult(
    LoadFilePreviewInfo File,
    IReadOnlyList<ColumnPreview> Columns,
    IReadOnlyList<string> MissingColumns,
    IReadOnlyList<string> NewColumns,
    IReadOnlyList<RowPreview> Rows,
    IReadOnlyList<NewFieldPreview> NewFields,
    IReadOnlyList<MappingIssue> Issues,
    bool CanImport,
    ImportProfileDefinition EffectiveProfile);

/// <summary>A mapping target offered by the mapping grid. Structural targets come first (guide §5.1).</summary>
public sealed record ImportTargetResource(
    MappingTarget Target,
    string Label,
    string Type,
    bool IsMultiValue,
    bool IsStructural,
    bool IsCodingField,
    bool IsSecurityAffecting,
    bool AutoMapEligible,
    IReadOnlyList<string> Aliases);
