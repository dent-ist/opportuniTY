using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;

namespace Opportunity.Import.Mapping;

public sealed record MappingOptions
{
    /// <summary>Auto-map columns the profile does not cover (exact names, then aliases).</summary>
    public bool AutoMap { get; init; } = true;

    /// <summary>The workspace's control-number case sensitivity (ADR-009 R2), used for normalization.</summary>
    public bool ControlNumberCaseSensitive { get; init; }

    /// <summary>Multi-value separator of the delimiter profile; a column's parsing may override it.</summary>
    public char MultiValueDelimiter { get; init; } = ';';
}

/// <summary>
/// Applies an import profile to a load file's header and a workspace's field catalogue: resolves every target,
/// auto-maps the rest (recording why each column matched), detects companion time columns and reports missing and new
/// columns. Auto-map never targets coding or privilege fields (Q-31).
/// </summary>
public static class MappingCompiler
{
    public const int MaxTargetsPerColumn = 4;

    private enum Rank
    {
        Exact = 0,
        Normalized = 1,
        Alias = 2,
    }

    public static CompiledMapping Compile(
        ImportProfileDefinition? profile, IReadOnlyList<string> header, FieldCatalog catalog, MappingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(catalog);
        profile ??= new ImportProfileDefinition();
        options ??= new MappingOptions();
        var issues = new List<MappingIssue>();
        ValidateProfile(profile, issues);

        var columns = header.Select((name, i) => new ColumnBinding { Column = name, Index = i, Status = ColumnStatus.Unmapped }).ToList();
        var byName = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            byName.TryAdd(column.Column.Trim(), column);
        }

        // 1. Profile rows.
        var missing = new List<string>();
        var seenRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profileRows = new Dictionary<ColumnBinding, ColumnMapping>();
        foreach (var row in profile.Columns)
        {
            var name = row.Column?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                issues.Add(Error("invalid-column", "A mapping row has no column name."));
                continue;
            }

            if (!seenRows.Add(name))
            {
                issues.Add(Error("duplicate-column-mapping", $"Column '{name}' has more than one mapping row.", name));
                continue;
            }

            if (!byName.TryGetValue(name, out var column))
            {
                missing.Add(name);
                if (!row.Ignore && row.Targets.Count > 0)
                {
                    issues.Add(Warning("column-missing",
                        $"The profile maps column '{name}' but the load file has no such column; the mapping is kept but nothing is loaded for it.", name));
                }

                continue;
            }

            profileRows[column] = row;
            column.Parsing = row.Parsing;
            if (row.Ignore)
            {
                column.Status = ColumnStatus.Ignored;
                continue;
            }

            if (row.Targets.Count > MaxTargetsPerColumn)
            {
                issues.Add(Error("too-many-targets", $"Column '{name}' maps to more than {MaxTargetsPerColumn} targets.", name));
            }

            foreach (var target in row.Targets.Take(MaxTargetsPerColumn))
            {
                column.Targets.Add(Resolve(target, column.Column, catalog, profile, issues));
            }

            if (column.Targets.Count > 0)
            {
                column.Status = ColumnStatus.Mapped;
            }
        }

        var newColumns = profile.Columns.Count == 0
            ? []
            : columns.Where(c => !profileRows.ContainsKey(c)).Select(c => c.Column).ToList();

        // Time columns named by profile rows are taken before auto-map runs.
        foreach (var column in columns.Where(c => c.Parsing?.TimeColumn is not null))
        {
            BindTimeColumn(column, column.Parsing!.TimeColumn!, byName, issues);
        }

        // 2. Auto-map.
        if (options.AutoMap)
        {
            AutoMap(columns, profileRows, catalog);
        }

        // 3. Unmapped columns: ignored or stored as metadata in a new Text field.
        foreach (var column in columns.Where(c => c.Status == ColumnStatus.Unmapped && c.MergedInto is null))
        {
            if (profile.UnmappedColumns == UnmappedColumnPolicy.CreateTextField)
            {
                var spec = new NewFieldSpec { Name = column.Column.Trim(), Type = ImportFieldType.Text };
                column.Targets.Add(Resolve(new MappingTarget { Kind = MappingTargetKind.NewField, NewField = spec }, column.Column, catalog, profile, issues));
                column.Status = ColumnStatus.StoredAsNewTextField;
            }
        }

        foreach (var column in columns.Where(c => c.MergedInto is not null && c.Status is ColumnStatus.Unmapped or ColumnStatus.Ignored))
        {
            column.Status = ColumnStatus.MergedIntoDate;
        }

        CheckTargets(columns, profile, issues);
        foreach (var column in columns)
        {
            BuildSettings(column, profile, options, issues);
        }

        var effective = EffectiveProfile(profile, columns, profileRows);
        return new CompiledMapping(header, columns, issues, missing, newColumns, effective, catalog, options.ControlNumberCaseSensitive);
    }

    /// <summary>Profile settings that do not depend on a file (locale, zone, formats, delimiters).</summary>
    public static void ValidateProfile(ImportProfileDefinition profile, List<MappingIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(issues);
        if (profile.SchemaVersion is < 1 or > ImportProfileDefinition.CurrentSchemaVersion)
        {
            issues.Add(Error("unsupported-schema-version", $"Profile schema version {profile.SchemaVersion} is not supported (expected 1–{ImportProfileDefinition.CurrentSchemaVersion})."));
        }

        if (TryLocale(profile.Parsing.Locale) is null)
        {
            issues.Add(Error("unknown-locale", $"Unknown locale '{profile.Parsing.Locale}'. Use en-US or en-GB."));
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(profile.Parsing.SourceTimeZone ?? string.Empty, out _))
        {
            issues.Add(Error("invalid-time-zone", $"Unknown time zone '{profile.Parsing.SourceTimeZone}'. Use an IANA zone such as America/New_York."));
        }

        var errors = new List<string>();
        DateFormats.ExpandDates(profile.Parsing.DateFormats, false, errors);
        issues.AddRange(errors.Select(e => Error("invalid-date-format", e)));
        LoadFileSettingsResolver.ResolveProfile(profile.LoadFile, issues);
        LoadFileSettingsResolver.ResolveEncoding(profile.LoadFile.DatEncoding, "DAT", issues);
        LoadFileSettingsResolver.ResolveEncoding(profile.LoadFile.TextEncoding, "text", issues);
        if (profile.ControlNumberPrefix is { Length: > 0 } prefix && (prefix.Length > 50 || prefix.Any(char.IsControl)))
        {
            issues.Add(Error("invalid-prefix", "The control number prefix must be at most 50 characters without control characters."));
        }
    }

    private static ImportLocale? TryLocale(string? locale) => locale?.Trim().Replace('_', '-').ToUpperInvariant() switch
    {
        "EN-US" => ImportLocale.EnUs,
        "EN-GB" => ImportLocale.EnGb,
        _ => null,
    };

    private static TargetBinding Resolve(MappingTarget target, string column, FieldCatalog catalog, ImportProfileDefinition profile, List<MappingIssue> issues)
    {
        switch (target.Kind)
        {
            case MappingTargetKind.Structural when target.Structural is { } structural && Enum.IsDefined(structural):
                return new TargetBinding
                {
                    Target = new MappingTarget { Kind = MappingTargetKind.Structural, Structural = structural },
                    Key = "s:" + structural,
                    Label = ImportTargets.Label(structural),
                    Definition = ImportTargets.Definition(structural),
                    Resolution = TargetResolution.Resolved,
                    Structural = structural,
                };
            case MappingTargetKind.Field:
                return ResolveField(target, column, catalog, profile, issues);
            case MappingTargetKind.NewField when target.NewField is { } spec:
                return ResolveNewField(spec, column, catalog, issues);
            default:
                issues.Add(Error("invalid-target", $"Column '{column}' has an incomplete target.", column));
                return Unresolved(target, column);
        }
    }

    private static TargetBinding ResolveField(MappingTarget target, string column, FieldCatalog catalog, ImportProfileDefinition profile, List<MappingIssue> issues)
    {
        FieldDefinition? field = null;
        var resolution = TargetResolution.Resolved;
        if (target.FieldId is { } id and > 0 and < FieldLimits.FirstCustomFieldId)
        {
            field = catalog.Find(id);
        }
        else
        {
            if (target.FieldName is { } name && !string.IsNullOrWhiteSpace(name))
            {
                field = catalog.Fields.FirstOrDefault(f => !f.IsDeleted && string.Equals(f.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (field is not null && target.FieldId is { } profileId && profileId != field.FieldId)
                {
                    resolution = TargetResolution.ResolvedByName;
                }
            }

            if (field is null && target.FieldId is { } fieldId && catalog.Find(fieldId) is { IsDeleted: false } byId)
            {
                field = byId;
                resolution = target.FieldName is null || string.Equals(byId.Name.Trim(), target.FieldName.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? TargetResolution.Resolved
                    : TargetResolution.ResolvedById;
            }
        }

        if (field is null || field.IsDeleted)
        {
            issues.Add(Error("unknown-field",
                $"Column '{column}' maps to field '{target.FieldName ?? target.FieldId?.ToString(System.Globalization.CultureInfo.InvariantCulture)}', which does not exist in this workspace.", column));
            return Unresolved(target, column);
        }

        if (ImportTargets.ComputedSystemFields.Contains(field.FieldId))
        {
            issues.Add(Error("not-mappable", $"{field.Name} is computed by the platform and cannot be loaded.", column));
            return Unresolved(target, column);
        }

        if (field.Storage == FieldStorage.Coding || field.IsSecurityAffecting)
        {
            if (!profile.Overlay.AllowCodingFieldOverlay)
            {
                issues.Add(Error("coding-field",
                    $"{field.Name} is a coding or privilege field; it can only be loaded when an administrator allows coding-field overlay (Q-31).", column));
                return Unresolved(target, column);
            }

            issues.Add(Warning("coding-field-overlay", $"{field.Name} is a coding or privilege field; loading it is audited (Q-31).", column));
        }

        if (field.Type == FieldType.User)
        {
            issues.Add(Warning("user-field", $"{field.Name} holds users; values must name workspace members.", column));
        }

        if (resolution is TargetResolution.ResolvedByName or TargetResolution.ResolvedById)
        {
            issues.Add(Warning(resolution == TargetResolution.ResolvedByName ? "field-resolved-by-name" : "field-resolved-by-id",
                resolution == TargetResolution.ResolvedByName
                    ? $"Field '{field.Name}' was found by name (the profile's field id differs, e.g. a profile copied from another workspace)."
                    : $"Field {field.FieldId} was found by id; it is now named '{field.Name}' (profile: '{target.FieldName}').",
                column));
        }

        return FieldBinding(field, resolution, MatchKind.Profile, null);
    }

    private static TargetBinding FieldBinding(FieldDefinition field, TargetResolution resolution, MatchKind match, string? alias) => new()
    {
        Target = new MappingTarget { Kind = MappingTargetKind.Field, FieldId = field.FieldId, FieldName = field.Name },
        Key = FieldKey.For(field.FieldId),
        Label = field.Name,
        Definition = field,
        Resolution = resolution,
        FieldId = field.FieldId,
        MatchedBy = match,
        Alias = alias,
    };

    private static TargetBinding ResolveNewField(NewFieldSpec spec, string column, FieldCatalog catalog, List<MappingIssue> issues)
    {
        var name = spec.Name?.Trim() ?? string.Empty;
        var normalized = new NewFieldSpec
        {
            Name = name,
            Type = spec.Type,
            IsMultiValue = spec.Type == ImportFieldType.MultiChoice || (spec.IsMultiValue && spec.Type is ImportFieldType.Text or ImportFieldType.Keyword),
            DecimalPrecision = spec.Type == ImportFieldType.Decimal ? spec.DecimalPrecision : null,
            DecimalScale = spec.Type == ImportFieldType.Decimal ? spec.DecimalScale : null,
        };
        var target = new MappingTarget { Kind = MappingTargetKind.NewField, NewField = normalized };
        var definition = ImportTargets.Definition(normalized);
        var errors = Enum.IsDefined(spec.Type) ? FieldRules.ValidateDefinition(definition) : [new FieldError("type", "invalid-type", "Unknown field type.")];
        if (errors.Count > 0)
        {
            issues.Add(Error("invalid-new-field", $"New field for column '{column}': {string.Join(" ", errors.Select(e => e.Message))}", column));
            return Unresolved(target, column);
        }

        var existing = catalog.Fields.FirstOrDefault(f => !f.IsDeleted && string.Equals(f.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var compatible = existing.Storage == FieldStorage.Metadata && !existing.IsSecurityAffecting
                && ImportTargets.ImportTypeOf(existing) == normalized.Type
                && (normalized.Type is not (ImportFieldType.Text or ImportFieldType.Keyword) || existing.IsMultiValue == normalized.IsMultiValue);
            if (!compatible)
            {
                issues.Add(Error("new-field-conflict",
                    $"Column '{column}' would create field '{name}', but a field of that name already exists with a different type or storage.", column));
                return Unresolved(target, column);
            }

            var binding = FieldBinding(existing, TargetResolution.ExistingField, MatchKind.Profile, null);
            return new TargetBinding
            {
                Target = target,
                Key = binding.Key,
                Label = binding.Label,
                Definition = existing,
                Resolution = TargetResolution.ExistingField,
                FieldId = existing.FieldId,
            };
        }

        return new TargetBinding
        {
            Target = target,
            Key = "n:" + ImportTargets.NormalizeName(name) + ":" + name.ToUpperInvariant(),
            Label = name,
            Definition = definition,
            Resolution = TargetResolution.WillCreate,
            CreatesField = normalized,
        };
    }

    private static TargetBinding Unresolved(MappingTarget target, string column) => new()
    {
        Target = target,
        Key = "x:" + column + ":" + Guid.NewGuid().ToString("N"),
        Label = target.FieldName ?? target.NewField?.Name ?? target.Structural?.ToString() ?? column,
        Definition = new FieldDefinition { Name = column, Type = FieldType.Text },
        Resolution = TargetResolution.Unresolved,
    };

    private sealed record Candidate(TargetBinding Prototype, IReadOnlyList<string> Names, IReadOnlyList<string> Aliases, int? DateFieldId);

    private static List<Candidate> AutoMapCandidates(FieldCatalog catalog)
    {
        var candidates = new List<Candidate>();
        var live = catalog.Fields.Where(f => !f.IsDeleted).ToList();
        var defaultNames = SystemFields.Create(Guid.Empty).ToDictionary(d => d.FieldId, d => d.Name);
        foreach (var field in live.Where(f => f.IsSystem && !ImportTargets.ComputedSystemFields.Contains(f.FieldId)).OrderBy(f => f.FieldId))
        {
            var defaultName = defaultNames.GetValueOrDefault(field.FieldId);
            candidates.Add(new Candidate(
                FieldBinding(field, TargetResolution.Resolved, MatchKind.ExactName, null),
                [field.Name, .. defaultName is null || defaultName == field.Name ? Array.Empty<string>() : [defaultName]],
                ImportTargets.SystemFieldAliases.GetValueOrDefault(field.FieldId) ?? [],
                field.Type == FieldType.Date ? field.FieldId : null));
        }

        foreach (var structural in Enum.GetValues<StructuralTarget>())
        {
            candidates.Add(new Candidate(
                new TargetBinding
                {
                    Target = new MappingTarget { Kind = MappingTargetKind.Structural, Structural = structural },
                    Key = "s:" + structural,
                    Label = ImportTargets.Label(structural),
                    Definition = ImportTargets.Definition(structural),
                    Resolution = TargetResolution.Resolved,
                    Structural = structural,
                },
                [ImportTargets.Label(structural)],
                ImportTargets.StructuralAliases[structural],
                null));
        }

        // Existing custom metadata fields, never coding or privilege fields (Q-31); well-known aliases attach by name.
        foreach (var field in live.Where(f => !f.IsSystem && f.Storage == FieldStorage.Metadata && !f.IsSecurityAffecting && f.Type != FieldType.User))
        {
            var known = ImportTargets.WellKnownFields.FirstOrDefault(w => ImportTargets.NormalizeName(w.Name) == ImportTargets.NormalizeName(field.Name));
            candidates.Add(new Candidate(FieldBinding(field, TargetResolution.Resolved, MatchKind.ExactName, null), [field.Name], known?.Aliases ?? [], null));
        }

        foreach (var known in ImportTargets.WellKnownFields)
        {
            if (live.Any(f => ImportTargets.NormalizeName(f.Name) == ImportTargets.NormalizeName(known.Name)))
            {
                continue;
            }

            var spec = new NewFieldSpec { Name = known.Name, Type = known.Type, IsMultiValue = known.IsMultiValue };
            candidates.Add(new Candidate(
                new TargetBinding
                {
                    Target = new MappingTarget { Kind = MappingTargetKind.NewField, NewField = spec },
                    Key = "n:" + ImportTargets.NormalizeName(known.Name) + ":" + known.Name.ToUpperInvariant(),
                    Label = known.Name,
                    Definition = ImportTargets.Definition(spec),
                    Resolution = TargetResolution.WillCreate,
                    CreatesField = spec,
                },
                [known.Name],
                known.Aliases,
                null));
        }

        return candidates;
    }

    private static (Rank Rank, int Index, string? Alias)? Match(string header, Candidate candidate)
    {
        var trimmed = header.Trim();
        var normalized = ImportTargets.NormalizeName(trimmed);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (candidate.Names.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return (Rank.Exact, 0, null);
        }

        if (candidate.Names.Any(n => ImportTargets.NormalizeName(n) == normalized))
        {
            return (Rank.Normalized, 0, null);
        }

        for (var i = 0; i < candidate.Aliases.Count; i++)
        {
            if (ImportTargets.NormalizeName(candidate.Aliases[i]) == normalized)
            {
                return (Rank.Alias, i, candidate.Aliases[i]);
            }
        }

        return null;
    }

    private static void AutoMap(List<ColumnBinding> columns, Dictionary<ColumnBinding, ColumnMapping> profileRows, FieldCatalog catalog)
    {
        var taken = new HashSet<string>(columns.SelectMany(c => c.Targets).Select(t => t.Key), StringComparer.Ordinal);
        var open = columns.Where(c => c.Status == ColumnStatus.Unmapped && c.MergedInto is null && !profileRows.ContainsKey(c)
                || (profileRows.TryGetValue(c, out var row) && !row.Ignore && row.Targets.Count == 0 && c.MergedInto is null))
            .ToList();
        var candidates = AutoMapCandidates(catalog);

        foreach (var candidate in candidates)
        {
            if (taken.Contains(candidate.Prototype.Key))
            {
                continue;
            }

            var best = open
                .Select(c => (Column: c, Match: Match(c.Column, candidate)))
                .Where(m => m.Match is not null)
                .OrderBy(m => m.Match!.Value.Rank).ThenBy(m => m.Match!.Value.Index).ThenBy(m => m.Column.Index)
                .FirstOrDefault();
            if (best.Column is null)
            {
                continue;
            }

            var (rank, _, alias) = best.Match!.Value;
            var p = candidate.Prototype;
            best.Column.Targets.Add(new TargetBinding
            {
                Target = p.Target,
                Key = p.Key,
                Label = p.Label,
                Definition = p.Definition,
                Resolution = p.Resolution,
                FieldId = p.FieldId,
                Structural = p.Structural,
                CreatesField = p.CreatesField,
                MatchedBy = rank switch
                {
                    Rank.Exact => MatchKind.ExactName,
                    Rank.Normalized => MatchKind.NormalizedName,
                    _ => MatchKind.Alias,
                },
                Alias = alias,
            });
            best.Column.Status = ColumnStatus.Mapped;
            taken.Add(p.Key);
        }

        // Companion time columns of auto-mapped date system fields (DateSent + TimeSent).
        var timeIds = new HashSet<int>(ImportTargets.TimeColumnAliases.Keys);
        foreach (var column in columns.Where(c => c.Status == ColumnStatus.Mapped && c.TimeColumn is null && c.Parsing?.TimeColumn is null))
        {
            var dateTarget = column.Targets.FirstOrDefault(t => t.MatchedBy != MatchKind.Profile && t.FieldId is { } id && timeIds.Contains(id));
            if (dateTarget is null)
            {
                continue;
            }

            var aliases = ImportTargets.TimeColumnAliases[dateTarget.FieldId!.Value];
            var time = columns
                .Where(c => c.Status == ColumnStatus.Unmapped && c.MergedInto is null && !profileRows.ContainsKey(c))
                .Select(c => (Column: c, Index: aliases.ToList().FindIndex(a => ImportTargets.NormalizeName(a) == ImportTargets.NormalizeName(c.Column))))
                .Where(m => m.Index >= 0)
                .OrderBy(m => m.Index)
                .FirstOrDefault();
            if (time.Column is not null)
            {
                column.TimeColumn = time.Column.Column;
                column.TimeColumnIndex = time.Column.Index;
                time.Column.MergedInto = column.Column;
                column.Parsing = (column.Parsing ?? new ColumnParsing()) with { TimeColumn = time.Column.Column };
            }
        }
    }

    private static void BindTimeColumn(ColumnBinding column, string timeColumn, Dictionary<string, ColumnBinding> byName, List<MappingIssue> issues)
    {
        if (!byName.TryGetValue(timeColumn.Trim(), out var time))
        {
            issues.Add(Error("time-column-missing", $"Column '{column.Column}' merges time column '{timeColumn}', which the load file does not have.", column.Column));
            return;
        }

        if (time == column)
        {
            issues.Add(Error("invalid-time-column", $"Column '{column.Column}' cannot be its own time column.", column.Column));
            return;
        }

        if (time.MergedInto is not null)
        {
            issues.Add(Error("invalid-time-column", $"Time column '{time.Column}' is already merged into '{time.MergedInto}'.", column.Column));
            return;
        }

        column.TimeColumn = time.Column;
        column.TimeColumnIndex = time.Index;
        time.MergedInto = column.Column;
    }

    private static void CheckTargets(List<ColumnBinding> columns, ImportProfileDefinition profile, List<MappingIssue> issues)
    {
        var active = columns.Where(c => c.Status is ColumnStatus.Mapped or ColumnStatus.StoredAsNewTextField).ToList();
        foreach (var group in active.SelectMany(c => c.Targets.Where(t => t.Usable).Select(t => (Column: c, Target: t))).GroupBy(x => x.Target.Key))
        {
            var list = group.ToList();
            if (list.Count > 1)
            {
                issues.Add(Error("duplicate-target",
                    $"{list[0].Target.Label} is mapped from more than one column ({string.Join(", ", list.Select(x => x.Column.Column).Distinct())}).",
                    list[1].Column.Column));
            }
        }

        foreach (var column in active.Where(c => c.TimeColumn is not null && !c.Targets.Any(t => t.Definition.Type == FieldType.Date)))
        {
            issues.Add(Warning("time-column-unused", $"Column '{column.Column}' names a time column but maps to no date field.", column.Column));
        }

        var keyField = profile.Overlay.KeyFieldId ?? SystemFields.ControlNumber;
        var hasKey = active.Any(c => c.Targets.Any(t => t.Usable && t.FieldId == keyField));
        var hasControl = active.Any(c => c.Targets.Any(t => t.Usable && t.IsControlNumber));
        if (!hasControl && (profile.Mode != ImportMode.Overlay || !hasKey))
        {
            issues.Add(Error("control-number-unmapped", "No column is mapped to Control Number; every document needs one."));
        }
        else if (profile.Mode != ImportMode.Append && !hasKey)
        {
            issues.Add(Error("overlay-key-unmapped", "Overlay needs the overlay key field to be mapped."));
        }
    }

    private static void BuildSettings(ColumnBinding column, ImportProfileDefinition profile, MappingOptions options, List<MappingIssue> issues)
    {
        var parsing = column.Parsing ?? new ColumnParsing();
        var defaults = profile.Parsing;
        var locale = TryLocale(defaults.Locale) ?? ImportLocale.EnUs;
        var errors = new List<string>();
        var dates = DateFormats.ExpandDates(parsing.DateFormats ?? defaults.DateFormats, locale == ImportLocale.EnGb, errors);
        var times = DateFormats.ExpandTimes(parsing.TimeFormats, errors);
        foreach (var error in errors.Distinct())
        {
            issues.Add(Error("invalid-date-format", $"Column '{column.Column}': {error}", column.Column));
        }

        var zoneId = parsing.SourceTimeZone ?? defaults.SourceTimeZone;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId ?? string.Empty, out var zone))
        {
            if (parsing.SourceTimeZone is not null)
            {
                issues.Add(Error("invalid-time-zone", $"Column '{column.Column}': unknown time zone '{zoneId}'.", column.Column));
            }

            zone = TimeZoneInfo.Utc;
        }

        var multi = options.MultiValueDelimiter;
        if (parsing.MultiValueDelimiter is { } text)
        {
            if (LoadFileSettingsResolver.TryParseDelimiter(text, out var c) && c is not ('\r' or '\n' or '\0'))
            {
                multi = c;
            }
            else
            {
                issues.Add(Error("invalid-delimiter", $"Column '{column.Column}': multi-value delimiter '{text}' is not one character.", column.Column));
            }
        }

        column.Settings = new CoercionSettings
        {
            Locale = locale,
            MultiValueDelimiter = multi,
            NewlineMarker = null,
            DateFormats = DateFormats.Combine(dates, times),
            SourceTimeZone = zone,
            UnparseableDateAsAbsent = defaults.UnparseableDatesAsBlank,
            CreateMissingChoices = parsing.CreateMissingChoices ?? false,
        };
        column.TrueValues = [.. defaults.TrueValues, .. parsing.TrueValues ?? []];
        column.FalseValues = [.. defaults.FalseValues, .. parsing.FalseValues ?? []];
        if (column.TrueValues.Intersect(column.FalseValues, StringComparer.OrdinalIgnoreCase).Any())
        {
            issues.Add(Error("invalid-boolean-values", $"Column '{column.Column}': a value is listed as both Yes and No.", column.Column));
        }
    }

    private static ImportProfileDefinition EffectiveProfile(
        ImportProfileDefinition profile, List<ColumnBinding> columns, Dictionary<ColumnBinding, ColumnMapping> profileRows)
    {
        var rows = new List<ColumnMapping>();
        foreach (var column in columns)
        {
            profileRows.TryGetValue(column, out var original);
            var name = original?.Column ?? column.Column;
            rows.Add(column.Status switch
            {
                ColumnStatus.Mapped or ColumnStatus.StoredAsNewTextField => new ColumnMapping
                {
                    Column = name,
                    Targets = [.. column.Targets.Select(t => t.Target)],
                    Parsing = column.Parsing,
                },
                _ => new ColumnMapping { Column = name, Ignore = true, Parsing = column.Parsing },
            });
        }

        // Rows for columns this file lacks stay in the profile.
        var present = new HashSet<string>(columns.Select(c => c.Column.Trim()), StringComparer.OrdinalIgnoreCase);
        rows.AddRange(profile.Columns.Where(r => !string.IsNullOrWhiteSpace(r.Column) && !present.Contains(r.Column.Trim()))
            .DistinctBy(r => r.Column.Trim(), StringComparer.OrdinalIgnoreCase));
        return profile with { Columns = rows };
    }

    private static MappingIssue Error(string code, string message, string? column = null) => new(MappingIssueSeverity.Error, code, message, column);

    private static MappingIssue Warning(string code, string message, string? column = null) => new(MappingIssueSeverity.Warning, code, message, column);
}
