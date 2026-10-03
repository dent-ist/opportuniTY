using System.Text.Json.Nodes;

namespace Opportunity.Core.Fields;

/// <summary>
/// A coding layout: ordered sections of fields with required flags and conditional visibility, assigned to workspace
/// roles. Every workspace has exactly one default layout, used when no role-specific layout applies.
/// </summary>
public sealed class CodingLayout
{
    public Guid WorkspaceId { get; set; }

    public Guid LayoutId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public List<CodingLayoutSection> Sections { get; init; } = [];

    /// <summary>Workspace role keys (E05-T02) this layout is assigned to.</summary>
    public List<string> Roles { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    public IEnumerable<CodingLayoutField> AllFields => Sections.SelectMany(s => s.Fields);
}

public sealed class CodingLayoutSection
{
    public Guid SectionId { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<CodingLayoutField> Fields { get; init; } = [];
}

public sealed class CodingLayoutField
{
    public int FieldId { get; set; }

    /// <summary>Required while visible. Only editable (Coding) fields can be required.</summary>
    public bool IsRequired { get; set; }

    /// <summary>Displayed but not editable; Metadata and Column fields are always read-only.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Shown only while the condition holds; null means always shown.</summary>
    public VisibilityCondition? VisibleWhen { get; set; }
}

/// <summary>
/// "Show this field when <see cref="FieldId"/> has any of <see cref="ChoiceIds"/>" (choice controlling field) or
/// "… equals <see cref="BooleanValue"/>" (Boolean controlling field). The controlling field must be in the same layout.
/// </summary>
public sealed record VisibilityCondition(int FieldId, IReadOnlyList<int>? ChoiceIds = null, bool? BooleanValue = null);

/// <summary>Validation of layout structure and of coding submissions against a layout.</summary>
public static class CodingLayoutValidator
{
    public const int MaxNameLength = 200;
    public const int MaxRoleLength = 100;

    /// <summary>Checks that a layout is well formed against the workspace's catalogue.</summary>
    public static IReadOnlyList<FieldError> ValidateStructure(CodingLayout layout, FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(layout.Name) || layout.Name.Trim().Length > MaxNameLength)
        {
            errors.Add(new("name", "invalid-name", $"Layout name must be 1–{MaxNameLength} characters."));
        }

        if (layout.Sections.Select(s => s.SectionId).Distinct().Count() != layout.Sections.Count
            || layout.Sections.Any(s => s.SectionId == Guid.Empty))
        {
            errors.Add(new("sections", "duplicate-section", "Section ids must be unique and non-empty."));
        }

        foreach (var section in layout.Sections.Where(s => string.IsNullOrWhiteSpace(s.Title) || s.Title.Trim().Length > MaxNameLength))
        {
            errors.Add(new("sections", "invalid-section-title", $"Section {section.SectionId} needs a title of 1–{MaxNameLength} characters."));
        }

        if (layout.Roles.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > MaxRoleLength)
            || layout.Roles.Distinct(StringComparer.Ordinal).Count() != layout.Roles.Count)
        {
            errors.Add(new("roles", "invalid-role", "Role keys must be unique and non-empty."));
        }

        var placed = new Dictionary<int, CodingLayoutField>();
        foreach (var field in layout.AllFields)
        {
            var key = FieldKey.For(field.FieldId);
            if (!placed.TryAdd(field.FieldId, field))
            {
                errors.Add(new(key, "duplicate-field", "A field appears at most once per layout."));
                continue;
            }

            var definition = catalog.Find(field.FieldId);
            if (definition is null || definition.IsDeleted)
            {
                errors.Add(new(key, "unknown-field", "The field does not exist in this workspace."));
                continue;
            }

            if (definition.Storage != FieldStorage.Coding && !field.IsReadOnly)
            {
                errors.Add(new(key, "not-editable", $"{definition.Name} is imported data and can only be shown read-only."));
            }

            if (field.IsRequired && (field.IsReadOnly || definition.Storage != FieldStorage.Coding))
            {
                errors.Add(new(key, "required-not-editable", "Only editable coding fields can be required."));
            }
        }

        foreach (var (fieldId, field) in placed)
        {
            if (field.VisibleWhen is not { } condition)
            {
                continue;
            }

            var key = FieldKey.For(fieldId);
            if (condition.FieldId == fieldId || !placed.ContainsKey(condition.FieldId))
            {
                errors.Add(new(key, "invalid-condition-field", "The controlling field must be another field of this layout."));
                continue;
            }

            var controlling = catalog.Find(condition.FieldId);
            if (controlling is null || controlling.IsDeleted)
            {
                continue; // reported above
            }

            switch (controlling.Type)
            {
                case FieldType.SingleChoice or FieldType.MultiChoice:
                    var valid = catalog.ChoicesOf(controlling.FieldId).Select(c => c.ChoiceId).ToHashSet();
                    if (condition.BooleanValue.HasValue || condition.ChoiceIds is not { Count: > 0 } ids || !ids.All(valid.Contains))
                    {
                        errors.Add(new(key, "invalid-condition-value", $"The condition needs one or more choices of {controlling.Name}."));
                    }

                    break;
                case FieldType.Boolean:
                    if (!condition.BooleanValue.HasValue || condition.ChoiceIds is { Count: > 0 })
                    {
                        errors.Add(new(key, "invalid-condition-value", $"The condition needs a true/false value of {controlling.Name}."));
                    }

                    break;
                default:
                    errors.Add(new(key, "invalid-condition-field", "Conditions can depend only on choice and Boolean fields."));
                    break;
            }
        }

        // Conditions must not form a cycle (A visible when B, B visible when A).
        foreach (var start in placed.Keys)
        {
            var seen = new HashSet<int> { start };
            for (var current = placed[start].VisibleWhen?.FieldId; current is { } next && placed.ContainsKey(next); current = placed[next].VisibleWhen?.FieldId)
            {
                if (!seen.Add(next))
                {
                    errors.Add(new(FieldKey.For(start), "condition-cycle", "Visibility conditions form a cycle."));
                    break;
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates a document's coding against a layout after a submission: every visible required field has a value and
    /// only editable layout fields were written. <paramref name="values"/> holds canonical values by field id.
    /// </summary>
    public static IReadOnlyList<FieldError> ValidateSubmission(
        CodingLayout layout, IReadOnlyDictionary<int, JsonNode?> values, IReadOnlyCollection<int> writtenFieldIds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(writtenFieldIds);
        var fields = layout.AllFields.ToDictionary(f => f.FieldId);
        var errors = new List<FieldError>();
        foreach (var fieldId in writtenFieldIds)
        {
            if (!fields.TryGetValue(fieldId, out var field) || field.IsReadOnly)
            {
                errors.Add(new(FieldKey.For(fieldId), "not-in-layout", "The field is not editable in this layout."));
            }
        }

        foreach (var field in fields.Values.Where(f => f.IsRequired && IsVisible(f, fields, values, 0)))
        {
            if (values.GetValueOrDefault(field.FieldId) is null)
            {
                errors.Add(new(FieldKey.For(field.FieldId), "required", "A value is required."));
            }
        }

        return errors;
    }

    /// <summary>A field is visible when its condition holds and its controlling field is itself visible.</summary>
    public static bool IsVisible(
        CodingLayoutField field, IReadOnlyDictionary<int, CodingLayoutField> fields, IReadOnlyDictionary<int, JsonNode?> values, int depth)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(values);
        if (field.VisibleWhen is not { } condition)
        {
            return true;
        }

        if (depth > fields.Count || !fields.TryGetValue(condition.FieldId, out var controlling)
            || !IsVisible(controlling, fields, values, depth + 1))
        {
            return false;
        }

        var value = values.GetValueOrDefault(condition.FieldId);
        if (condition.BooleanValue is { } expected)
        {
            return value is JsonValue v && v.TryGetValue<bool>(out var actual) && actual == expected;
        }

        var held = FieldValues.ChoiceIds(value);
        return condition.ChoiceIds?.Any(held.Contains) == true;
    }
}
