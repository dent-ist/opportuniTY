using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Opportunity.Application.Import;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Core.Fields;
using Opportunity.Import.Jobs;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;
using Opportunity.Import.Volumes;

namespace Opportunity.Import.Preflight;

/// <summary>What a pre-flight checks: the uploaded load file(s) with the effective profile, against the workspace.</summary>
public sealed record ImportPreflightRequest
{
    public required Guid WorkspaceId { get; init; }

    /// <summary>The DAT; null for an OPT-only load.</summary>
    public Stream? Dat { get; init; }

    public Stream? Opt { get; init; }

    public required ImportProfileDefinition Profile { get; init; }

    public required FieldCatalog Catalog { get; init; }

    public bool AutoMap { get; init; } = true;

    public bool ControlNumberCaseSensitive { get; init; }

    public IReadOnlyCollection<int> CodingOverlayFieldIds { get; init; } = [];
}

/// <summary>Limits of a pre-flight (memory stays bounded whatever the file size).</summary>
public sealed record ImportPreflightOptions
{
    /// <summary>Issues kept for the download; all are counted.</summary>
    public int MaxStoredIssues { get; init; } = 100_000;

    /// <summary>
    /// Path sampling rule: the native/text/image paths of the first <see cref="FullPathCheckRows"/> rows that name files
    /// are all checked; after that every <see cref="PathSampleInterval"/>-th such row is (a <c>PATHS_SAMPLED</c> warning
    /// then says how many were checked).
    /// </summary>
    public int FullPathCheckRows { get; init; } = 10_000;

    public int PathSampleInterval { get; init; } = 100;

    /// <summary>Distinct control numbers remembered for the within-file duplicate check (8-byte hashes plus row numbers).</summary>
    public int MaxTrackedKeys { get; init; } = 5_000_000;

    /// <summary>Control numbers looked up in the workspace per round trip.</summary>
    public int KeyBatchSize { get; init; } = 5_000;
}

/// <summary>The outcome of a pre-flight: exact counts, and the issues in row order (up to the stored maximum).</summary>
public sealed class ImportPreflightResult
{
    public long RowsRead { get; internal set; }

    public long ErrorCount { get; internal set; }

    public long WarningCount { get; internal set; }

    public long IssuesDropped { get; internal set; }

    public IReadOnlyList<ImportPreflightIssue> Issues { get; internal set; } = [];

    public IReadOnlyList<ImportPreflightIssueCount> IssueCounts { get; internal set; } = [];

    public bool Blocking => ErrorCount > 0;
}

/// <summary>Receives the issues of a pre-flight check.</summary>
public interface IImportPreflightIssueSink
{
    void Add(ImportPreflightIssue issue);
}

/// <summary>A row whose control number parsed, handed to the key checks in batches.</summary>
public sealed record ImportPreflightKeyRow(long Row, string ControlNumber, string ControlNumberNorm);

/// <param name="Store">Read-only lookups of the workspace's documents.</param>
public sealed record ImportPreflightContext(Guid WorkspaceId, ImportProfileDefinition Profile, IImportPreflightStore Store);

/// <summary>
/// A pluggable key check of the pre-flight (collisions per import mode). Each runs over every batch of rows with a
/// control number; a check that does not apply to the profile's mode does nothing. Register further checks (e.g.
/// <c>KEY_MISSING</c> for overlay modes) in DI as <see cref="IImportPreflightCheck"/>.
/// </summary>
public interface IImportPreflightCheck
{
    Task CheckKeysAsync(
        ImportPreflightContext context, IReadOnlyList<ImportPreflightKeyRow> rows, IImportPreflightIssueSink issues, CancellationToken cancellationToken);
}

/// <summary>Append loads new documents only: a control number that exists (or existed) in the workspace is <c>KEY_EXISTS</c>.</summary>
public sealed class AppendKeyCollisionCheck : IImportPreflightCheck
{
    public async Task CheckKeysAsync(
        ImportPreflightContext context, IReadOnlyList<ImportPreflightKeyRow> rows, IImportPreflightIssueSink issues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(issues);
        if (context.Profile.Mode != ImportMode.Append || rows.Count == 0)
        {
            return;
        }

        var states = await context.Store.FindKeysAsync(context.WorkspaceId, [.. rows.Select(r => r.ControlNumberNorm).Distinct(StringComparer.Ordinal)], cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (!states.TryGetValue(row.ControlNumberNorm, out var state))
            {
                continue;
            }

            issues.Add(state switch
            {
                ImportKeyState.Retired => new ImportPreflightIssue(row.Row, row.ControlNumber, null, ImportPreflightCodes.KeyRetired, ImportRowIssueSeverity.Error,
                    $"Control number {row.ControlNumber} belonged to a removed document and cannot be reused."),
                ImportKeyState.Deleted => new ImportPreflightIssue(row.Row, row.ControlNumber, null, ImportPreflightCodes.KeyExists, ImportRowIssueSeverity.Error,
                    $"A deleted document has control number {row.ControlNumber}; Append loads new documents only."),
                _ => new ImportPreflightIssue(row.Row, row.ControlNumber, null, ImportPreflightCodes.KeyExists, ImportRowIssueSeverity.Error,
                    $"A document with control number {row.ControlNumber} already exists; Append loads new documents only."),
            });
        }
    }
}

/// <summary>Pre-flight issue codes (UPPER_SNAKE_CASE). Other row codes are the importer's codes, upper-cased.</summary>
public static partial class ImportPreflightCodes
{
    public const string KeyExists = "KEY_EXISTS";
    public const string KeyMissing = "KEY_MISSING";
    public const string KeyRetired = "KEY_RETIRED";
    public const string DuplicateControlNumber = "DUPLICATE_CONTROL_NUMBER";
    public const string RequiredFieldMissing = "REQUIRED_FIELD_MISSING";
    public const string DateParseFailed = "DATE_PARSE_FAILED";
    public const string ChoiceWillBeCreated = "CHOICE_WILL_BE_CREATED";
    public const string PathsSampled = "PATHS_SAMPLED";
    public const string PathsNotChecked = "PATHS_NOT_CHECKED";
    public const string VolumeUnavailable = "VOLUME_UNAVAILABLE";
    public const string DuplicatesNotFullyChecked = "DUPLICATES_NOT_FULLY_CHECKED";
    public const string FileUnreadable = "FILE_UNREADABLE";
    public const string OptOrphanDocument = "OPT_ORPHAN_DOCUMENT";
    public const string OptDocumentNotFound = "OPT_DOCUMENT_NOT_FOUND";

    /// <summary><c>field-count-mismatch</c> / <c>FieldCountMismatch</c> → <c>FIELD_COUNT_MISMATCH</c>, with the ticket's names for dates and blanks.</summary>
    public static string From(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code switch
        {
            "invalid-date" => DateParseFailed,
            "control-number-missing" => RequiredFieldMissing,
            "duplicate-control-number" => DuplicateControlNumber,
            _ => Pascal().Replace(code, "$1_$2").Replace('-', '_').ToUpperInvariant(),
        };
    }

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex Pascal();
}

/// <summary>
/// Pre-flight validation of a load (E08-T06): one streaming read of the DAT (and OPT) with the import's own parser,
/// mapping and row builder, so it reports what the import would: the header and mapping, field counts per row, required
/// values (control number), value coercion (date parse failures, choices, hashes), duplicate control numbers within the
/// file, collisions with the workspace (<see cref="IImportPreflightCheck"/>) and the existence of native, text and image
/// files in the volume (all rows up to <see cref="ImportPreflightOptions.FullPathCheckRows"/>, then a sample). It
/// writes nothing: the workspace is only read, files are only stat'ed.
/// </summary>
public sealed class ImportPreflightRunner(
    IImportPreflightStore store,
    IEnumerable<IImportPreflightCheck> checks,
    ImportVolumeOptions? volumes = null,
    ImportPreflightOptions? options = null)
{
    private readonly ImportPreflightOptions _options = options ?? new ImportPreflightOptions();
    private readonly IReadOnlyList<IImportPreflightCheck> _checks = [.. checks];

    public async Task<ImportPreflightResult> RunAsync(ImportPreflightRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sink = new Collector(_options.MaxStoredIssues);
        var context = new ImportPreflightContext(request.WorkspaceId, request.Profile, store);
        var paths = new PathChecker(_options, volumes, request.Profile, sink);
        var keys = new KeyTracker(_options.MaxTrackedKeys);
        long rows = 0;
        if (request.Dat is { } dat)
        {
            rows = await CheckDatAsync(request, dat, context, paths, keys, sink, cancellationToken).ConfigureAwait(false);
        }

        if (request.Opt is { } opt)
        {
            var optRows = await CheckOptAsync(request, opt, context, paths, request.Dat is null ? null : keys, sink, cancellationToken).ConfigureAwait(false);
            if (request.Dat is null)
            {
                rows = optRows;
            }
        }

        paths.Finish();
        if (keys.Overflowed)
        {
            sink.Add(new ImportPreflightIssue(0, null, null, ImportPreflightCodes.DuplicatesNotFullyChecked, ImportRowIssueSeverity.Warning,
                $"The file has more than {_options.MaxTrackedKeys:N0} control numbers; duplicates among the later ones were not checked (the import still rejects them)."));
        }

        return sink.Result(rows);
    }

    private async Task<long> CheckDatAsync(
        ImportPreflightRequest request, Stream dat, ImportPreflightContext context, PathChecker paths, KeyTracker keys, Collector sink,
        CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var settingsIssues = new List<MappingIssue>();
        var readerOptions = ImportSource.ReaderOptions(profile, null, settingsIssues);
        foreach (var issue in settingsIssues)
        {
            sink.Add(FileIssue(issue));
        }

        if (settingsIssues.Any(i => i.Severity == MappingIssueSeverity.Error))
        {
            return 0;
        }

        var reader = await DatReader.OpenAsync(dat, readerOptions, leaveOpen: true, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            foreach (var issue in reader.PreflightIssues)
            {
                sink.Add(new ImportPreflightIssue(0, null, issue.Column, ImportPreflightCodes.From(issue.Kind.ToString()), Severity(issue.Severity), issue.Message));
            }

            if (reader.HasPreflightErrors)
            {
                return 0;
            }

            var mapping = MappingCompiler.Compile(profile, reader.Header.Names, request.Catalog, new MappingOptions
            {
                AutoMap = request.AutoMap,
                ControlNumberCaseSensitive = request.ControlNumberCaseSensitive,
                MultiValueDelimiter = readerOptions.Profile.MultiValue,
            });
            foreach (var issue in mapping.Issues)
            {
                sink.Add(FileIssue(issue));
            }

            var codingFields = request.CodingOverlayFieldIds.ToHashSet();
            foreach (var id in ImportStartScope.CodingFieldsNotEnabled(mapping, codingFields))
            {
                sink.Add(new ImportPreflightIssue(0, null, null, "CODING_FIELD_NOT_ENABLED", ImportRowIssueSeverity.Error,
                    $"Coding field {id} is mapped but not listed in codingOverlayFieldIds (Q-31)."));
            }

            var usable = !mapping.HasErrors;
            if (usable)
            {
                paths.Bind(mapping);
            }

            var createsChoices = mapping.Columns.Where(c => c.Parsing?.CreateMissingChoices == true).Select(c => c.Column)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newChoices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var batch = new List<ImportPreflightKeyRow>(_options.KeyBatchSize);
            long rows = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
            {
                rows = record.RowNumber;
                if (!usable || record.IsRejected)
                {
                    foreach (var issue in record.Issues)
                    {
                        sink.Add(new ImportPreflightIssue(record.RowNumber, record.ControlNumber, issue.Column,
                            ImportPreflightCodes.From(issue.Kind.ToString()), Severity(issue.Severity), issue.Message));
                    }

                    continue;
                }

                var row = ImportRowBuilder.Build(mapping, record, record.RowNumber, record.LineNumber, request.WorkspaceId, Guid.Empty, codingFields);
                foreach (var issue in row.Issues)
                {
                    if (issue.Code == "unknown-choice" && issue.Column is { } column && createsChoices.Contains(column))
                    {
                        newChoices[column] = newChoices.GetValueOrDefault(column) + 1;
                        continue;
                    }

                    sink.Add(new ImportPreflightIssue(record.RowNumber, row.ControlNumber, issue.Column, ImportPreflightCodes.From(issue.Code),
                        issue.Severity == ImportIssueSeverity.Error ? ImportRowIssueSeverity.Error : ImportRowIssueSeverity.Warning, issue.Message));
                }

                if (row.ControlNumberNorm is { } norm)
                {
                    if (keys.FirstRow(norm, record.RowNumber) is { } first)
                    {
                        sink.Add(new ImportPreflightIssue(record.RowNumber, row.ControlNumber, null, ImportPreflightCodes.DuplicateControlNumber,
                            ImportRowIssueSeverity.Error, $"Control number {row.ControlNumber} is already used by row {first} of this load file."));
                    }
                    else
                    {
                        batch.Add(new ImportPreflightKeyRow(record.RowNumber, row.ControlNumber ?? norm, norm));
                        if (batch.Count >= _options.KeyBatchSize)
                        {
                            await RunChecksAsync(context, batch, sink, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                paths.CheckRow(record.RowNumber, row.ControlNumber, record.Values);
            }

            await RunChecksAsync(context, batch, sink, cancellationToken).ConfigureAwait(false);
            foreach (var (column, count) in newChoices)
            {
                sink.Add(new ImportPreflightIssue(0, null, column, ImportPreflightCodes.ChoiceWillBeCreated, ImportRowIssueSeverity.Warning,
                    string.Create(CultureInfo.InvariantCulture, $"{count:N0} row(s) of column {column} use choices that do not exist yet; the import creates them.")));
            }

            return rows;
        }
    }

    private async Task<long> CheckOptAsync(
        ImportPreflightRequest request, Stream opt, ImportPreflightContext context, PathChecker paths, KeyTracker? datKeys, Collector sink,
        CancellationToken cancellationToken)
    {
        var profile = request.Profile;
        var byBates = profile.Images.MatchBy == ImageMatchField.BegBates;
        var prefix = byBates ? null : profile.ControlNumberPrefix;
        var reader = OptReader.Open(opt, leaveOpen: true);
        var lookups = new List<ImportPreflightKeyRow>(_options.KeyBatchSize);
        long rows = 0;
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } record)
            {
                rows = record.RowNumber;
                if (record.Problem is { } problem)
                {
                    sink.Add(new ImportPreflightIssue(record.RowNumber, record.ImageKey, null, "OPT_ROW_MALFORMED", ImportRowIssueSeverity.Warning,
                        Truncate($"OPT row {record.RowNumber} cannot be read: {problem} Its page has no image.")));
                    continue;
                }

                paths.CheckImage(record.RowNumber, record.ImageKey, record.Path);
                if (!record.DocumentBreak || byBates
                    || !ControlNumber.TryNormalize(record.ImageKey, request.ControlNumberCaseSensitive, prefix, out var norm, out _))
                {
                    continue;
                }

                if (datKeys is not null)
                {
                    if (!datKeys.Contains(norm))
                    {
                        sink.Add(new ImportPreflightIssue(record.RowNumber, record.ImageKey, null, ImportPreflightCodes.OptOrphanDocument, ImportRowIssueSeverity.Warning,
                            $"OPT document {record.ImageKey} has no row in the DAT; its images will not be loaded."));
                    }
                }
                else
                {
                    lookups.Add(new ImportPreflightKeyRow(record.RowNumber, record.ImageKey, norm));
                    if (lookups.Count >= _options.KeyBatchSize)
                    {
                        await FindOptDocumentsAsync(context, lookups, sink, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        await FindOptDocumentsAsync(context, lookups, sink, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    /// <summary>An OPT-only load replaces the pages of existing documents: a document break naming none fails.</summary>
    private static async Task FindOptDocumentsAsync(
        ImportPreflightContext context, List<ImportPreflightKeyRow> rows, Collector sink, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var states = await context.Store.FindKeysAsync(context.WorkspaceId, [.. rows.Select(r => r.ControlNumberNorm).Distinct(StringComparer.Ordinal)], cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows.Where(r => !states.TryGetValue(r.ControlNumberNorm, out var s) || s != ImportKeyState.Exists))
        {
            sink.Add(new ImportPreflightIssue(row.Row, row.ControlNumber, null, ImportPreflightCodes.OptDocumentNotFound, ImportRowIssueSeverity.Error,
                $"No document of the workspace has control number '{row.ControlNumber}' (orphan OPT document)."));
        }

        rows.Clear();
    }

    private async Task RunChecksAsync(ImportPreflightContext context, List<ImportPreflightKeyRow> batch, Collector sink, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        foreach (var check in _checks)
        {
            await check.CheckKeysAsync(context, batch, sink, cancellationToken).ConfigureAwait(false);
        }

        batch.Clear();
    }

    private static ImportPreflightIssue FileIssue(MappingIssue issue) => new(
        0, null, issue.Column, ImportPreflightCodes.From(issue.Code),
        issue.Severity == MappingIssueSeverity.Error ? ImportRowIssueSeverity.Error : ImportRowIssueSeverity.Warning, issue.Message);

    private static ImportRowIssueSeverity Severity(DatIssueSeverity severity) =>
        severity == DatIssueSeverity.Error ? ImportRowIssueSeverity.Error : ImportRowIssueSeverity.Warning;

    private static string Truncate(string message) => message.Length <= ImportRowIssue.MaxMessageLength ? message : message[..ImportRowIssue.MaxMessageLength];

    /// <summary>Exact counts by code and severity; issues kept in row order up to the maximum.</summary>
    private sealed class Collector(int maxStored) : IImportPreflightIssueSink
    {
        private readonly List<ImportPreflightIssue> _issues = [];
        private readonly Dictionary<(string, ImportRowIssueSeverity), long> _counts = [];
        private long _errors;
        private long _warnings;
        private long _dropped;

        public void Add(ImportPreflightIssue issue)
        {
            ArgumentNullException.ThrowIfNull(issue);
            if (issue.Severity == ImportRowIssueSeverity.Error)
            {
                _errors++;
            }
            else
            {
                _warnings++;
            }

            var key = (issue.Code, issue.Severity);
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            if (_issues.Count < maxStored)
            {
                _issues.Add(issue.Message.Length <= ImportRowIssue.MaxMessageLength ? issue : issue with { Message = Truncate(issue.Message) });
            }
            else
            {
                _dropped++;
            }
        }

        public ImportPreflightResult Result(long rows) => new()
        {
            RowsRead = rows,
            ErrorCount = _errors,
            WarningCount = _warnings,
            IssuesDropped = _dropped,
            Issues = [.. _issues.OrderBy(i => i.Row)],
            IssueCounts = [.. _counts
                .OrderBy(c => c.Key.Item2 == ImportRowIssueSeverity.Error ? 0 : 1).ThenByDescending(c => c.Value).ThenBy(c => c.Key.Item1, StringComparer.Ordinal)
                .Select(c => new ImportPreflightIssueCount(c.Key.Item1, c.Key.Item2, c.Value))],
        };
    }

    /// <summary>First row of each control number, by an 8-byte SHA-256 prefix of the normalized number (bounded memory).</summary>
    private sealed class KeyTracker(int max)
    {
        private readonly Dictionary<long, long> _rows = [];

        public bool Overflowed { get; private set; }

        /// <summary>The earlier row with this number, or null (and the number is remembered) for its first occurrence.</summary>
        public long? FirstRow(string norm, long row)
        {
            var hash = Hash(norm);
            if (_rows.TryGetValue(hash, out var first))
            {
                return first;
            }

            if (_rows.Count >= max)
            {
                Overflowed = true;
                return null;
            }

            _rows[hash] = row;
            return null;
        }

        public bool Contains(string norm) => _rows.ContainsKey(Hash(norm)) || Overflowed;

        private static long Hash(string norm) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(norm)), 0);
    }

    /// <summary>Stat's the files rows name (never reads or stores them), with the sampling rule of the options.</summary>
    private sealed class PathChecker(ImportPreflightOptions options, ImportVolumeOptions? volumes, ImportProfileDefinition profile, Collector sink)
    {
        private (int Index, string Column, string Kind)[] _columns = [];
        private ImportVolume? _volume;
        private bool _opened;
        private bool _unavailable;
        private long _linkingRows;
        private long _checkedRows;

        public void Bind(CompiledMapping mapping)
        {
            var bound = new List<(int, string, string)>();
            foreach (var (target, kind) in new[] { (StructuralTarget.NativePath, "native"), (StructuralTarget.TextPath, "text") })
            {
                if (target == StructuralTarget.TextPath && mapping.EffectiveProfile.Paths.TextInLoadFile)
                {
                    continue;
                }

                if (mapping.Columns.FirstOrDefault(c => c.Status == ColumnStatus.Mapped && c.Targets.Any(t => t.Usable && t.Structural == target)) is { } column)
                {
                    bound.Add((column.Index, column.Column, kind));
                }
            }

            _columns = [.. bound];
        }

        public void CheckRow(long row, string? controlNumber, IReadOnlyList<string> values)
        {
            var named = _columns.Where(c => c.Index < values.Count && !string.IsNullOrWhiteSpace(values[c.Index])).ToList();
            if (named.Count == 0 || !Take() || Volume() is not { } volume)
            {
                return;
            }

            foreach (var (index, column, kind) in named)
            {
                var file = volume.Resolve(values[index], profile.Paths.StripPrefix);
                if (file.Status == VolumeFileStatus.Rejected)
                {
                    sink.Add(new ImportPreflightIssue(row, controlNumber, column, kind.ToUpperInvariant() + "_PATH_REJECTED", ImportRowIssueSeverity.Error,
                        Truncate($"The {kind} path is refused: {file.Reason}")));
                }
                else if (file.Status == VolumeFileStatus.Missing)
                {
                    sink.Add(new ImportPreflightIssue(row, controlNumber, column, kind.ToUpperInvariant() + "_MISSING",
                        profile.Paths.MissingFiles == MissingFilePolicy.Error ? ImportRowIssueSeverity.Error : ImportRowIssueSeverity.Warning,
                        Truncate($"The {kind} file is missing: {file.Reason}")));
                }
            }
        }

        public void CheckImage(long optRow, string imageKey, string path)
        {
            if (!Take() || Volume() is not { } volume)
            {
                return;
            }

            var file = volume.Resolve(path, profile.Paths.StripPrefix);
            if (file.Status != VolumeFileStatus.Found)
            {
                var rejected = file.Status == VolumeFileStatus.Rejected;
                sink.Add(new ImportPreflightIssue(optRow, imageKey, "Path", rejected ? "OPT_PATH_REJECTED" : "OPT_IMAGE_MISSING", ImportRowIssueSeverity.Warning,
                    Truncate(rejected ? $"The image path '{path}' was rejected: {file.Reason}" : $"The image file is missing: {file.Reason}")));
            }
        }

        public void Finish()
        {
            if (_checkedRows < _linkingRows && !_unavailable)
            {
                sink.Add(new ImportPreflightIssue(0, null, null, ImportPreflightCodes.PathsSampled, ImportRowIssueSeverity.Warning, string.Create(
                    CultureInfo.InvariantCulture,
                    $"File paths were checked for {_checkedRows:N0} of {_linkingRows:N0} rows: every row up to {options.FullPathCheckRows:N0}, then every {options.PathSampleInterval}th. The import checks all of them.")));
            }
        }

        /// <summary>Whether this row's files are checked (all of the first rows, then a sample).</summary>
        private bool Take()
        {
            _linkingRows++;
            if (_linkingRows > options.FullPathCheckRows && (_linkingRows - options.FullPathCheckRows) % options.PathSampleInterval != 0)
            {
                return false;
            }

            _checkedRows++;
            return true;
        }

        private ImportVolume? Volume()
        {
            if (_opened)
            {
                return _volume;
            }

            _opened = true;
            if (string.IsNullOrWhiteSpace(volumes?.VolumeShareRoot))
            {
                _unavailable = true;
                sink.Add(new ImportPreflightIssue(0, null, null, ImportPreflightCodes.PathsNotChecked, ImportRowIssueSeverity.Warning,
                    "The import share is not configured for the API (Import:VolumeShareRoot); file paths are checked when the import runs."));
                return null;
            }

            if (!ImportVolume.TryOpen(volumes, profile.Paths.VolumeRoot, out _volume, out var error))
            {
                _unavailable = true;
                sink.Add(new ImportPreflightIssue(0, null, null, ImportPreflightCodes.VolumeUnavailable, ImportRowIssueSeverity.Error,
                    Truncate($"{error} Natives, text and images are read from the volume root (paths.volumeRoot); the import would fail.")));
            }

            return _volume;
        }
    }
}
