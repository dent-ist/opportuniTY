using System.Text;

using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;

namespace Opportunity.Import.Mapping;

/// <summary>A field the auto-mapper proposes to create when a well-known column has no field yet (guide §5.1).</summary>
/// <param name="Name">Name of the new field.</param>
/// <param name="Aliases">Header names recognized, in priority order.</param>
public sealed record WellKnownField(string Name, ImportFieldType Type, bool IsMultiValue, IReadOnlyList<string> Aliases);

/// <summary>
/// The structural targets, mappable system fields and their aliases (Q-26 alias maps for common review/processing
/// platform exports and generic Concordance/CSV loads, without vendor names in user-facing strings, Q-51). Aliases
/// are listed in priority order: when several columns match a target, the earliest alias wins.
/// </summary>
public static class ImportTargets
{
    /// <summary>
    /// System fields the platform computes (text length, missing/truncated flags) or derives from structural targets
    /// (duplicate group, primary flag, email thread); never mapped from a load file.
    /// </summary>
    public static readonly IReadOnlySet<int> ComputedSystemFields = new HashSet<int>
    {
        SystemFields.TextLength, SystemFields.TextTruncated, SystemFields.TextMissing, SystemFields.NativeMissing, SystemFields.ImagesIncomplete,
        SystemFields.DuplicateGroup, SystemFields.DuplicatePrimary, SystemFields.EmailThreadGroup,
    };

    /// <summary>System hash fields, validated as lower-case hex of this many digits.</summary>
    public static readonly IReadOnlyDictionary<int, int> HashHexLength = new Dictionary<int, int>
    {
        [SystemFields.Md5] = 32,
        [SystemFields.Sha1] = 40,
        [SystemFields.Sha256] = 64,
    };

    public static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> SystemFieldAliases = new Dictionary<int, IReadOnlyList<string>>
    {
        [SystemFields.ControlNumber] =
        [
            "ControlNumber", "Control No", "Control #", "DocID", "Doc ID", "Document ID", "DocumentID", "DocNo", "Doc Number",
            "BEGDOC", "BegDoc#", "BEGNO", "BegBates", "Beg Bates", "Bates Begin", "Begin Bates",
        ],
        [SystemFields.BegBates] =
        [
            "BegBates", "Bates Begin", "BatesBegin", "Begin Bates", "BeginBates", "Bates Beg", "BegProd", "ProdBeg", "ProdBegBates",
            "Production Begin", "Prod Begin", "BEGDOC", "BegDoc#", "BEGNO",
        ],
        [SystemFields.EndBates] =
        [
            "EndBates", "Bates End", "BatesEnd", "End Bates", "EndProd", "ProdEnd", "ProdEndBates", "Production End", "Prod End",
            "ENDDOC", "EndDoc#", "ENDNO",
        ],
        [SystemFields.BegAttach] =
        [
            "BegAttach", "BegAttach#", "Begin Attachment", "BeginAttachment", "Beg Attachment", "BeginAttach", "Attach Begin",
            "AttachBegin", "BegAtt", "Beg Family", "BegFamily", "Begin Family", "ProdBegAttach", "BegAttachBates",
        ],
        [SystemFields.EndAttach] =
        [
            "EndAttach", "EndAttach#", "End Attachment", "EndAttachment", "Attach End", "AttachEnd", "EndAtt", "End Family",
            "EndFamily", "ProdEndAttach", "EndAttachBates",
        ],
        [SystemFields.FileName] =
        [
            "FileName", "Filename", "File_Name", "Original File Name", "OriginalFileName", "Native File Name", "NativeFileName",
            "Document Name", "DocName",
        ],
        [SystemFields.FileExtension] = ["FileExt", "File Ext", "Extension", "Ext", "Document Extension", "DocExt", "Native Extension"],
        [SystemFields.FileType] = ["FileType", "File Description", "FileDescription", "Document Type", "DocType", "Doc Type"],
        [SystemFields.MimeType] = ["MimeType", "Mime", "Content Type", "ContentType"],
        [SystemFields.FileSize] = ["FileSize", "Size", "Native Size", "NativeFileSize", "Native File Size", "File Size (bytes)"],
        [SystemFields.PageCount] = ["PageCount", "PGCOUNT", "Pg Count", "PgCount", "Pages", "NumPages", "Number of Pages", "Image Count"],
        [SystemFields.DateSent] = ["DateSent", "Sent Date", "SentDate", "Sent On", "SentOn", "Email Date Sent", "EmailDateSent", "DateTimeSent"],
        [SystemFields.DateReceived] =
            ["DateRcvd", "Date Rcvd", "DateReceived", "Received Date", "ReceivedDate", "Received On", "ReceivedOn", "DateTimeReceived"],
        [SystemFields.DateCreated] = ["DateCreated", "Created Date", "CreatedDate", "Creation Date", "CreationDate", "CreateDate", "DateTimeCreated"],
        [SystemFields.DateLastModified] =
        [
            "DateLastMod", "Date Last Mod", "DateLastModified", "Last Modified Date", "LastModDate", "Last Modified", "LastModified",
            "Date Modified", "DateModified", "Modified Date", "ModifiedDate", "DateMod",
        ],
        [SystemFields.DocumentDate] = ["DocDate", "Doc Date", "DocumentDate", "Primary Date", "PrimaryDate"],
        [SystemFields.FamilyDate] = ["FamilyDate", "Family Date", "SortDate", "Sort Date", "ParentDate", "Parent Date", "Group Date", "GroupDate"],
        [SystemFields.Md5] = ["MD5Hash", "MD5", "MD5 Hash", "Hash MD5", "HashMD5", "MD5_HASH"],
        [SystemFields.Sha1] = ["SHA1Hash", "SHA1", "SHA-1", "SHA1 Hash", "Hash SHA1", "HashSHA1", "SHA1_HASH"],
        [SystemFields.Sha256] = ["SHA256Hash", "SHA256", "SHA-256", "SHA256 Hash", "Hash SHA256", "HashSHA256", "SHA256_HASH"],
        [SystemFields.AllCustodians] =
            ["AllCustodians", "All Custodians", "Custodians", "Custodian(s)", "Custodians All", "CustodianAll", "All Custodian", "DeDupedCustodians"],
        [SystemFields.DuplicateCustodians] =
            ["DuplicateCustodians", "Duplicate Custodians", "DupCustodians", "Dup Custodians", "Other Custodians", "OtherCustodians", "Dupe Custodians"],
        [SystemFields.AllPaths] = ["AllPaths", "All Paths", "AllFilePaths", "All File Paths", "All Locations", "AllLocations", "DeDupedPaths"],
        [SystemFields.DuplicatePaths] = ["DuplicatePaths", "Duplicate Paths", "DupPaths", "Dup Paths", "Duplicate File Paths", "Other Paths"],
        [SystemFields.ConversationIndex] = ["ConversationIndex", "Conversation Index", "ConvIndex", "Conv Index", "ThreadIndex", "Thread Index", "PR_CONVERSATION_INDEX"],
        [SystemFields.ConversationTopic] = ["ConversationTopic", "Conversation Topic", "ConvTopic", "Thread Topic", "ThreadTopic", "PR_CONVERSATION_TOPIC"],
        [SystemFields.InclusiveEmail] = ["InclusiveEmail", "Inclusive Email", "Inclusive", "Email Inclusive", "IsInclusive", "Is Inclusive"],
        [SystemFields.ThreadSortOrder] = ["ThreadSortOrder", "Thread Sort Order", "ThreadSort", "Thread Sort", "Email Thread Sort Order", "ThreadOrder"],
    };

    /// <summary>Companion time columns of the date system fields, in priority order.</summary>
    public static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> TimeColumnAliases = new Dictionary<int, IReadOnlyList<string>>
    {
        [SystemFields.DateSent] = ["TimeSent", "Time Sent", "SentTime", "Sent Time", "Time_Sent"],
        [SystemFields.DateReceived] = ["TimeRcvd", "Time Rcvd", "TimeReceived", "Time Received", "ReceivedTime", "Received Time"],
        [SystemFields.DateCreated] = ["TimeCreated", "Time Created", "CreatedTime", "Created Time", "CreateTime", "Creation Time"],
        [SystemFields.DateLastModified] =
            ["TimeLastMod", "Time Last Mod", "TimeLastModified", "Time Last Modified", "TimeModified", "Time Modified", "LastModTime", "ModifiedTime"],
        [SystemFields.DocumentDate] = ["DocTime", "Doc Time", "Document Time"],
    };

    public static readonly IReadOnlyDictionary<StructuralTarget, IReadOnlyList<string>> StructuralAliases =
        new Dictionary<StructuralTarget, IReadOnlyList<string>>
        {
            [StructuralTarget.ParentId] =
            [
                "ParentID", "Parent ID", "ParentDocID", "Parent Doc ID", "Parent Document ID", "ParentDocumentID", "Parent_ID",
                "ParentBates", "Parent Bates", "ParentControlNumber", "Parent Control Number",
            ],
            [StructuralTarget.GroupId] =
            [
                "GroupID", "Group ID", "Group Identifier", "GroupIdentifier", "FamilyID", "Family ID", "Family_ID", "Family Group",
                "FamilyGroup", "Attachment Group", "AttachmentGroup",
            ],
            [StructuralTarget.NativePath] =
            [
                "NativePath", "Native Path", "NativeLink", "Native Link", "NativeFile", "Native File", "Native File Path",
                "NativeFilePath", "Native Location", "DocLink", "FileLink",
            ],
            [StructuralTarget.TextPath] =
            [
                "TextPath", "Text Path", "TextLink", "Text Link", "ExtractedText", "Extracted Text", "Extracted Text Path",
                "TextFile", "Text File", "Text Location", "FullTextPath", "Full Text Path", "OCRPath", "OCR Path",
            ],
            [StructuralTarget.FolderPath] =
                ["FolderPath", "Folder Path", "Original Folder Path", "Folder", "Virtual Path", "VirtualPath", "Relative Path"],
            [StructuralTarget.DuplicateGroupId] =
            [
                "DuplicateGroupID", "Duplicate Group ID", "DupGroupID", "Dup Group ID", "DuplicateGroup", "Duplicate Group",
                "DedupeGroup", "Dedupe Group", "DupID", "Duplicate ID",
            ],
            [StructuralTarget.DedupeHash] =
                ["DedupeHash", "Dedupe Hash", "DedupHash", "Deduplication Hash", "DupHash", "Duplicate Hash", "HashDedupe", "Dedupe Key"],
            [StructuralTarget.EmailHash] = ["EmailHash", "Email Hash", "MessageHash", "Message Hash", "EmailDedupeHash", "Email Dedupe Hash"],
            [StructuralTarget.EmailThreadId] =
            [
                "EmailThreadID", "Email Thread ID", "EmailThreadGroup", "Email Thread Group", "ThreadID", "Thread ID",
                "ThreadGroup", "Thread Group", "ConversationID", "Conversation ID",
            ],
        };

    /// <summary>Common columns with no system field; proposed as new fields when the workspace has none of that name.</summary>
    public static readonly IReadOnlyList<WellKnownField> WellKnownFields =
    [
        new("Custodian", ImportFieldType.Keyword, false, ["Custodian", "CustodianName", "Custodian Name", "Primary Custodian"]),
        new("Original File Path", ImportFieldType.Text, false, ["FilePath", "File Path", "OriginalPath", "Original Path", "Source Path", "SourcePath"]),
        new("Author", ImportFieldType.Keyword, false, ["Author", "DocAuthor", "Doc Author", "Document Author"]),
        new("From", ImportFieldType.Text, false, ["From", "EmailFrom", "Email From", "Sender", "Email Sender"]),
        new("To", ImportFieldType.Text, true, ["To", "EmailTo", "Email To", "Recipients", "Recipient"]),
        new("CC", ImportFieldType.Text, true, ["CC", "EmailCC", "Email CC"]),
        new("BCC", ImportFieldType.Text, true, ["BCC", "EmailBCC", "Email BCC"]),
        new("Subject", ImportFieldType.Text, false, ["Subject", "EmailSubject", "Email Subject"]),
        new("Title", ImportFieldType.Text, false, ["Title", "DocTitle", "Doc Title", "Document Title"]),
    ];

    public static string Label(StructuralTarget target) => target switch
    {
        StructuralTarget.ParentId => "Parent ID",
        StructuralTarget.GroupId => "Family/Group ID",
        StructuralTarget.NativePath => "Native Path",
        StructuralTarget.TextPath => "Extracted Text Path",
        StructuralTarget.FolderPath => "Folder Path",
        StructuralTarget.DuplicateGroupId => "Duplicate Group ID",
        StructuralTarget.DedupeHash => "Dedupe Hash",
        StructuralTarget.EmailThreadId => "Email Thread ID",
        StructuralTarget.EmailHash => "Email Hash",
        _ => target.ToString(),
    };

    /// <summary>The definition used to coerce a structural value: identifiers are Keywords, paths are Text.</summary>
    public static FieldDefinition Definition(StructuralTarget target) => new()
    {
        Name = Label(target),
        Type = target is StructuralTarget.NativePath or StructuralTarget.TextPath or StructuralTarget.FolderPath ? FieldType.Text : FieldType.Keyword,
        Storage = FieldStorage.Column,
        IsSystem = true,
        TextAnalysis = target is StructuralTarget.NativePath or StructuralTarget.TextPath or StructuralTarget.FolderPath ? TextAnalysis.Identifier : null,
    };

    /// <summary>Header comparison key ignoring case, spaces, underscores and punctuation (<c>Date_Sent</c> = <c>DATESENT</c>).</summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '#')
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Type label shown in the mapping grid.</summary>
    public static string TypeLabel(FieldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Type switch
        {
            FieldType.Date => definition.DatePrecision == DatePrecision.Date ? "date" : "dateTime",
            FieldType.SingleChoice => "singleChoice",
            FieldType.MultiChoice => "multiChoice",
            _ => definition.Type.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Field definition for a field a load would create.</summary>
    public static FieldDefinition Definition(NewFieldSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var type = spec.Type switch
        {
            ImportFieldType.Text => FieldType.Text,
            ImportFieldType.Keyword => FieldType.Keyword,
            ImportFieldType.Integer => FieldType.Integer,
            ImportFieldType.Decimal => FieldType.Decimal,
            ImportFieldType.Date or ImportFieldType.DateTime => FieldType.Date,
            ImportFieldType.Boolean => FieldType.Boolean,
            ImportFieldType.SingleChoice => FieldType.SingleChoice,
            _ => FieldType.MultiChoice,
        };
        return new FieldDefinition
        {
            Name = spec.Name.Trim(),
            Type = type,
            Storage = FieldStorage.Metadata,
            IsMultiValue = type == FieldType.MultiChoice || (spec.IsMultiValue && type is FieldType.Text or FieldType.Keyword),
            DatePrecision = spec.Type switch
            {
                ImportFieldType.Date => DatePrecision.Date,
                ImportFieldType.DateTime => DatePrecision.DateTime,
                _ => null,
            },
            DecimalPrecision = type == FieldType.Decimal ? spec.DecimalPrecision ?? 18 : null,
            DecimalScale = type == FieldType.Decimal ? spec.DecimalScale ?? 2 : null,
            TextAnalysis = type == FieldType.Text ? TextAnalysis.Prose : null,
            IsSearchable = true,
        };
    }

    /// <summary>The import type of an existing field, for comparing it with a requested new field; null for User fields.</summary>
    public static ImportFieldType? ImportTypeOf(FieldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Type switch
        {
            FieldType.User => null,
            FieldType.Text => ImportFieldType.Text,
            FieldType.Keyword => ImportFieldType.Keyword,
            FieldType.Integer => ImportFieldType.Integer,
            FieldType.Decimal => ImportFieldType.Decimal,
            FieldType.Date => definition.DatePrecision == DatePrecision.Date ? ImportFieldType.Date : ImportFieldType.DateTime,
            FieldType.Boolean => ImportFieldType.Boolean,
            FieldType.SingleChoice => ImportFieldType.SingleChoice,
            _ => ImportFieldType.MultiChoice,
        };
    }
}
