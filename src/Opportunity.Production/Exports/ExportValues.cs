using System.Text.Json;
using System.Text.Json.Nodes;

using Opportunity.Application.Content;
using Opportunity.Application.Exports;
using Opportunity.Core.Fields;

namespace Opportunity.Production.Exports;

/// <summary>
/// Renders a document's DAT values (ticket E12-T01): canonical values in forms the importer reads back unchanged, i.e.
/// ISO 8601 dates and UTC instants, invariant numbers, <c>Yes</c>/<c>No</c>, choice names and multi-values joined by the
/// profile's multi-value separator. Begin/End Attachment are computed from the family (control numbers of its first
/// and last member) whenever the document belongs to a family of more than one document.
/// </summary>
public sealed class ExportValues(FieldCatalog catalog, char multiValueSeparator, IReadOnlySet<int> restrictedFields)
{
    private readonly string _separator = multiValueSeparator.ToString();

    /// <summary>The DAT values of one document in column order, and which of them are text to neutralize.</summary>
    public (string[] Values, bool[] Neutralize) Row(
        IReadOnlyList<ExportColumn> columns, ExportSourceDocument document, string? nativePath, string? textPath)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(document);
        var values = new string[columns.Count];
        var neutralize = new bool[columns.Count];
        JsonObject? metadata = null;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            switch (column.Kind)
            {
                case ExportColumnKind.FamilyId:
                    values[i] = document.FamilyControlNumber;
                    neutralize[i] = true;
                    break;
                case ExportColumnKind.ParentId:
                    values[i] = document.ParentControlNumber ?? string.Empty;
                    neutralize[i] = true;
                    break;
                case ExportColumnKind.NativePath:
                    values[i] = nativePath ?? string.Empty;
                    break;
                case ExportColumnKind.TextPath:
                    values[i] = textPath ?? string.Empty;
                    break;
                default:
                    var field = catalog.Find(column.FieldId!.Value);
                    if (field is null || field.IsDeleted || restrictedFields.Contains(field.FieldId))
                    {
                        // Deleted since the export started, or no longer visible to the initiator: the column stays, empty.
                        values[i] = string.Empty;
                        break;
                    }

                    metadata ??= ParseObject(document.Document.Metadata);
                    values[i] = Format(field, FieldValue(field, document, metadata)) ?? string.Empty;
                    neutralize[i] = field.Type is FieldType.Text or FieldType.Keyword or FieldType.SingleChoice or FieldType.MultiChoice
                        or FieldType.User;
                    break;
            }
        }

        return (values, neutralize);
    }

    private static JsonNode? FieldValue(FieldDefinition field, ExportSourceDocument document, JsonObject? metadata)
    {
        if (field.FieldId == SystemFields.BegAttach && document.FamilyBegAttach is { } begin)
        {
            return JsonValue.Create(begin);
        }

        if (field.FieldId == SystemFields.EndAttach && document.FamilyEndAttach is { } end)
        {
            return JsonValue.Create(end);
        }

        return field.Storage switch
        {
            FieldStorage.Column => field.ColumnName is { } column ? DocumentFieldDisplay.ColumnValue(document.Document, column) : null,
            FieldStorage.Coding => document.Coding.GetValueOrDefault(field.FieldId),
            _ => metadata?[field.Key],
        };
    }

    private string? Format(FieldDefinition field, JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        if (field.IsChoice)
        {
            var ids = FieldValues.ChoiceIds(value).ToHashSet();
            var names = catalog.ChoicesOf(field.FieldId).Where(c => ids.Contains(c.ChoiceId)).Select(c => c.Name).ToList();
            return names.Count == 0 ? null : string.Join(_separator, names);
        }

        if (value is JsonArray array)
        {
            var items = array.Select(item => item is null ? null : Scalar(item)).OfType<string>().ToList();
            return items.Count == 0 ? null : string.Join(_separator, items);
        }

        return Scalar(value);
    }

    private static string? Scalar(JsonNode node)
    {
        if (node is not JsonValue value)
        {
            return node.ToJsonString();
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.String => value.GetValue<string>(),
            _ => null,
        };
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
}
