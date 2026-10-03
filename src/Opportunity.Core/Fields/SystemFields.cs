namespace Opportunity.Core.Fields;

/// <summary>
/// System fields seeded into every workspace (ADR-003 R3): reserved ids 1–999, renameable and hideable but never
/// deleted or retyped. The ids are persisted in documents' raw-value keys and must never change.
/// Capabilities follow the structural-field table of ADR-007 §3.
/// </summary>
public static class SystemFields
{
    public const int ControlNumber = 1;
    public const int BegBates = 2;
    public const int EndBates = 3;
    public const int BegAttach = 4;
    public const int EndAttach = 5;
    public const int FileName = 6;
    public const int FileExtension = 7;
    public const int FileType = 8;
    public const int MimeType = 9;
    public const int FileSize = 10;
    public const int PageCount = 11;
    public const int DateSent = 12;
    public const int DateReceived = 13;
    public const int DateCreated = 14;
    public const int DateLastModified = 15;
    public const int DocumentDate = 16;
    public const int FamilyDate = 17;
    public const int Md5 = 18;
    public const int Sha1 = 19;
    public const int Sha256 = 20;
    public const int TextLength = 21;
    public const int TextTruncated = 22;
    public const int TextMissing = 23;
    public const int NativeMissing = 24;
    public const int ImagesIncomplete = 25;

    private const FieldCapabilities Exists = FieldCapabilities.Exists;
    private const FieldCapabilities Natural = FieldCapabilities.Sortable | FieldCapabilities.Filterable
        | FieldCapabilities.Rangeable | FieldCapabilities.Wildcard | Exists;
    private const FieldCapabilities Keyword = FieldCapabilities.Sortable | FieldCapabilities.Filterable
        | FieldCapabilities.Aggregatable | Exists;
    private const FieldCapabilities Numeric = FieldCapabilities.Sortable | FieldCapabilities.Filterable
        | FieldCapabilities.Rangeable | FieldCapabilities.Aggregatable | Exists;
    private const FieldCapabilities Flag = FieldCapabilities.Sortable | FieldCapabilities.Filterable
        | FieldCapabilities.Aggregatable | Exists;

    /// <summary>The seed set, in id order.</summary>
    public static IReadOnlyList<FieldDefinition> Create(Guid workspaceId) =>
    [
        Column(workspaceId, ControlNumber, "Control Number", FieldType.Keyword, "control_number", Natural),
        Column(workspaceId, BegBates, "Beg Bates", FieldType.Keyword, "beg_bates", Natural),
        Column(workspaceId, EndBates, "End Bates", FieldType.Keyword, "end_bates", Natural),
        Column(workspaceId, BegAttach, "Beg Attach", FieldType.Keyword, "beg_attach", FieldCapabilities.Filterable | Exists),
        Column(workspaceId, EndAttach, "End Attach", FieldType.Keyword, "end_attach", FieldCapabilities.Filterable | Exists),
        Column(workspaceId, FileName, "File Name", FieldType.Text, "file_name",
            FieldCapabilities.Sortable | FieldCapabilities.Filterable | FieldCapabilities.Wildcard | FieldCapabilities.LeadingWildcard | Exists),
        Column(workspaceId, FileExtension, "File Extension", FieldType.Keyword, "file_extension", Keyword),
        Column(workspaceId, FileType, "File Type", FieldType.Keyword, "file_type", Keyword),
        Column(workspaceId, MimeType, "MIME Type", FieldType.Keyword, "mime_type", Keyword),
        Column(workspaceId, FileSize, "File Size", FieldType.Integer, "file_size", Numeric),
        Column(workspaceId, PageCount, "Page Count", FieldType.Integer, "page_count", Numeric),
        Column(workspaceId, DateSent, "Date Sent", FieldType.Date, "date_sent", Numeric, Fields.DatePrecision.DateTime),
        Column(workspaceId, DateReceived, "Date Received", FieldType.Date, "date_received", Numeric, Fields.DatePrecision.DateTime),
        Column(workspaceId, DateCreated, "Date Created", FieldType.Date, "date_created", Numeric, Fields.DatePrecision.DateTime),
        Column(workspaceId, DateLastModified, "Date Last Modified", FieldType.Date, "date_last_modified", Numeric, Fields.DatePrecision.DateTime),
        Column(workspaceId, DocumentDate, "Document Date", FieldType.Date, "document_date", Numeric, Fields.DatePrecision.DateTime),
        Column(workspaceId, FamilyDate, "Family Date", FieldType.Date, "family_date", Numeric, Fields.DatePrecision.DateTime),
        Column(workspaceId, Md5, "MD5 Hash", FieldType.Keyword, "md5", FieldCapabilities.Filterable | Exists),
        Column(workspaceId, Sha1, "SHA-1 Hash", FieldType.Keyword, "sha1", FieldCapabilities.Filterable | Exists),
        Column(workspaceId, Sha256, "SHA-256 Hash", FieldType.Keyword, "sha256", FieldCapabilities.Filterable | Exists),
        Column(workspaceId, TextLength, "Text Length", FieldType.Integer, "text_length",
            FieldCapabilities.Sortable | FieldCapabilities.Filterable | FieldCapabilities.Rangeable | Exists),
        Column(workspaceId, TextTruncated, "Text Truncated", FieldType.Boolean, "text_truncated", Flag),
        Column(workspaceId, TextMissing, "Text Missing", FieldType.Boolean, "text_missing", Flag),
        Column(workspaceId, NativeMissing, "Native Missing", FieldType.Boolean, "native_missing", Flag),
        Column(workspaceId, ImagesIncomplete, "Images Incomplete", FieldType.Boolean, "images_incomplete", Flag),
    ];

    private static FieldDefinition Column(
        Guid workspaceId, int id, string name, FieldType type, string column, FieldCapabilities capabilities,
        DatePrecision? precision = null) => new()
        {
            WorkspaceId = workspaceId,
            FieldId = id,
            Name = name,
            Type = type,
            Storage = FieldStorage.Column,
            IsSystem = true,
            DatePrecision = precision,
            TextAnalysis = type == FieldType.Text ? TextAnalysis.Identifier : null,
            IsSearchable = true,
            Capabilities = capabilities,
            ColumnName = column,
        };
}
