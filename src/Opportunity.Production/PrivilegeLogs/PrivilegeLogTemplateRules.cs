using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;

namespace Opportunity.Production.PrivilegeLogs;

/// <summary>
/// Validates and normalizes privilege log templates (E13-T03): every member is filled in, so a stored template states
/// every value the log uses, and it is serialized canonically (fixed member order, no whitespace), so equal templates have
/// equal bytes. Columns refer to fields by id; the presets resolve their fields in the workspace (by id for system and
/// privilege fields, by name for metadata fields such as From or Subject that are not system fields).
/// </summary>
public static partial class PrivilegeLogTemplateRules
{
    public const int MaxColumns = 40;
    public const int MaxRules = 20;
    public const int MaxHeaderLength = 100;
    public const int MaxLabelLength = 200;
    public const int MaxNameLength = 200;
    public const string DefaultPrivIdPrefix = "PRIV";
    public const int DefaultPrivIdPadding = 4;
    public const string DefaultDateFormat = "yyyy-MM-dd";
    public const string DefaultTimeZone = "UTC";
    public const string DefaultSeparator = "; ";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>A preset column: a kind, its header, and the field (by id, or by name when it is not a system field).</summary>
    private sealed record PresetColumn(PrivilegeLogColumnKind Kind, string Header, int? FieldId = null, string? FieldName = null);

    private static readonly PresetColumn[] DocumentByDocumentColumns =
    [
        new(PrivilegeLogColumnKind.PrivId, "Priv ID"),
        new(PrivilegeLogColumnKind.Field, "Date", SystemFields.DocumentDate),
        new(PrivilegeLogColumnKind.Field, "Author", FieldName: "Author"),
        new(PrivilegeLogColumnKind.Field, "From", FieldName: "From"),
        new(PrivilegeLogColumnKind.Field, "To", FieldName: "To"),
        new(PrivilegeLogColumnKind.Field, "CC", FieldName: "CC"),
        new(PrivilegeLogColumnKind.Field, "BCC", FieldName: "BCC"),
        new(PrivilegeLogColumnKind.SubjectOrFileName, "Subject/File Name"),
        new(PrivilegeLogColumnKind.Field, "Doc Type", SystemFields.FileType),
        new(PrivilegeLogColumnKind.Basis, "Privilege Basis"),
        new(PrivilegeLogColumnKind.Field, "Description", PrivilegeFields.Description),
        new(PrivilegeLogColumnKind.Field, "Attorneys Involved", PrivilegeFields.AttorneysInvolved),
        new(PrivilegeLogColumnKind.FamilyRange, "Family Range"),
        new(PrivilegeLogColumnKind.Treatment, "Withheld/Redacted"),
    ];

    private static readonly HashSet<int> ReviewerWritten = [PrivilegeFields.Description, PrivilegeFields.AttorneysInvolved];

    public static string Serialize(PrivilegeLogTemplateDefinition definition) => JsonSerializer.Serialize(definition, Json);

    public static PrivilegeLogTemplateDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<PrivilegeLogTemplateDefinition>(json, Json) ?? throw new JsonException("Empty privilege log template.");

    public static string PresetName(PrivilegeLogPreset preset) => preset switch
    {
        PrivilegeLogPreset.MetadataOnly => "Metadata only",
        _ => "Document-by-document",
    };

    /// <summary>The preset's columns that the workspace can fill (fields it lacks or the caller may not see are left out).</summary>
    public static IReadOnlyList<PrivilegeLogColumn> PresetColumns(PrivilegeLogPreset preset, FieldCatalog catalog, IReadOnlySet<int> restricted)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(restricted);
        var columns = new List<PrivilegeLogColumn>();
        foreach (var column in DocumentByDocumentColumns)
        {
            if (preset == PrivilegeLogPreset.MetadataOnly && column.FieldId is { } written && ReviewerWritten.Contains(written))
            {
                continue;
            }

            if (column.Kind == PrivilegeLogColumnKind.Basis && (catalog.Find(PrivilegeFields.Basis) is not { IsDeleted: false } || restricted.Contains(PrivilegeFields.Basis)))
            {
                continue;
            }

            if (column.Kind != PrivilegeLogColumnKind.Field)
            {
                columns.Add(new PrivilegeLogColumn(column.Kind, column.Header));
                continue;
            }

            var field = column.FieldId is { } id ? catalog.Find(id) : FindByName(catalog, column.FieldName!);
            if (field is { IsDeleted: false } && !restricted.Contains(field.FieldId))
            {
                columns.Add(new PrivilegeLogColumn(PrivilegeLogColumnKind.Field, column.Header, field.FieldId));
            }
        }

        return columns;
    }

    /// <summary>A custom (non-system) field by name, compared without case; the lowest id wins.</summary>
    public static FieldDefinition? FindByName(FieldCatalog catalog, string name)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Fields.Where(f => !f.IsDeleted && !f.IsSystem && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.FieldId).FirstOrDefault();
    }

    /// <summary>
    /// The normalized template, or the problems keyed by member path. Fields must exist, be live and visible to the
    /// caller (<paramref name="restricted"/>: a hidden field answers like an unknown one).
    /// </summary>
    public static (PrivilegeLogTemplateDefinition? Definition, IReadOnlyDictionary<string, string[]> Errors) Normalize(
        PrivilegeLogTemplateDefinition? input, FieldCatalog catalog, IReadOnlySet<int> restricted)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(restricted);
        input ??= new PrivilegeLogTemplateDefinition();
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string message)
        {
            if (!problems.TryGetValue(key, out var list))
            {
                problems[key] = list = [];
            }

            list.Add(message);
        }

        bool Usable(int fieldId) => catalog.Find(fieldId) is { IsDeleted: false } && !restricted.Contains(fieldId);

        var preset = input.Preset ?? PrivilegeLogPreset.DocumentByDocument;
        if (!Enum.IsDefined(preset))
        {
            Add("preset", "Use documentByDocument or metadataOnly.");
        }

        var columns = new List<PrivilegeLogColumn>();
        var given = input.Columns is { Count: > 0 } explicitColumns ? explicitColumns : PresetColumns(preset, catalog, restricted);
        if (given.Count > MaxColumns)
        {
            Add("columns", string.Create(CultureInfo.InvariantCulture, $"A log has at most {MaxColumns} columns."));
        }

        for (var i = 0; i < given.Count && i < MaxColumns; i++)
        {
            var column = given[i];
            var key = string.Create(CultureInfo.InvariantCulture, $"columns[{i}]");
            if (column is null || !Enum.IsDefined(column.Kind))
            {
                Add(key, "Each column needs a kind.");
                continue;
            }

            int? fieldId = null;
            if (column.Kind == PrivilegeLogColumnKind.Field)
            {
                if (column.FieldId is not { } id || !Usable(id))
                {
                    Add(key + ".fieldId", "No such field in this workspace.");
                    continue;
                }

                fieldId = id;
            }
            else if (column.FieldId is not null)
            {
                Add(key + ".fieldId", "Only a field column names a field.");
                continue;
            }
            else if (column.Kind == PrivilegeLogColumnKind.Basis && !Usable(PrivilegeFields.Basis))
            {
                Add(key + ".kind", "No such field in this workspace.");
                continue;
            }

            var header = string.IsNullOrWhiteSpace(column.Header)
                ? fieldId is { } f ? catalog.Find(f)!.Name : DefaultHeader(column.Kind)
                : column.Header.Trim();
            if (header.Length > MaxHeaderLength || header.Any(char.IsControl))
            {
                Add(key + ".header", string.Create(CultureInfo.InvariantCulture, $"A header has 1 to {MaxHeaderLength} characters and no control characters."));
                continue;
            }

            columns.Add(new PrivilegeLogColumn(column.Kind, header, fieldId));
        }

        if (columns.Count == 0 && !problems.Keys.Any(k => k.StartsWith("columns", StringComparison.Ordinal)))
        {
            Add("columns", "A log needs at least one column.");
        }

        var prefix = input.PrivIdPrefix is null ? DefaultPrivIdPrefix : input.PrivIdPrefix.Trim();
        if (!PrivIdPrefixPattern().IsMatch(prefix))
        {
            Add("privIdPrefix", "1 to 20 letters, digits, _ - or ., starting with a letter or digit.");
        }

        var start = input.PrivIdStart ?? 1;
        if (start is < 1 or > 9_999_999_999)
        {
            Add("privIdStart", "Start between 1 and 9,999,999,999.");
        }

        var padding = input.PrivIdPadding ?? DefaultPrivIdPadding;
        if (padding is < 1 or > 10)
        {
            Add("privIdPadding", "Padding between 1 and 10 digits.");
        }

        var dateFormat = string.IsNullOrWhiteSpace(input.DateFormat) ? DefaultDateFormat : input.DateFormat.Trim();
        if (dateFormat.Length > 50 || !ValidDateFormat(dateFormat))
        {
            Add("dateFormat", "A .NET date pattern such as yyyy-MM-dd or MM/dd/yyyy.");
        }

        var timeZone = string.IsNullOrWhiteSpace(input.TimeZone) ? DefaultTimeZone : input.TimeZone.Trim();
        if (timeZone.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            Add("timeZone", "An IANA time zone such as UTC or America/New_York.");
        }

        var separator = input.MultiValueSeparator ?? DefaultSeparator;
        if (separator.Length is 0 or > 5 || separator.Any(c => char.IsControl(c)))
        {
            Add("multiValueSeparator", "1 to 5 characters, no control characters.");
        }

        var rules = new List<PrivilegeLogExclusionRule>();
        var givenRules = input.ExclusionRules ?? [];
        if (givenRules.Count > MaxRules)
        {
            Add("exclusionRules", string.Create(CultureInfo.InvariantCulture, $"At most {MaxRules} exclusion rules."));
        }

        var logCategories = catalog.ChoicesOf(PrivilegeFields.LogCategory).Select(c => c.ChoiceId).ToHashSet();
        for (var i = 0; i < givenRules.Count && i < MaxRules; i++)
        {
            var rule = givenRules[i];
            var key = string.Create(CultureInfo.InvariantCulture, $"exclusionRules[{i}]");
            var label = rule?.Label?.Trim() ?? string.Empty;
            if (label.Length is 0 or > MaxLabelLength || label.Any(char.IsControl))
            {
                Add(key + ".label", string.Create(CultureInfo.InvariantCulture, $"A label has 1 to {MaxLabelLength} characters and no control characters."));
            }

            if (rule is null)
            {
                continue;
            }

            var hasDate = rule.OnOrAfter is not null || rule.Before is not null;
            int? dateField = null;
            if (hasDate || rule.DateFieldId is not null)
            {
                dateField = rule.DateFieldId ?? SystemFields.DocumentDate;
                if (!Usable(dateField.Value) || catalog.Find(dateField.Value)!.Type != FieldType.Date)
                {
                    Add(key + ".dateFieldId", "No such date field in this workspace.");
                }
            }

            if (rule.OnOrAfter is { } from && rule.Before is { } to && to <= from)
            {
                Add(key + ".before", "before must be later than onOrAfter.");
            }

            var categories = (rule.LogCategoryChoiceIds ?? []).Distinct().Order().ToList();
            if (categories.Count > 0 && (!Usable(PrivilegeFields.LogCategory) || categories.Any(c => !logCategories.Contains(c))))
            {
                Add(key + ".logCategoryChoiceIds", "No such Log Category choice in this workspace.");
            }

            var attorneys = (rule.AttorneysInvolved ?? []).Select(a => a?.Trim() ?? string.Empty).ToList();
            if (attorneys.Any(a => a.Length is 0 or > 200 || a.Any(char.IsControl)) || attorneys.Count > 100)
            {
                Add(key + ".attorneysInvolved", "At most 100 names of 1 to 200 characters.");
            }
            else if (attorneys.Count > 0 && !Usable(PrivilegeFields.AttorneysInvolved))
            {
                Add(key + ".attorneysInvolved", "No such field in this workspace.");
            }

            if (!hasDate && categories.Count == 0 && attorneys.Count == 0)
            {
                Add(key, "A rule needs at least one condition: a date range, Log Categories or Attorneys Involved.");
            }

            rules.Add(new PrivilegeLogExclusionRule(label, dateField, rule.OnOrAfter, rule.Before, categories,
                [.. attorneys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)]));
        }

        if (problems.Count > 0)
        {
            return (null, problems.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal));
        }

        return (new PrivilegeLogTemplateDefinition(columns, preset, prefix, start, padding, dateFormat, timeZone, separator,
            input.IncludePrivacyRedactions, rules), new Dictionary<string, string[]>());
    }

    /// <summary>Every field a normalized template reads (columns and exclusion rules).</summary>
    public static IReadOnlyList<int> FieldIdsOf(PrivilegeLogTemplateDefinition definition, FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalog);
        var ids = new SortedSet<int>();
        foreach (var column in definition.Columns ?? [])
        {
            switch (column.Kind)
            {
                case PrivilegeLogColumnKind.Field:
                    ids.Add(column.FieldId!.Value);
                    break;
                case PrivilegeLogColumnKind.Basis:
                    ids.Add(PrivilegeFields.Basis);
                    break;
                case PrivilegeLogColumnKind.SubjectOrFileName:
                    if (FindByName(catalog, "Subject") is { } subject)
                    {
                        ids.Add(subject.FieldId);
                    }

                    ids.Add(SystemFields.FileName);
                    break;
            }
        }

        foreach (var rule in definition.ExclusionRules ?? [])
        {
            if (rule.DateFieldId is { } date)
            {
                ids.Add(date);
            }

            if (rule.LogCategoryChoiceIds is { Count: > 0 })
            {
                ids.Add(PrivilegeFields.LogCategory);
            }

            if (rule.AttorneysInvolved is { Count: > 0 })
            {
                ids.Add(PrivilegeFields.AttorneysInvolved);
            }
        }

        return [.. ids];
    }

    public static string DefaultHeader(PrivilegeLogColumnKind kind) => kind switch
    {
        PrivilegeLogColumnKind.PrivId => "Priv ID",
        PrivilegeLogColumnKind.BegBates => "Beg Bates",
        PrivilegeLogColumnKind.EndBates => "End Bates",
        PrivilegeLogColumnKind.ControlNumber => "Control Number",
        PrivilegeLogColumnKind.FamilyRange => "Family Range",
        PrivilegeLogColumnKind.Treatment => "Withheld/Redacted",
        PrivilegeLogColumnKind.Basis => "Privilege Basis",
        PrivilegeLogColumnKind.SubjectOrFileName => "Subject/File Name",
        PrivilegeLogColumnKind.RedactionReasons => "Redaction Reasons",
        _ => "Field",
    };

    /// <summary>A template name: trimmed, 1–200 characters, no control characters; null when invalid.</summary>
    public static string? NormalizeName(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        return value.Length is 0 or > MaxNameLength || value.Any(char.IsControl) ? null : value;
    }

    private static bool ValidDateFormat(string pattern)
    {
        try
        {
            var sample = new DateTime(2026, 10, 6, 13, 5, 9, DateTimeKind.Utc).ToString(pattern, CultureInfo.InvariantCulture);
            return sample.Length > 0 && pattern.Any(c => c is 'y' or 'M' or 'd');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrivIdPrefixPattern();
}
