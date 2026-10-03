namespace Opportunity.Contracts.Import;

/// <summary>
/// How a load treats documents that already exist (guide §5.1): <c>append</c> creates new documents only,
/// <c>overlay</c> updates existing ones only, <c>appendOverlay</c> does both.
/// </summary>
public enum ImportMode
{
    Append,
    Overlay,
    AppendOverlay,
}

/// <summary>What happens to load-file columns that no mapping row covers.</summary>
public enum UnmappedColumnPolicy
{
    /// <summary>Not imported; the preview lists them as ignored.</summary>
    Ignore,

    /// <summary>Stored as metadata in a new Text field named after the column (created when the load runs).</summary>
    CreateTextField,
}

public enum MappingTargetKind
{
    /// <summary>An existing field definition (system fields such as Control Number have fixed ids below 1000).</summary>
    Field,

    /// <summary>A structural value that is not a field definition (family links, file paths, upstream group ids).</summary>
    Structural,

    /// <summary>A field the load creates; an existing field with the same name is reused when its type matches.</summary>
    NewField,
}

/// <summary>Structural targets of a load file that are not field definitions (ADR-009, E08-T04, E09-T02, P-8).</summary>
public enum StructuralTarget
{
    /// <summary>Control number of the immediate parent document.</summary>
    ParentId,

    /// <summary>Family / group identifier shared by a parent and its attachments.</summary>
    GroupId,

    NativePath,
    TextPath,
    FolderPath,

    /// <summary>Upstream duplicate group identifier.</summary>
    DuplicateGroupId,

    /// <summary>Upstream de-duplication hash (hex).</summary>
    DedupeHash,

    /// <summary>Upstream email thread group identifier.</summary>
    EmailThreadId,

    /// <summary>Upstream email hash (hex); the dedupe key of an email when no dedupe hash is mapped.</summary>
    EmailHash,
}

/// <summary>Type of a field a load creates (the nine field types of §6; dates split by precision).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names of the §6 field types.")]
public enum ImportFieldType
{
    Text,
    Keyword,
    Integer,
    Decimal,
    Date,
    DateTime,
    Boolean,
    SingleChoice,
    MultiChoice,
}

/// <summary>
/// An import profile (guide §5.1, ticket review E08-T02): delimiters and encodings, field mapping, per-column parsing,
/// import mode, overlay and path settings. Saved per workspace and exchanged as JSON; it never stores file content.
/// </summary>
public sealed record ImportProfileDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public LoadFileSettings LoadFile { get; init; } = new();

    public ParsingDefaults Parsing { get; init; } = new();

    public ImportMode Mode { get; init; } = ImportMode.Append;

    public OverlaySettings Overlay { get; init; } = new();

    public PathSettings Paths { get; init; } = new();

    public UnmappedColumnPolicy UnmappedColumns { get; init; } = UnmappedColumnPolicy.Ignore;

    /// <summary>Optional prefix prepended to every control number of the load (Q-27).</summary>
    public string? ControlNumberPrefix { get; init; }

    public RelationshipSettings Relationships { get; init; } = new();

    /// <summary>One row per load-file column, by header name.</summary>
    public IReadOnlyList<ColumnMapping> Columns { get; init; } = [];
}

/// <summary>Upstream duplicate and email-thread options (ADR-009 R13-R19, Q-09).</summary>
public sealed record RelationshipSettings
{
    /// <summary>
    /// Without a mapped email thread group, group emails by the 22-byte header of their Conversation Index. Off by
    /// default: the header is shared by every reply in an Outlook conversation, including forks other tools split.
    /// </summary>
    public bool DeriveEmailThreadFromConversationIndex { get; init; }
}

/// <summary>
/// Delimiters and encodings. <see cref="Delimiters"/> names a preset (<c>concordance</c>, <c>concordance-pilcrow</c>,
/// <c>csv</c>) or <c>custom</c>; a set character overrides the preset. Characters are given as the character itself or
/// as a decimal code (<c>"020"</c>, <c>"254"</c>, <c>"174"</c>) or <c>U+00FE</c>.
/// </summary>
public sealed record LoadFileSettings
{
    public string Delimiters { get; init; } = "concordance";

    public string? Column { get; init; }

    public string? Quote { get; init; }

    public string? Newline { get; init; }

    public string? MultiValue { get; init; }

    public string? NestedValue { get; init; }

    /// <summary><c>auto</c>, <c>utf-8</c>, <c>utf-16le</c>, <c>utf-16be</c> or <c>windows-1252</c>.</summary>
    public string DatEncoding { get; init; } = "auto";

    /// <summary>Encoding of extracted-text files; same values as <see cref="DatEncoding"/>.</summary>
    public string TextEncoding { get; init; } = "auto";

    public bool FirstLineContainsFieldNames { get; init; } = true;

    /// <summary>Convert the newline character (<c>®</c>) to a line break; false keeps it literally.</summary>
    public bool ConvertNewlineCharacter { get; init; } = true;
}

/// <summary>Defaults for every column; a column's <see cref="ColumnParsing"/> overrides them.</summary>
public sealed record ParsingDefaults
{
    /// <summary><c>en-US</c> or <c>en-GB</c> (Q-28): default date order and number grouping.</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>Date formats tried in order; empty means the locale's. See <see cref="ColumnParsing.DateFormats"/>.</summary>
    public IReadOnlyList<string> DateFormats { get; init; } = [];

    /// <summary>IANA zone of date-times without an offset.</summary>
    public string SourceTimeZone { get; init; } = "UTC";

    /// <summary>Unparseable dates are left empty with a warning instead of a row error.</summary>
    public bool UnparseableDatesAsBlank { get; init; }

    /// <summary>Extra Yes/No tokens on top of Y/N, Yes/No, True/False, T/F, 1/0.</summary>
    public IReadOnlyList<string> TrueValues { get; init; } = [];

    public IReadOnlyList<string> FalseValues { get; init; } = [];
}

public sealed record OverlaySettings
{
    /// <summary>Field that identifies existing documents on overlay; null is Control Number.</summary>
    public int? KeyFieldId { get; init; }

    /// <summary>Q-31: coding and privilege fields are only mappable when an admin enables this (audited per load).</summary>
    public bool AllowCodingFieldOverlay { get; init; }
}

public sealed record PathSettings
{
    /// <summary>Volume root that relative native/text/image paths resolve against.</summary>
    public string? VolumeRoot { get; init; }

    /// <summary>Leading path portion removed from native/text paths before resolving (e.g. <c>\\server\export\</c>).</summary>
    public string? StripPrefix { get; init; }
}

/// <summary>One load-file column: ignored, or mapped to one or more targets (e.g. <c>BEGDOC</c> → Control Number and Beg Bates).</summary>
public sealed record ColumnMapping
{
    public string Column { get; init; } = string.Empty;

    /// <summary>Explicit "Do not import".</summary>
    public bool Ignore { get; init; }

    public IReadOnlyList<MappingTarget> Targets { get; init; } = [];

    public ColumnParsing? Parsing { get; init; }
}

public sealed record MappingTarget
{
    public MappingTargetKind Kind { get; init; }

    /// <summary>Kind <c>field</c>: field id. System fields (below 1000) resolve by id; custom fields by name first.</summary>
    public int? FieldId { get; init; }

    /// <summary>Kind <c>field</c>: field name, so a profile copied to another workspace finds the same field.</summary>
    public string? FieldName { get; init; }

    public StructuralTarget? Structural { get; init; }

    public NewFieldSpec? NewField { get; init; }
}

public sealed record NewFieldSpec
{
    public string Name { get; init; } = string.Empty;

    public ImportFieldType Type { get; init; } = ImportFieldType.Text;

    /// <summary>Text and Keyword only; MultiChoice is always multi-valued.</summary>
    public bool IsMultiValue { get; init; }

    public short? DecimalPrecision { get; init; }

    public short? DecimalScale { get; init; }
}

/// <summary>Per-column parsing; null members fall back to <see cref="ParsingDefaults"/>.</summary>
public sealed record ColumnParsing
{
    /// <summary>
    /// Date formats tried in order: <c>MM/DD/YYYY</c>, <c>DD/MM/YYYY</c>, <c>YYYYMMDD</c>, <c>ISO 8601</c>, or a .NET
    /// custom pattern such as <c>dd.MM.yyyy</c>. ISO 8601 (with or without offset) is always accepted.
    /// </summary>
    public IReadOnlyList<string>? DateFormats { get; init; }

    /// <summary>Companion time column merged into this date (e.g. <c>TimeSent</c> for <c>DateSent</c>).</summary>
    public string? TimeColumn { get; init; }

    /// <summary>Formats of the time column: <c>HH:mm:ss</c>, <c>HH:mm</c>, <c>hh:mm tt</c>, <c>hh:mm:ss tt</c>; empty tries all.</summary>
    public IReadOnlyList<string>? TimeFormats { get; init; }

    public string? SourceTimeZone { get; init; }

    /// <summary>Multi-value separator for this column; null uses the delimiter profile's.</summary>
    public string? MultiValueDelimiter { get; init; }

    public IReadOnlyList<string>? TrueValues { get; init; }

    public IReadOnlyList<string>? FalseValues { get; init; }

    /// <summary>Choice targets: create missing choices from the distinct values instead of a row error.</summary>
    public bool? CreateMissingChoices { get; init; }
}

/// <summary>Body of create and replace. An exported profile resource can be posted as is to copy it to another workspace.</summary>
public sealed record ImportProfileWrite
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public ImportProfileDefinition? Definition { get; init; }
}

/// <summary>A saved import profile. <see cref="Version"/> is also the <c>ETag</c>.</summary>
public sealed record ImportProfileResource(
    Guid ProfileId,
    Guid WorkspaceId,
    string Name,
    string? Description,
    ImportProfileDefinition Definition,
    long Version,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record ImportProfileSummary(
    Guid ProfileId,
    string Name,
    string? Description,
    ImportMode Mode,
    int ColumnCount,
    long Version,
    DateTimeOffset UpdatedAt);
