using System.Text.Json.Nodes;

using Opportunity.Application.Search.Projection;
using Opportunity.Application.Storage;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// Fixed inputs behind the projection golden files (<c>Search/Golden/*.json</c>): a catalogue with one field of every
/// slot kind in both namespaces, overflow fields, a deleted and a non-searchable field, and one fully populated document.
/// </summary>
internal static class ProjectionSamples
{
    public static readonly Guid WorkspaceId = Guid.Parse("6f1c2b3a-4d5e-4f60-8a9b-0c1d2e3f4a5b");
    public static readonly Guid DocumentId = Guid.Parse("0192a3b4-c5d6-7e8f-9a0b-1c2d3e4f5a6b");
    public static readonly Guid ParentId = Guid.Parse("0192a3b4-c5d6-7e8f-9a0b-1c2d3e4f5a60");
    public static readonly Guid DuplicateGroupId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    public static readonly Guid ThreadId = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");
    public static readonly Guid ReviewerId = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");

    public const int Custodian = 1000;
    public const int Subject = 1001;
    public const int From = 1002;
    public const int Amount = 1003;
    public const int MessageCount = 1004;
    public const int ReceivedOn = 1005;
    public const int Hot = 1006;
    public const int Confidentiality = 1007;
    public const int Owner = 1008;
    public const int VendorCode = 1009;
    public const int Retired = 1010;
    public const int Unsearched = 1011;
    public const int Responsive = 1020;
    public const int Issues = 1021;
    public const int ReviewNotes = 1022;
    public const int Privilege = 1023;
    public const int Escalation = 1024;
    public const int SentOn = 1025;

    public static string TextKey => ObjectKeys.Text(
        WorkspaceId, DocumentId, Sha256Digest.Parse("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")).Value;

    public static FieldCatalog Catalog()
    {
        var fields = SystemFields.Create(WorkspaceId).ToList();
        fields.AddRange(
        [
            Field(Custodian, "Custodian", FieldType.Keyword, FieldStorage.Metadata, "kw.s001", multi: true),
            Field(Subject, "Subject", FieldType.Text, FieldStorage.Metadata, "txt.s001", analysis: TextAnalysis.Prose),
            Field(From, "From", FieldType.Text, FieldStorage.Metadata, "idt.s001", analysis: TextAnalysis.Identifier),
            Field(Amount, "Amount", FieldType.Decimal, FieldStorage.Metadata, "dec.s001"),
            Field(MessageCount, "Message Count", FieldType.Integer, FieldStorage.Metadata, "int.s001"),
            Field(ReceivedOn, "Received On", FieldType.Date, FieldStorage.Metadata, "dt.s001", precision: DatePrecision.Date),
            Field(Hot, "Hot", FieldType.Boolean, FieldStorage.Metadata, "bool.s001"),
            Field(Confidentiality, "Confidentiality", FieldType.SingleChoice, FieldStorage.Metadata, "ch.s001"),
            Field(Owner, "Owner", FieldType.User, FieldStorage.Metadata, "usr.s001"),
            Field(VendorCode, "Vendor Code", FieldType.Keyword, FieldStorage.Metadata, FieldRules.OverflowSlot),
            Field(Unsearched, "Unsearched", FieldType.Keyword, FieldStorage.Metadata, null),
            Field(Responsive, "Responsive", FieldType.Boolean, FieldStorage.Coding, "bool.s001"),
            Field(Issues, "Issues", FieldType.MultiChoice, FieldStorage.Coding, "ch.s001", multi: true),
            Field(ReviewNotes, "Review Notes", FieldType.Text, FieldStorage.Coding, "txt.s001", analysis: TextAnalysis.Prose),
            Field(Privilege, "Privilege Status", FieldType.SingleChoice, FieldStorage.Coding, "ch.s002"),
            Field(Escalation, "Escalation", FieldType.Integer, FieldStorage.Coding, FieldRules.OverflowSlot),
            Field(SentOn, "Sent On", FieldType.Date, FieldStorage.Coding, "dt.s001", precision: DatePrecision.DateTime),
        ]);

        // Retired (1010) was deleted: its values remain in metadata until the purge job, but are invisible.
        return new FieldCatalog(fields, []);
    }

    public static Document Document()
    {
        var document = new Document
        {
            WorkspaceId = WorkspaceId,
            DocumentId = DocumentId,
            ControlNumber = "ABC0000010",
            ControlNumberNorm = "ABC0000010",
            ControlNumberSortKey = Core.Documents.ControlNumber.SortKey("ABC0000010"),
            BegBates = "prod 9",
            EndBates = "PROD 12",
            BegAttach = "ABC0000009",
            EndAttach = "ABC0000011",
            FamilyId = ParentId,
            ParentDocumentId = ParentId,
            FamilySequence = 1,
            FamilyStatus = FamilyStatus.Resolved,
            DuplicateGroupId = DuplicateGroupId,
            IsDuplicatePrimary = true,
            EmailThreadId = ThreadId,
            Md5 = Convert.FromHexString("D41D8CD98F00B204E9800998ECF8427E"),
            Sha1 = Convert.FromHexString("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709"),
            Sha256 = Convert.FromHexString("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"),
            FileName = "Q3_Budget-final.xlsx",
            FileExtension = "xlsx",
            FileType = "Microsoft Excel",
            MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileSize = 48_213,
            PageCount = 3,
            DateSent = new DateTimeOffset(2025, 3, 1, 9, 5, 0, TimeSpan.FromHours(-5)),
            DateReceived = new DateTimeOffset(2025, 3, 1, 14, 6, 30, 250, TimeSpan.Zero),
            DocumentDate = new DateTimeOffset(2025, 3, 1, 14, 5, 0, TimeSpan.Zero),
            FamilyDate = new DateTimeOffset(2025, 3, 1, 14, 5, 0, TimeSpan.Zero),
            TextObjectId = Guid.Parse("22222222-3333-4444-8555-666666666666"),
            TextLength = 18_000_000,
            ImagesIncomplete = true,
            Metadata =
                $$"""
                {"f1000": ["Smith, John", "Doe, Jane"], "f1001": "Quarterly results — café pricing", "f1002": "john.smith@acme.com",
                 "f1003": 1234.5, "f1004": 42, "f1005": "2025-03-01", "f1006": true, "f1007": 7, "f1008": "{{ReviewerId}}",
                 "f1009": "VND-17", "f1010": "gone", "f1011": "not searchable"}
                """,
        };
        return document;
    }

    public static ProjectionSource Live() => new()
    {
        WorkspaceId = WorkspaceId,
        DocumentId = DocumentId,
        State = ProjectionSourceState.Live,
        DocumentVersion = 7,
        Document = Document(),
        Coding = new Dictionary<int, JsonNode>
        {
            [Responsive] = JsonValue.Create(true),
            [Issues] = new JsonArray(3, 5),
            [ReviewNotes] = JsonValue.Create("Key memo"),
            [Privilege] = JsonValue.Create(11),
            [Escalation] = JsonValue.Create(2),
            [SentOn] = JsonValue.Create("2025-03-01T14:05:00Z"),
        },
        SecurityTags = ["wall:0f0e0d0c-0b0a-4908-8706-050403020100", "class:privilegestatus"],
        TextObjectKey = TextKey,
    };

    private static FieldDefinition Field(
        int id, string name, FieldType type, FieldStorage storage, string? slot, bool multi = false,
        TextAnalysis? analysis = null, DatePrecision? precision = null) => new()
        {
            WorkspaceId = WorkspaceId,
            FieldId = id,
            Name = name,
            Type = type,
            Storage = storage,
            IsMultiValue = multi,
            TextAnalysis = analysis,
            DatePrecision = precision,
            IsSearchable = slot is not null,
            SearchSlot = slot,
            Capabilities = FieldRules.CapabilitiesForSlot(slot),
        };
}
