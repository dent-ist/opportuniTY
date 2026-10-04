using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.Application.Content;

/// <summary>How the viewer should present a field value; the API's display hint (E11-T01).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names of the display formats.")]
public enum FieldDisplayFormat
{
    /// <summary>A short single-line string.</summary>
    Text,

    /// <summary>Prose that may span lines (wrap, keep line breaks).</summary>
    LongText,

    /// <summary>An identifier, hash or path: monospace, never reflowed or localized.</summary>
    Identifier,

    Number,
    Decimal,

    /// <summary>A size in bytes.</summary>
    Bytes,

    /// <summary>A calendar date (no time, no zone).</summary>
    Date,

    /// <summary>An instant, shown in the display time zone.</summary>
    DateTime,

    Boolean,
    Choice,
    User,
}

/// <summary>One field of a document for the metadata view.</summary>
/// <param name="Value">Canonical ADR-003 value (null when the document has none).</param>
/// <param name="DisplayValue">The value formatted for display (dates in the display time zone), or null when empty.</param>
/// <param name="RawValue">The imported string when import coerced it into a different canonical form (ADR-003 R9).</param>
/// <param name="Choices">The selected choices of a choice field, in the field's choice order.</param>
public sealed record DocumentFieldEntry(
    FieldDefinition Field,
    FieldDisplayFormat Format,
    string TypeLabel,
    JsonNode? Value,
    string? DisplayValue,
    string? RawValue,
    IReadOnlyList<Choice> Choices);

/// <summary>
/// Builds the metadata view of a document: every live field the caller may see (system, imported and coding fields),
/// with its value, display hint, practitioner type label and a display string, so the viewer needs no type logic of its
/// own (ticket review E11-T01). Pure: no I/O.
/// </summary>
public static class DocumentFieldDisplay
{
    public const string MultiValueSeparator = "; ";

    private const string DateTimeDisplayFormat = "yyyy-MM-dd HH:mm:ss zzz";

    /// <summary>Practitioner type labels of the familiarity guide (§ field types); the API keeps the ADR-003 names.</summary>
    public static string TypeLabel(FieldType type) => type switch
    {
        FieldType.Text => "Long Text",
        FieldType.Keyword => "Short Text",
        FieldType.Integer => "Whole Number",
        FieldType.Decimal => "Decimal",
        FieldType.Date => "Date",
        FieldType.Boolean => "Yes/No",
        FieldType.SingleChoice => "Single Choice",
        FieldType.MultiChoice => "Multiple Choice",
        FieldType.User => "User",
        _ => type.ToString(),
    };

    public static FieldDisplayFormat FormatOf(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Type switch
        {
            FieldType.Text => field.TextAnalysis == TextAnalysis.Prose ? FieldDisplayFormat.LongText
                : field.FieldId is SystemFields.AllPaths or SystemFields.DuplicatePaths ? FieldDisplayFormat.Identifier
                : FieldDisplayFormat.Text,
            FieldType.Keyword => field.FieldId is SystemFields.Md5 or SystemFields.Sha1 or SystemFields.Sha256 or SystemFields.DuplicateGroup
                or SystemFields.EmailThreadGroup or SystemFields.ConversationIndex
                ? FieldDisplayFormat.Identifier
                : FieldDisplayFormat.Text,
            FieldType.Integer => field.FieldId == SystemFields.FileSize ? FieldDisplayFormat.Bytes : FieldDisplayFormat.Number,
            FieldType.Decimal => FieldDisplayFormat.Decimal,
            FieldType.Date => field.DatePrecision == DatePrecision.Date ? FieldDisplayFormat.Date : FieldDisplayFormat.DateTime,
            FieldType.Boolean => FieldDisplayFormat.Boolean,
            FieldType.SingleChoice or FieldType.MultiChoice => FieldDisplayFormat.Choice,
            FieldType.User => FieldDisplayFormat.User,
            _ => FieldDisplayFormat.Text,
        };
    }

    /// <summary>Every live field of the catalog except <paramref name="restricted"/>, in field-id order.</summary>
    public static IReadOnlyList<DocumentFieldEntry> Build(DocumentViewerRecord record, IReadOnlySet<int> restricted)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(restricted);
        var zone = ResolveZone(record.DisplayTimeZone);
        var metadata = ParseObject(record.Document.Metadata);
        var entries = new List<DocumentFieldEntry>();
        foreach (var field in record.Catalog.Fields.Where(f => !f.IsDeleted && !restricted.Contains(f.FieldId)).OrderBy(f => f.FieldId))
        {
            var value = field.Storage switch
            {
                FieldStorage.Column => field.ColumnName is { } column ? ColumnValue(record.Document, column) : null,
                FieldStorage.Coding => record.Coding.GetValueOrDefault(field.FieldId),
                _ => metadata?[field.Key],
            };
            var choices = field.IsChoice ? SelectedChoices(record.Catalog, field, value) : [];
            entries.Add(new DocumentFieldEntry(
                field,
                FormatOf(field),
                TypeLabel(field.Type),
                value?.DeepClone(),
                Display(field, value, choices, zone),
                record.RawValues.GetValueOrDefault(field.FieldId),
                choices));
        }

        return entries;
    }

    /// <summary>The canonical value of a structural column (ADR-003 §1), as the projection and coercer render it.</summary>
    public static JsonNode? ColumnValue(Document document, string column)
    {
        ArgumentNullException.ThrowIfNull(document);
        return column switch
        {
            "control_number" => Str(document.ControlNumber),
            "beg_bates" => Str(document.BegBates),
            "end_bates" => Str(document.EndBates),
            "beg_attach" => Str(document.BegAttach),
            "end_attach" => Str(document.EndAttach),
            "file_name" => Str(document.FileName),
            "file_extension" => Str(document.FileExtension),
            "file_type" => Str(document.FileType),
            "mime_type" => Str(document.MimeType),
            "file_size" => document.FileSize is { } size ? JsonValue.Create(size) : null,
            "page_count" => document.PageCount is { } pages ? JsonValue.Create(pages) : null,
            "date_sent" => Instant(document.DateSent),
            "date_received" => Instant(document.DateReceived),
            "date_created" => Instant(document.DateCreated),
            "date_last_modified" => Instant(document.DateLastModified),
            "document_date" => Instant(document.DocumentDate),
            "family_date" => Instant(document.FamilyDate),
            "md5" => Hex(document.Md5),
            "sha1" => Hex(document.Sha1),
            "sha256" => Hex(document.Sha256),
            "text_length" => document.TextLength is { } length ? JsonValue.Create(length) : null,
            "text_truncated" => JsonValue.Create(document.TextTruncated),
            "text_missing" => JsonValue.Create(document.TextMissing),
            "native_missing" => JsonValue.Create(document.NativeMissing),
            "images_incomplete" => JsonValue.Create(document.ImagesIncomplete),
            "duplicate_group_id" => Str(document.DuplicateGroupId?.ToString("D")),
            "is_duplicate_primary" => JsonValue.Create(document.IsDuplicatePrimary),
            "email_thread_id" => Str(document.EmailThreadId?.ToString("D")),
            _ => null,
        };
    }

    /// <summary>The display string of a canonical value; null when there is no value.</summary>
    public static string? Display(FieldDefinition field, JsonNode? value, IReadOnlyList<Choice> choices, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(zone);
        if (value is null)
        {
            return null;
        }

        if (field.IsChoice)
        {
            return choices.Count == 0 ? null : string.Join(MultiValueSeparator, choices.Select(c => c.Name));
        }

        if (value is JsonArray array)
        {
            var items = array.Select(item => item is null ? null : Scalar(field, item, zone)).OfType<string>().ToList();
            return items.Count == 0 ? null : string.Join(MultiValueSeparator, items);
        }

        return Scalar(field, value, zone);
    }

    /// <summary>The IANA (or Windows) zone, or UTC when the id is unknown on this host.</summary>
    public static TimeZoneInfo ResolveZone(string? id) =>
        id is { Length: > 0 } && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    private static string? Scalar(FieldDefinition field, JsonNode node, TimeZoneInfo zone)
    {
        if (node is not JsonValue value)
        {
            return node.ToJsonString();
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                return "Yes";
            case JsonValueKind.False:
                return "No";
            case JsonValueKind.Number:
                if (field.Type == FieldType.Decimal && decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return d.ToString(field.DecimalScale is { } scale ? "N" + scale.ToString(CultureInfo.InvariantCulture) : "#,0.######", CultureInfo.InvariantCulture);
                }

                if (value.TryGetValue<long>(out var l))
                {
                    var number = l.ToString("N0", CultureInfo.InvariantCulture);
                    return FormatOf(field) == FieldDisplayFormat.Bytes ? number + (l == 1 ? " byte" : " bytes") : number;
                }

                return value.ToJsonString();
            case JsonValueKind.String:
                var text = value.GetValue<string>();
                if (field.Type == FieldType.Date && field.DatePrecision != DatePrecision.Date
                    && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
                {
                    return TimeZoneInfo.ConvertTime(instant, zone).ToString(DateTimeDisplayFormat, CultureInfo.InvariantCulture);
                }

                return text;
            default:
                return null;
        }
    }

    private static List<Choice> SelectedChoices(FieldCatalog catalog, FieldDefinition field, JsonNode? value)
    {
        if (value is null)
        {
            return [];
        }

        HashSet<int> ids;
        try
        {
            ids = [.. FieldValues.ChoiceIds(value)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return [];
        }

        return [.. catalog.ChoicesOf(field.FieldId).Where(c => ids.Contains(c.ChoiceId))];
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonValue? Str(string? value) => value is null ? null : JsonValue.Create(value);

    private static JsonValue? Instant(DateTimeOffset? value) => value is { } v ? JsonValue.Create(FieldValues.FormatInstant(v.UtcDateTime)) : null;

    private static JsonValue? Hex(byte[]? value) => value is null ? null : JsonValue.Create(Convert.ToHexStringLower(value));
}
