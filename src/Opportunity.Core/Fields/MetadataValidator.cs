using System.Text.Json;
using System.Text.Json.Nodes;

namespace Opportunity.Core.Fields;

/// <summary>
/// Validates a <c>Document.Metadata</c> JSONB object keyed by <c>"f" + FieldId</c> (ADR-003 §2–§3): every key names a
/// live <see cref="FieldStorage.Metadata"/> field of the workspace and every value is canonical for that field's type.
/// Coding fields never appear here (R2).
/// </summary>
public static class MetadataValidator
{
    public static IReadOnlyList<FieldError> Validate(string metadataJson, FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(metadataJson);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(metadataJson);
        }
        catch (JsonException ex)
        {
            return [new FieldError("metadata", "invalid-json", ex.Message)];
        }

        return root is JsonObject obj ? Validate(obj, catalog) : [new FieldError("metadata", "not-an-object", "Metadata must be a JSON object.")];
    }

    public static IReadOnlyList<FieldError> Validate(JsonObject metadata, FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = new List<FieldError>();
        foreach (var (key, value) in metadata)
        {
            if (!FieldKey.TryParse(key, out var fieldId) || catalog.Find(fieldId) is not { IsDeleted: false } field)
            {
                errors.Add(new FieldError(key, "unknown-field", $"'{key}' is not a field of this workspace."));
                continue;
            }

            if (field.Storage != FieldStorage.Metadata)
            {
                errors.Add(new FieldError(key, "not-metadata-field",
                    field.Storage == FieldStorage.Coding
                        ? $"{field.Name} is a coding field and is never stored in metadata (ADR-003 R2)."
                        : $"{field.Name} is a structural column."));
                continue;
            }

            if (value is null)
            {
                errors.Add(new FieldError(key, "null-value", "Absent values are omitted, never stored as null."));
                continue;
            }

            if (!FieldValues.TryCanonicalize(field, value, catalog.ChoicesOf(fieldId), forAssignment: false, out var canonical, out var error))
            {
                errors.Add(error!);
            }
            else if (!FieldValues.AreEqual(canonical, value))
            {
                errors.Add(new FieldError(key, "not-canonical", $"Expected the canonical form {canonical?.ToJsonString() ?? "(absent)"}."));
            }
        }

        return errors;
    }
}
