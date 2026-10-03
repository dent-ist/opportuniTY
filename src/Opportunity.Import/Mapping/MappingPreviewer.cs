using Opportunity.Contracts.Import;
using Opportunity.Core.Fields;
using Opportunity.Import.LoadFiles;

namespace Opportunity.Import.Mapping;

public sealed record MappingPreviewOptions
{
    public const int MaxRows = 100;

    public bool AutoMap { get; init; } = true;

    public int Rows { get; init; } = DatPreview.DefaultRows;

    /// <summary>The stream holds only the leading bytes of the DAT; a record cut off at its end is dropped.</summary>
    public bool SampleIsPartial { get; init; }

    public bool ControlNumberCaseSensitive { get; init; }

    /// <summary>Parser limit for the preview (SEC-15).</summary>
    public int MaxRecordBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// The mapping step of the import wizard (guide §5.1 step 3): parses the first rows of a DAT with the profile's
/// delimiters and encoding, applies the mapping and shows coerced values with per-column error counts.
/// </summary>
public static class MappingPreviewer
{
    private const int SampleValuesPerColumn = 3;
    private const int SampleChoiceNames = 20;

    public static async Task<MappingPreviewResult> PreviewAsync(
        Stream stream, ImportProfileDefinition? profile, FieldCatalog catalog, MappingPreviewOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(catalog);
        profile ??= new ImportProfileDefinition();
        options ??= new MappingPreviewOptions();
        var rowLimit = Math.Clamp(options.Rows, 1, MappingPreviewOptions.MaxRows);

        var settingsIssues = new List<MappingIssue>();
        var readerOptions = LoadFileSettingsResolver.ReaderOptions(profile.LoadFile, settingsIssues);
        if (settingsIssues.Any(i => i.Severity == MappingIssueSeverity.Error))
        {
            // The file cannot be read with these settings; report without parsing.
            var empty = MappingCompiler.Compile(profile, [], catalog, new MappingOptions { AutoMap = false });
            return new MappingPreviewResult(
                FileInfo(readerOptions.Profile, null, [], false, []), [], empty.MissingColumns, [], [], [],
                [.. settingsIssues, .. empty.Issues.Where(i => !settingsIssues.Contains(i))], false, profile);
        }

        var reader = await DatReader.OpenAsync(stream, new DatReaderOptions
        {
            Profile = readerOptions.Profile,
            EncodingOverride = readerOptions.EncodingOverride,
            HasHeader = readerOptions.HasHeader,
            ConvertNewlineCharacter = readerOptions.ConvertNewlineCharacter,
            MaxRecordBytes = options.MaxRecordBytes,
            MaxRejectedRows = null,
            ReturnRejectedRecords = true,
        }, leaveOpen: true, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var records = new List<DatRecord>();
            if (!reader.HasPreflightErrors)
            {
                while (records.Count <= rowLimit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
                {
                    records.Add(record);
                }

                if (records.Count > rowLimit)
                {
                    records.RemoveAt(records.Count - 1);
                }
                else if (options.SampleIsPartial && records.Count > 0)
                {
                    // The sample ended inside the file: its last record may be cut off.
                    records.RemoveAt(records.Count - 1);
                }
            }

            var header = reader.Header.Names;
            var mapping = MappingCompiler.Compile(profile, header, catalog, new MappingOptions
            {
                AutoMap = options.AutoMap,
                ControlNumberCaseSensitive = options.ControlNumberCaseSensitive,
                MultiValueDelimiter = readerOptions.Profile.MultiValue,
            });

            var parserIssues = reader.PreflightIssues.Select(ToMappingIssue).ToList();
            var misdecode = reader.Issues.Any(i => i.Kind == DatIssueKind.SuspectedMisdecode)
                || header.Any(DatPreview.LooksMisdecoded)
                || records.Any(r => r.Values.Any(DatPreview.LooksMisdecoded));
            return Build(mapping, reader, records, parserIssues, misdecode);
        }
    }

    private static MappingPreviewResult Build(
        CompiledMapping mapping, DatReader reader, List<DatRecord> records, List<MappingIssue> parserIssues, bool misdecode)
    {
        var stats = mapping.Columns.ToDictionary(c => c, _ => new ColumnStats());
        var rows = new List<RowPreview>();
        var seenControlNumbers = new Dictionary<string, long>(StringComparer.Ordinal);
        var missingChoices = new Dictionary<string, (TargetBinding Target, List<string> Names)>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            foreach (var column in mapping.Columns)
            {
                var raw = column.Index < record.Values.Count ? record.Values[column.Index] : string.Empty;
                stats[column].Observe(raw);
            }

            var rowIssues = record.Issues.Select(ToMappingIssue).ToList();
            if (record.IsRejected)
            {
                rows.Add(new RowPreview(record.RowNumber, record.LineNumber, record.ControlNumber, true, rowIssues, [], 0));
                continue;
            }

            var mapped = mapping.Map(record.RowNumber, record.Values);
            var cells = new List<CellPreview>(mapped.Cells.Count);
            foreach (var cell in mapped.Cells)
            {
                var error = cell.Error;
                if (cell.Target.IsControlNumber && error is null && mapped.ControlNumberNorm is { } norm)
                {
                    if (seenControlNumbers.TryGetValue(norm, out var firstRow))
                    {
                        error = new CellError("duplicate-control-number", $"Control number already used by row {firstRow}.");
                    }
                    else
                    {
                        seenControlNumbers[norm] = record.RowNumber;
                    }
                }

                if (error is not null)
                {
                    stats[cell.Column].Errors++;
                }

                stats[cell.Column].Warnings += cell.Warnings.Count;
                foreach (var name in cell.MissingChoices)
                {
                    if (!missingChoices.TryGetValue(cell.Target.Key, out var entry))
                    {
                        entry = (cell.Target, []);
                        missingChoices[cell.Target.Key] = entry;
                    }

                    if (!entry.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        entry.Names.Add(name);
                    }
                }

                cells.Add(new CellPreview(cell.Column.Column, cell.Target.Label, cell.Raw, error is null ? cell.Value?.DeepClone() : null, error, cell.Warnings));
            }

            rows.Add(new RowPreview(record.RowNumber, record.LineNumber, mapped.ControlNumber ?? record.ControlNumber, false, rowIssues,
                cells, cells.Count(c => c.Error is not null)));
        }

        var issues = new List<MappingIssue>(mapping.Issues);
        foreach (var (target, names) in missingChoices.Values.Where(v => v.Target.CreatesField is null))
        {
            issues.Add(new MappingIssue(MappingIssueSeverity.Warning, "choices-to-create",
                $"Will create {names.Count} choice(s) in {target.Label}: {string.Join(", ", names.Take(SampleChoiceNames))}."));
        }

        var newFields = mapping.Targets
            .Where(t => t.CreatesField is not null)
            .DistinctBy(t => t.Key)
            .Select(t =>
            {
                var names = missingChoices.TryGetValue(t.Key, out var entry) ? entry.Names : [];
                return new NewFieldPreview(t.CreatesField!.Name, t.CreatesField.Type, t.Definition.IsMultiValue, names.Count, [.. names.Take(SampleChoiceNames)]);
            })
            .ToList();

        var columns = mapping.Columns.Select(c =>
        {
            var s = stats[c];
            return new ColumnPreview(
                c.Column,
                c.Index,
                c.Status,
                [.. c.Targets.Select(t => new TargetPreview(t.Target, t.Label, ImportTargets.TypeLabel(t.Definition), t.Definition.IsMultiValue, t.Resolution, t.MatchedBy, t.Alias))],
                c.MergedInto,
                s.Samples,
                s.Values,
                s.Blanks,
                s.Errors,
                s.Warnings);
        }).ToList();

        var file = FileInfo(reader.Profile, reader.Encoding, reader.Header.Names, misdecode, parserIssues);
        var canImport = !mapping.HasErrors && !reader.HasPreflightErrors;
        return new MappingPreviewResult(file, columns, mapping.MissingColumns, mapping.NewColumns, rows, newFields, issues, canImport, mapping.EffectiveProfile);
    }

    private static LoadFilePreviewInfo FileInfo(DelimiterProfile profile, DetectedEncoding? encoding, IReadOnlyList<string> header, bool misdecode, List<MappingIssue> parserIssues) =>
        new(
            encoding?.Name ?? "unknown",
            encoding is null ? "none" : char.ToLowerInvariant(encoding.Source.ToString()[0]) + encoding.Source.ToString()[1..],
            profile.Name,
            LoadFileSettingsResolver.Describe(profile.Column),
            profile.Quote is { } q ? LoadFileSettingsResolver.Describe(q) : null,
            profile.Newline is { } n ? LoadFileSettingsResolver.Describe(n) : null,
            LoadFileSettingsResolver.Describe(profile.MultiValue),
            LoadFileSettingsResolver.Describe(profile.NestedValue),
            header,
            misdecode,
            parserIssues);

    private static MappingIssue ToMappingIssue(DatIssue issue) => new(
        issue.Severity == DatIssueSeverity.Error ? MappingIssueSeverity.Error : MappingIssueSeverity.Warning,
        ToCode(issue.Kind),
        issue.Message,
        issue.Column);

    /// <summary><c>FieldCountMismatch</c> → <c>field-count-mismatch</c>.</summary>
    private static string ToCode(DatIssueKind kind)
    {
        var name = kind.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    private sealed class ColumnStats
    {
        public List<string> Samples { get; } = [];

        public int Values { get; private set; }

        public int Blanks { get; private set; }

        public int Errors { get; set; }

        public int Warnings { get; set; }

        public void Observe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Blanks++;
                return;
            }

            Values++;
            if (Samples.Count < SampleValuesPerColumn && !Samples.Contains(raw, StringComparer.Ordinal))
            {
                Samples.Add(raw);
            }
        }
    }
}
