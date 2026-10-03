using System.Text.Json;
using System.Text.Json.Serialization;

using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;

namespace Opportunity.Import.Mapping;

/// <summary>Validation and the stored JSON form of import profiles.</summary>
public static class ImportProfileRules
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxColumns = 2048;

    /// <summary>Same shape as the API: camelCase members and enum names.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ImportProfileDefinition definition) => JsonSerializer.Serialize(definition, JsonOptions);

    public static ImportProfileDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<ImportProfileDefinition>(json, JsonOptions) ?? new ImportProfileDefinition();

    /// <summary>
    /// File- and workspace-independent checks of a profile to be saved. Field references are resolved when the profile
    /// is applied to a load (a profile may name fields a later load creates, or come from another workspace).
    /// </summary>
    public static IReadOnlyList<FieldError> Validate(ImportProfileWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var errors = new List<FieldError>();
        var name = write.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            errors.Add(new("name", "invalid-name", $"Name must be 1–{MaxNameLength} characters without control characters."));
        }

        if (write.Description is { Length: > MaxDescriptionLength })
        {
            errors.Add(new("description", "too-long", $"Description is limited to {MaxDescriptionLength} characters."));
        }

        if (write.Definition is not { } definition)
        {
            errors.Add(new("definition", "required", "The profile definition is required."));
            return errors;
        }

        var issues = new List<MappingIssue>();
        MappingCompiler.ValidateProfile(definition, issues);
        if (definition.Columns.Count > MaxColumns)
        {
            errors.Add(new("definition.columns", "too-many-columns", $"A profile maps at most {MaxColumns} columns."));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < definition.Columns.Count; i++)
        {
            var row = definition.Columns[i];
            var path = $"definition.columns[{i}]";
            if (row is null || string.IsNullOrWhiteSpace(row.Column))
            {
                errors.Add(new(path + ".column", "required", "Each mapping row names a column."));
                continue;
            }

            if (!seen.Add(row.Column.Trim()))
            {
                errors.Add(new(path + ".column", "duplicate-column-mapping", $"Column '{row.Column}' has more than one mapping row."));
            }

            if (row.Targets.Count > MappingCompiler.MaxTargetsPerColumn)
            {
                errors.Add(new(path + ".targets", "too-many-targets", $"A column maps to at most {MappingCompiler.MaxTargetsPerColumn} targets."));
            }

            for (var t = 0; t < row.Targets.Count; t++)
            {
                if (TargetError(row.Targets[t]) is { } message)
                {
                    errors.Add(new($"{path}.targets[{t}]", "invalid-target", message));
                }
            }

            if (row.Parsing is { } parsing)
            {
                var formatErrors = new List<string>();
                DateFormats.ExpandDates(parsing.DateFormats, false, formatErrors);
                DateFormats.ExpandTimes(parsing.TimeFormats, formatErrors);
                errors.AddRange(formatErrors.Select(e => new FieldError(path + ".parsing", "invalid-date-format", e)));
                if (parsing.SourceTimeZone is { } zone && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
                {
                    errors.Add(new(path + ".parsing.sourceTimeZone", "invalid-time-zone", $"Unknown time zone '{zone}'."));
                }

                if (parsing.MultiValueDelimiter is { } delimiter && !LoadFileSettingsResolver.TryParseDelimiter(delimiter, out _))
                {
                    errors.Add(new(path + ".parsing.multiValueDelimiter", "invalid-delimiter", $"'{delimiter}' is not one character."));
                }
            }
        }

        errors.AddRange(issues.Where(i => i.Severity == MappingIssueSeverity.Error).Select(i => new FieldError("definition", i.Code, i.Message)));
        return errors;
    }

    private static string? TargetError(MappingTarget? target) => target switch
    {
        null => "The target is empty.",
        { Kind: MappingTargetKind.Field, FieldId: null, FieldName: null or "" } => "A field target needs a field id or name.",
        { Kind: MappingTargetKind.Structural, Structural: null } => "A structural target needs its kind.",
        { Kind: MappingTargetKind.Structural, Structural: { } s } when !Enum.IsDefined(s) => "Unknown structural target.",
        { Kind: MappingTargetKind.NewField, NewField: null } => "A new-field target needs a name and type.",
        { Kind: MappingTargetKind.NewField, NewField: { } spec } =>
            FieldRules.ValidateDefinition(ImportTargets.Definition(spec)) is { Count: > 0 } e ? string.Join(" ", e.Select(x => x.Message)) : null,
        _ when !Enum.IsDefined(target.Kind) => "Unknown target kind.",
        _ => null,
    };
}
