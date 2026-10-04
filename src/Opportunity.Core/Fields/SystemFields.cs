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

    // Relationship columns (ADR-009 §3-§4): set from upstream identifiers and the dedupe/thread writers, never mapped.
    public const int DuplicateGroup = 26;
    public const int DuplicatePrimary = 27;
    public const int EmailThreadGroup = 28;

    // Upstream dedupe and threading values (ADR-009 R13, R19): Metadata-storage system fields, imported verbatim and
    // never recomputed. Their search slots are reserved at the top of each kind's budget (see ReservedSlots).
    public const int AllCustodians = 29;
    public const int DuplicateCustodians = 30;
    public const int AllPaths = 31;
    public const int DuplicatePaths = 32;
    public const int ConversationIndex = 33;
    public const int ConversationTopic = 34;
    public const int InclusiveEmail = 35;
    public const int ThreadSortOrder = 36;

    /// <summary>
    /// Fixed search slots of the Metadata-storage system fields: the same in every workspace, taken from the top of the
    /// kind's budget so lowest-free allocation of custom fields (ADR-007 R4) is unaffected.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> ReservedSlots = new Dictionary<int, string>
    {
        [AllCustodians] = "kw.s300",
        [DuplicateCustodians] = "kw.s299",
        [ConversationIndex] = "kw.s298",
        [ThreadSortOrder] = "kw.s297",
        [AllPaths] = "idt.s050",
        [DuplicatePaths] = "idt.s049",
        [ConversationTopic] = "txt.s100",
        [InclusiveEmail] = "bool.s100",
    };

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
        Column(workspaceId, DuplicateGroup, "Duplicate Group", FieldType.Keyword, "duplicate_group_id", Keyword),
        Column(workspaceId, DuplicatePrimary, "Duplicate Primary", FieldType.Boolean, "is_duplicate_primary", Flag),
        Column(workspaceId, EmailThreadGroup, "Email Thread Group", FieldType.Keyword, "email_thread_id", Keyword),
        Metadata(workspaceId, AllCustodians, "All Custodians", FieldType.Keyword, multi: true),
        Metadata(workspaceId, DuplicateCustodians, "Duplicate Custodians", FieldType.Keyword, multi: true),
        Metadata(workspaceId, AllPaths, "All Paths", FieldType.Text, multi: true, Fields.TextAnalysis.Identifier),
        Metadata(workspaceId, DuplicatePaths, "Duplicate Paths", FieldType.Text, multi: true, Fields.TextAnalysis.Identifier),
        Metadata(workspaceId, ConversationIndex, "Conversation Index", FieldType.Keyword),
        Metadata(workspaceId, ConversationTopic, "Conversation Topic", FieldType.Text, analysis: Fields.TextAnalysis.Prose),
        Metadata(workspaceId, InclusiveEmail, "Inclusive Email", FieldType.Boolean),
        Metadata(workspaceId, ThreadSortOrder, "Thread Sort Order", FieldType.Keyword),
    ];

    private static FieldDefinition Metadata(
        Guid workspaceId, int id, string name, FieldType type, bool multi = false, TextAnalysis? analysis = null) => new()
        {
            WorkspaceId = workspaceId,
            FieldId = id,
            Name = name,
            Type = type,
            Storage = FieldStorage.Metadata,
            IsSystem = true,
            IsMultiValue = multi,
            TextAnalysis = analysis,
            IsSearchable = true,
            SearchSlot = ReservedSlots[id],
            Capabilities = FieldRules.CapabilitiesForSlot(ReservedSlots[id]),
        };

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
