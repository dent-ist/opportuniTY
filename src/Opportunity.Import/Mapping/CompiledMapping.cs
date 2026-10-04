using System.Text.Json.Nodes;

using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.Import.Mapping;

/// <summary>One resolved target of a column.</summary>
public sealed class TargetBinding
{
    /// <summary>The target as the effective profile stores it (field id and current name filled in).</summary>
    public required MappingTarget Target { get; init; }

    /// <summary>Identity for duplicate detection: <c>f{id}</c>, <c>s:{name}</c> or <c>n:{name}</c>.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>The definition values are coerced with: the field's, a synthetic one for structural and new fields.</summary>
    public required FieldDefinition Definition { get; init; }

    public required TargetResolution Resolution { get; init; }

    public MatchKind MatchedBy { get; init; } = MatchKind.Profile;

    public string? Alias { get; init; }

    /// <summary>Existing field id (system or custom); null for structural and to-be-created fields.</summary>
    public int? FieldId { get; init; }

    public StructuralTarget? Structural { get; init; }

    /// <summary>The field the load creates (null when an existing field is used).</summary>
    public NewFieldSpec? CreatesField { get; init; }

    public bool Usable => Resolution != TargetResolution.Unresolved;

    public bool IsControlNumber => FieldId == SystemFields.ControlNumber;
}

/// <summary>A load-file column with its targets and parsing settings.</summary>
public sealed class ColumnBinding
{
    public required string Column { get; init; }

    public required int Index { get; init; }

    public required ColumnStatus Status { get; set; }

    public List<TargetBinding> Targets { get; } = [];

    /// <summary>The companion time column merged into this date column.</summary>
    public string? TimeColumn { get; set; }

    public int? TimeColumnIndex { get; set; }

    /// <summary>For a time column: the date column it is merged into.</summary>
    public string? MergedInto { get; set; }

    public ColumnParsing? Parsing { get; set; }

    internal CoercionSettings Settings { get; set; } = new();

    internal IReadOnlyList<string> TrueValues { get; set; } = [];

    internal IReadOnlyList<string> FalseValues { get; set; } = [];
}

/// <summary>One value of a mapped row.</summary>
/// <param name="Raw">The input string (date and time merged); kept as the raw value when <paramref name="KeepRaw"/>.</param>
/// <param name="Value">Canonical value; for choices still to be created, the choice names.</param>
/// <param name="MissingChoices">Choice names the load must create first.</param>
public sealed record MappedCell(
    ColumnBinding Column,
    TargetBinding Target,
    string? Raw,
    CoercionStatus Status,
    JsonNode? Value,
    bool KeepRaw,
    string? Format,
    CellError? Error,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> MissingChoices);

/// <summary>A data row mapped to its targets. A row with errors goes to the error file, not into the workspace.</summary>
public sealed record MappedRow(long RowNumber, string? ControlNumber, string? ControlNumberNorm, IReadOnlyList<MappedCell> Cells)
{
    public int ErrorCount => Cells.Count(c => c.Error is not null);

    public bool HasErrors => ErrorCount > 0;

    public MappedCell? Cell(string targetKey) => Cells.FirstOrDefault(c => c.Target.Key == targetKey);
}

/// <summary>
/// A profile applied to one load file's header against one workspace's fields: every column bound, every target
/// resolved, and the profile-level issues. <see cref="Map"/> coerces a data row (pure, thread-safe).
/// </summary>
public sealed class CompiledMapping
{
    internal CompiledMapping(
        IReadOnlyList<string> header,
        IReadOnlyList<ColumnBinding> columns,
        IReadOnlyList<MappingIssue> issues,
        IReadOnlyList<string> missingColumns,
        IReadOnlyList<string> newColumns,
        ImportProfileDefinition effectiveProfile,
        FieldCatalog catalog,
        bool controlNumberCaseSensitive)
    {
        Header = header;
        Columns = columns;
        Issues = issues;
        MissingColumns = missingColumns;
        NewColumns = newColumns;
        EffectiveProfile = effectiveProfile;
        Catalog = catalog;
        ControlNumberCaseSensitive = controlNumberCaseSensitive;
    }

    public IReadOnlyList<string> Header { get; }

    /// <summary>One binding per header column, in header order.</summary>
    public IReadOnlyList<ColumnBinding> Columns { get; }

    public IReadOnlyList<MappingIssue> Issues { get; }

    public bool HasErrors => Issues.Any(i => i.Severity == MappingIssueSeverity.Error);

    /// <summary>Profile columns absent from this file; their mappings are kept, never dropped silently.</summary>
    public IReadOnlyList<string> MissingColumns { get; }

    /// <summary>File columns the profile does not mention.</summary>
    public IReadOnlyList<string> NewColumns { get; }

    /// <summary>The profile as applied (auto-mapped columns and detected time columns included), ready to save.</summary>
    public ImportProfileDefinition EffectiveProfile { get; }

    public FieldCatalog Catalog { get; }

    public bool ControlNumberCaseSensitive { get; }

    public IEnumerable<TargetBinding> Targets =>
        Columns.Where(c => c.Status is ColumnStatus.Mapped or ColumnStatus.StoredAsNewTextField).SelectMany(c => c.Targets);

    public MappedRow Map(long rowNumber, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var cells = new List<MappedCell>();
        string? controlNumber = null;
        string? controlNorm = null;
        foreach (var column in Columns)
        {
            if (column.Status is not (ColumnStatus.Mapped or ColumnStatus.StoredAsNewTextField))
            {
                continue;
            }

            var raw = column.Index < values.Count ? values[column.Index] : string.Empty;
            var time = column.TimeColumnIndex is { } t && t < values.Count ? values[t] : null;
            foreach (var target in column.Targets.Where(t => t.Usable))
            {
                var cell = MapCell(column, target, raw, time);
                if (target.IsControlNumber && cell.Error is null && cell.Value is not null)
                {
                    controlNumber ??= cell.Value.GetValue<string>();
                    controlNorm ??= NormalizeIdentifier(cell.Value.GetValue<string>());
                }

                cells.Add(cell);
            }
        }

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.Target.Structural == StructuralTarget.ParentId && cell.Value is not null && controlNorm is not null
                && NormalizeIdentifier(cell.Value.GetValue<string>()) == controlNorm)
            {
                // Some exports set a top-level document's parent to itself; that means "no parent".
                cells[i] = cell with
                {
                    Status = CoercionStatus.Absent,
                    Value = null,
                    Warnings = [.. cell.Warnings, "Parent ID equals the document's own control number; treated as no parent."],
                };
            }
        }

        AttachmentRangeWarnings(cells);
        return new MappedRow(rowNumber, controlNumber, controlNorm, cells);
    }

    private static void AttachmentRangeWarnings(List<MappedCell> cells)
    {
        var beg = cells.FindIndex(c => c.Target.FieldId == SystemFields.BegAttach);
        var end = cells.FindIndex(c => c.Target.FieldId == SystemFields.EndAttach);
        if (beg < 0 || end < 0 || (cells[beg].Value is null) == (cells[end].Value is null))
        {
            return;
        }

        var present = cells[beg].Value is null ? end : beg;
        cells[present] = cells[present] with
        {
            Warnings = [.. cells[present].Warnings, "Only one end of the attachment range is set."],
        };
    }

    private string? NormalizeIdentifier(string value) =>
        ControlNumber.TryNormalize(value, ControlNumberCaseSensitive, null, out var norm, out _) ? norm : null;

    private MappedCell MapCell(ColumnBinding column, TargetBinding target, string raw, string? time)
    {
        if (target.Structural == StructuralTarget.TextPath && EffectiveProfile.Paths?.TextInLoadFile == true)
        {
            // The column holds the extracted text itself: stored as a text object by the chunk (E08-T04), never coerced
            // as a field value or kept as a raw metadata string.
            return new MappedCell(column, target, null, string.IsNullOrEmpty(raw) ? CoercionStatus.Absent : CoercionStatus.Value, null, false, null, null, [], []);
        }

        var definition = target.Definition;
        var input = raw;
        var warnings = new List<string>();
        if (definition.Type == FieldType.Date && time is not null)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (!string.IsNullOrWhiteSpace(time) && !IsZeroValue(time))
                {
                    warnings.Add($"Time '{time.Trim()}' without a date was ignored.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(time))
            {
                input = raw.Trim() + " " + time.Trim();
            }
        }
        else if (definition.Type == FieldType.Boolean)
        {
            var token = raw.Trim();
            if (column.TrueValues.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                input = "true";
            }
            else if (column.FalseValues.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                input = "false";
            }
        }

        var choices = target.FieldId is { } id ? Catalog.ChoicesOf(id) : [];
        var settings = target.CreatesField is not null && definition.IsChoice
            ? column.Settings with { CreateMissingChoices = true }
            : column.Settings;
        var result = FieldValueCoercer.Coerce(definition, input, settings, choices);
        warnings.AddRange(result.Warnings);
        var rawText = string.IsNullOrWhiteSpace(input) ? null : input.Trim();

        if (result.Status == CoercionStatus.Error)
        {
            return Cell(result.Status, null, false, null, new CellError(result.Error!.Code, result.Error.Message));
        }

        if (result.Status == CoercionStatus.MissingChoices)
        {
            // Names of the choices still to be created stand in for the ids until the load creates them.
            var missing = result.MissingChoiceNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            JsonNode? names = definition.Type == FieldType.MultiChoice
                ? new JsonArray([.. ResolvedChoiceNames(definition, input, settings, choices, missing).Select(n => (JsonNode)JsonValue.Create(n))])
                : JsonValue.Create(missing[0]);
            return new MappedCell(column, target, rawText, result.Status, names, false, null, null, warnings, missing);
        }

        if (target.IsControlNumber)
        {
            if (result.Value is null)
            {
                return Cell(CoercionStatus.Error, null, false, null, new CellError("control-number-missing", "The control number is blank."));
            }

            var prefix = EffectiveProfile.ControlNumberPrefix;
            var value = result.Value.GetValue<string>();
            if (!ControlNumber.TryNormalize(value, ControlNumberCaseSensitive, prefix, out _, out var error))
            {
                return Cell(CoercionStatus.Error, null, false, null, new CellError("invalid-control-number", error!));
            }

            return Cell(result.Status, JsonValue.Create((prefix + value).Trim()), result.KeepRaw, null, null);
        }

        if (target.FieldId is { } hashField && ImportTargets.HashHexLength.TryGetValue(hashField, out var hexLength) && result.Value is not null)
        {
            var hash = result.Value.GetValue<string>().ToLowerInvariant();
            if (hash.Length != hexLength || !hash.All(char.IsAsciiHexDigit))
            {
                return Cell(CoercionStatus.Error, null, false, null,
                    new CellError("invalid-hash", $"{target.Label} must be {hexLength} hexadecimal digits."));
            }

            return Cell(result.Status, JsonValue.Create(hash), hash != result.Value.GetValue<string>(), null, null);
        }

        if (target.Structural is StructuralTarget.DedupeHash or StructuralTarget.EmailHash && result.Value is not null)
        {
            // ADR-009 R17: upstream hashes are stored as lower-case hex; any digest length up to SHA-512.
            var original = result.Value.GetValue<string>();
            var hash = original.ToLowerInvariant();
            if (hash.Length > 128 || !hash.All(char.IsAsciiHexDigit))
            {
                return Cell(CoercionStatus.Error, null, false, null,
                    new CellError("invalid-hash", $"{target.Label} must be 1–128 hexadecimal digits."));
            }

            return Cell(result.Status, JsonValue.Create(hash), result.KeepRaw || hash != original, null, null);
        }

        if (target.Structural is StructuralTarget.DuplicateGroupId or StructuralTarget.EmailThreadId && result.Value is not null
            && result.Value.GetValue<string>().Length > RelationshipIds.MaxUpstreamValueLength)
        {
            return Cell(CoercionStatus.Error, null, false, null,
                new CellError("identifier-too-long", $"{target.Label} is longer than {RelationshipIds.MaxUpstreamValueLength} characters."));
        }

        if (target.FieldId == SystemFields.ConversationIndex && result.Value is not null)
        {
            var original = result.Value.GetValue<string>();
            if (!Core.Documents.ConversationIndex.TryNormalize(original, out var hex, out var fromBase64))
            {
                return Cell(CoercionStatus.Error, null, false, null, new CellError("invalid-conversation-index",
                    $"{target.Label} must be hexadecimal or base64 of at least {Core.Documents.ConversationIndex.HeaderBytes} bytes."));
            }

            if (fromBase64)
            {
                warnings.Add($"{target.Label} was base64; stored as hexadecimal.");
            }

            return Cell(result.Status, JsonValue.Create(hex), result.KeepRaw || hex != original, null, null);
        }

        if (target.Structural == StructuralTarget.ParentId && result.Value is not null)
        {
            // Parent IDs reference control numbers, so they take the same import prefix.
            var prefix = EffectiveProfile.ControlNumberPrefix;
            var parent = result.Value.GetValue<string>();
            if (!ControlNumber.TryNormalize(parent, ControlNumberCaseSensitive, prefix, out _, out var parentError))
            {
                return Cell(CoercionStatus.Error, null, false, null, new CellError("invalid-parent-id", parentError!));
            }

            return Cell(result.Status, JsonValue.Create((prefix + parent).Trim()), result.KeepRaw, null, null);
        }

        return Cell(result.Status, result.Value, result.KeepRaw, result.Format, null);

        MappedCell Cell(CoercionStatus status, JsonNode? value, bool keepRaw, string? format, CellError? error) =>
            new(column, target, rawText, status, value, keepRaw, format, error, warnings, []);
    }

    private static IEnumerable<string> ResolvedChoiceNames(
        FieldDefinition definition, string input, CoercionSettings settings, IReadOnlyList<Choice> choices, IReadOnlyList<string> missing)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in input.Split(settings.MultiValueDelimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var existing = choices.FirstOrDefault(c => c.FieldId == definition.FieldId && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            var display = existing?.Name ?? missing.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase)) ?? name;
            if (seen.Add(display))
            {
                yield return display;
            }
        }
    }

    private static bool IsZeroValue(string value) => value.Trim().All(c => c is '0' or ':' or '.' or ' ');
}
