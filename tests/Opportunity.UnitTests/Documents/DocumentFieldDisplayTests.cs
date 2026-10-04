using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Content;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.UnitTests.Documents;

/// <summary>
/// E11-T01 metadata view: every visible live field with its canonical value, practitioner type label, display hint and
/// a display string (instants in the workspace display time zone, choice names, Yes/No), plus the raw imported string.
/// </summary>
public sealed class DocumentFieldDisplayTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    [Fact]
    public void System_imported_and_coding_fields_are_shown_with_display_values_in_the_display_time_zone()
    {
        var custodian = Field(1000, "Custodian", FieldType.Keyword, FieldStorage.Metadata);
        var received = Field(1001, "Received On", FieldType.Date, FieldStorage.Metadata, precision: DatePrecision.DateTime);
        var responsive = Field(1002, "Responsiveness", FieldType.SingleChoice, FieldStorage.Coding);
        var issues = Field(1003, "Issues", FieldType.MultiChoice, FieldStorage.Coding);
        var amount = Field(1004, "Amount", FieldType.Decimal, FieldStorage.Metadata);
        amount.DecimalScale = 2;
        var notes = Field(1005, "Reviewer Notes", FieldType.Text, FieldStorage.Coding);
        notes.TextAnalysis = TextAnalysis.Prose;
        var deleted = Field(1006, "Old Field", FieldType.Keyword, FieldStorage.Metadata);
        deleted.IsDeleted = true;
        var secret = Field(1007, "Restricted Field", FieldType.Keyword, FieldStorage.Metadata);
        var choices = new[]
        {
            Choice(1002, 1, "Responsive", 1), Choice(1002, 2, "Not Responsive", 2),
            Choice(1003, 3, "Fraud", 1), Choice(1003, 4, "Pricing", 2), Choice(1003, 5, "Travel", 3),
        };
        var catalog = new FieldCatalog([.. SystemFields.Create(Workspace), custodian, received, responsive, issues, amount, notes, deleted, secret], choices);
        var document = Document.Create(Workspace, "ABC0000001", caseSensitive: false);
        document.FileSize = 1_234_567;
        document.DateSent = new DateTimeOffset(2024, 3, 1, 14, 5, 0, TimeSpan.Zero);
        document.Md5 = Convert.FromHexString("0123456789abcdef0123456789abcdef");
        document.TextTruncated = true;
        document.Metadata = """{"f1000":["Smith, Alex","Jones, Kim"],"f1001":"2024-07-01T09:30:00Z","f1004":1234.5,"f1007":"x"}""";
        var record = new DocumentViewerRecord(
            document,
            7,
            catalog,
            new Dictionary<int, JsonNode> { [1002] = JsonValue.Create(1), [1003] = new JsonArray(3, 5), [1005] = JsonValue.Create("Line one\nLine two") },
            new Dictionary<int, string> { [1001] = "07/01/2024 05:30 AM" },
            "America/New_York",
            null,
            null,
            null);

        var entries = DocumentFieldDisplay.Build(record, new HashSet<int> { 1007 }).ToDictionary(e => e.Field.FieldId);

        entries.Keys.Should().NotContain([1006, 1007], "deleted and restricted fields are omitted");
        entries.Keys.Should().Contain([SystemFields.ControlNumber, SystemFields.ThreadSortOrder, 1000, 1005]);
        Check(entries[SystemFields.ControlNumber], FieldDisplayFormat.Text, "Short Text", "ABC0000001");
        Check(entries[SystemFields.FileSize], FieldDisplayFormat.Bytes, "Whole Number", "1,234,567 bytes");
        Check(entries[SystemFields.DateSent], FieldDisplayFormat.DateTime, "Date", "2024-03-01 09:05:00 -05:00");
        entries[SystemFields.DateSent].Value!.GetValue<string>().Should().Be("2024-03-01T14:05:00Z");
        Check(entries[SystemFields.Md5], FieldDisplayFormat.Identifier, "Short Text", "0123456789abcdef0123456789abcdef");
        Check(entries[SystemFields.TextTruncated], FieldDisplayFormat.Boolean, "Yes/No", "Yes");
        Check(entries[SystemFields.NativeMissing], FieldDisplayFormat.Boolean, "Yes/No", "No");
        Check(entries[SystemFields.BegBates], FieldDisplayFormat.Text, "Short Text", null);
        entries[SystemFields.BegBates].Value.Should().BeNull();
        Check(entries[1000], FieldDisplayFormat.Text, "Short Text", "Smith, Alex; Jones, Kim");
        Check(entries[1001], FieldDisplayFormat.DateTime, "Date", "2024-07-01 05:30:00 -04:00");
        entries[1001].RawValue.Should().Be("07/01/2024 05:30 AM");
        Check(entries[1002], FieldDisplayFormat.Choice, "Single Choice", "Responsive");
        Check(entries[1003], FieldDisplayFormat.Choice, "Multiple Choice", "Fraud; Travel");
        entries[1003].Choices.Select(c => c.ChoiceId).Should().Equal(3, 5);
        Check(entries[1004], FieldDisplayFormat.Decimal, "Decimal", "1,234.50");
        Check(entries[1005], FieldDisplayFormat.LongText, "Long Text", "Line one\nLine two");
    }

    [Fact]
    public void An_unknown_display_time_zone_falls_back_to_utc()
    {
        DocumentFieldDisplay.ResolveZone("Not/AZone").Should().Be(TimeZoneInfo.Utc);
        DocumentFieldDisplay.ResolveZone(null).Should().Be(TimeZoneInfo.Utc);
        DocumentFieldDisplay.ResolveZone("Europe/London").Id.Should().Be("Europe/London");
    }

    private static void Check(DocumentFieldEntry entry, FieldDisplayFormat format, string label, string? display)
    {
        entry.Format.Should().Be(format, entry.Field.Name);
        entry.TypeLabel.Should().Be(label, entry.Field.Name);
        entry.DisplayValue.Should().Be(display, entry.Field.Name);
    }

    private static FieldDefinition Field(int id, string name, FieldType type, FieldStorage storage, DatePrecision? precision = null) => new()
    {
        WorkspaceId = Workspace,
        FieldId = id,
        Name = name,
        Type = type,
        Storage = storage,
        DatePrecision = precision,
        IsMultiValue = type == FieldType.Keyword && id == 1000,
    };

    private static Choice Choice(int fieldId, int choiceId, string name, int order) => new()
    {
        WorkspaceId = Workspace,
        FieldId = fieldId,
        ChoiceId = choiceId,
        Name = name,
        SortOrder = order,
        IsActive = true,
    };
}
