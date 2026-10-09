using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Import.LoadFiles;

namespace Opportunity.Production.Exports;

/// <summary>What a DAT column holds.</summary>
public enum ExportColumnKind
{
    /// <summary>A field's value (system, imported or coding field).</summary>
    Field,

    /// <summary>The control number of the family's top-level parent (a group identifier).</summary>
    FamilyId,

    /// <summary>The control number of the immediate parent.</summary>
    ParentId,

    NativePath,
    TextPath,

    /// <summary>Productions only (E12-T05): the member's ProdBegBates.</summary>
    ProdBegBates,

    ProdEndBates,
    ProdBegAttach,
    ProdEndAttach,

    /// <summary>Productions only: the frozen designation legend stamped on the member's pages.</summary>
    Confidentiality,

    /// <summary>Productions only: <c>Yes</c> when redactions were burned into the member's images.</summary>
    Redacted,

    /// <summary>Productions only: the member's produced image count.</summary>
    ProducedPages,
}

/// <summary>One DAT column, in output order.</summary>
public sealed record ExportColumn(ExportColumnKind Kind, int? FieldId, string Header);

/// <summary>
/// The frozen settings of an export (stored as JSON with the export): DAT columns in order, the delimiter preset and
/// encodings, the files to include and the volume layout (ticket review E12-T01: <c>VOL001</c>, subfolders of at most
/// <see cref="MaxFilesPerFolder"/> documents, <c>\</c> path separator and CRLF by default).
/// </summary>
public sealed record ExportSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<ExportColumn> Columns { get; init; } = [];

    /// <summary>Delimiter preset id (<see cref="DelimiterProfile.Presets"/>).</summary>
    public string Delimiters { get; init; } = DelimiterProfile.Concordance.Name;

    public string Encoding { get; init; } = "utf-8";

    public string TextEncoding { get; init; } = "utf-8";

    public bool IncludeNatives { get; init; } = true;

    public bool IncludeText { get; init; } = true;

    public bool IncludeImages { get; init; } = true;

    public string VolumePrefix { get; init; } = "VOL";

    public int VolumeStart { get; init; } = 1;

    public int VolumePadding { get; init; } = 3;

    public int MaxFilesPerFolder { get; init; } = 1_000;

    public string PathSeparator { get; init; } = "\\";

    [JsonIgnore]
    public string VolumeName => VolumePrefix + VolumeStart.ToString("D" + VolumePadding.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    [JsonIgnore]
    public DelimiterProfile Profile => DelimiterProfile.TryGetPreset(Delimiters, out var profile)
        ? profile
        : throw new InvalidOperationException($"Unknown delimiter preset '{Delimiters}'.");

    [JsonIgnore]
    public LoadFileEncodingKind DatEncoding => ExportSettingsRules.ParseDatEncoding(Encoding)
        ?? throw new InvalidOperationException($"Unsupported DAT encoding '{Encoding}'.");

    [JsonIgnore]
    public LoadFileEncodingKind TextFileEncoding => ExportSettingsRules.ParseTextEncoding(TextEncoding)
        ?? throw new InvalidOperationException($"Unsupported text encoding '{TextEncoding}'.");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Canonical JSON (fixed property order): equal settings serialize to equal bytes.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static ExportSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<ExportSettings>(json, Json) ?? throw new JsonException("Empty export settings.");

    public ExportSettingsResource ToResource() => new(
        [.. Columns.Select(c => new ExportColumnSettingResource(
            c.FieldId,
            c.Kind == ExportColumnKind.Field ? null : Enum.Parse<ExportColumnResource>(c.Kind.ToString()),
            c.Header))],
        Delimiters,
        Encoding,
        TextEncoding,
        IncludeNatives,
        IncludeText,
        IncludeImages,
        VolumeName,
        MaxFilesPerFolder,
        PathSeparator);
}

/// <summary>Validates a <see cref="CreateExportRequest"/> against the field catalogue into <see cref="ExportSettings"/>.</summary>
public static partial class ExportSettingsRules
{
    public const int MaxColumns = 500;
    public const int MaxHeaderLength = 200;
    public const int MaxNameLength = 200;

    public static readonly IReadOnlyDictionary<ExportColumnKind, string> DefaultHeaders = new Dictionary<ExportColumnKind, string>
    {
        [ExportColumnKind.FamilyId] = "FamilyID",
        [ExportColumnKind.ParentId] = "ParentID",
        [ExportColumnKind.NativePath] = "NativePath",
        [ExportColumnKind.TextPath] = "TextPath",
        [ExportColumnKind.ProdBegBates] = "ProdBegBates",
        [ExportColumnKind.ProdEndBates] = "ProdEndBates",
        [ExportColumnKind.ProdBegAttach] = "ProdBegAttach",
        [ExportColumnKind.ProdEndAttach] = "ProdEndAttach",
        [ExportColumnKind.Confidentiality] = "Confidentiality",
        [ExportColumnKind.Redacted] = "Redacted",
        [ExportColumnKind.ProducedPages] = "PageCount",
    };

    /// <summary>Columns only a production volume fills (E12-T05).</summary>
    public static bool IsProductionColumn(ExportColumnKind kind) => kind >= ExportColumnKind.ProdBegBates;

    /// <summary>
    /// The settings of <paramref name="request"/>, or the validation errors by request member. Fields must exist, be
    /// live and visible to the caller (<paramref name="restricted"/> lists the ones they may not see).
    /// </summary>
    public static ExportSettings? Normalize(
        CreateExportRequest request, FieldCatalog catalog, IReadOnlySet<int> restricted, out Dictionary<string, string[]> errors) =>
        Normalize(request, catalog, restricted, production: false, out errors);

    /// <param name="production">A production's load file (E12-T05): the production columns (Bates, designation, …) are allowed.</param>
    public static ExportSettings? Normalize(
        CreateExportRequest request, FieldCatalog catalog, IReadOnlySet<int> restricted, bool production, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(restricted);
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string message)
        {
            if (!problems.TryGetValue(key, out var list))
            {
                problems[key] = list = [];
            }

            list.Add(message);
        }

        var delimiters = string.IsNullOrWhiteSpace(request.Delimiters) ? DelimiterProfile.Concordance.Name : request.Delimiters.Trim().ToLowerInvariant();
        if (!DelimiterProfile.TryGetPreset(delimiters, out var profile))
        {
            Add("delimiters", $"Use one of {string.Join(", ", DelimiterProfile.Presets.Select(p => p.Name))}.");
        }

        var encoding = string.IsNullOrWhiteSpace(request.Encoding) ? "utf-8" : request.Encoding.Trim().ToLowerInvariant();
        if (ParseDatEncoding(encoding) is not { } datEncoding)
        {
            Add("encoding", "Use utf-8, utf-16le or windows-1252.");
        }
        else if (profile is not null)
        {
            foreach (var problem in profile.ValidateFor(datEncoding))
            {
                Add("encoding", problem);
            }
        }

        var textEncoding = string.IsNullOrWhiteSpace(request.TextEncoding) ? "utf-8" : request.TextEncoding.Trim().ToLowerInvariant();
        if (ParseTextEncoding(textEncoding) is null)
        {
            Add("textEncoding", "Use utf-8 or utf-16le.");
        }

        var separator = string.IsNullOrEmpty(request.PathSeparator) ? "\\" : request.PathSeparator;
        if (separator is not ("\\" or "/"))
        {
            Add("pathSeparator", @"Use \ or /.");
        }

        var volume = request.Volume ?? new ExportVolumeRequest();
        var prefix = volume.Prefix?.Trim() is { Length: > 0 } p ? p : "VOL";
        if (!VolumePrefixPattern().IsMatch(prefix))
        {
            Add("volume.prefix", "Use 1 to 20 letters, digits, '_' or '-'.");
        }

        var start = volume.Start ?? 1;
        if (start is < 1 or > 999_999)
        {
            Add("volume.start", "Must be between 1 and 999999.");
        }

        var padding = volume.Padding ?? 3;
        if (padding is < 1 or > 6)
        {
            Add("volume.padding", "Must be between 1 and 6.");
        }

        var maxFiles = volume.MaxFilesPerFolder ?? 1_000;
        if (maxFiles is < 1 or > 100_000)
        {
            Add("volume.maxFilesPerFolder", "Must be between 1 and 100000.");
        }

        var columns = new List<ExportColumn>();
        var fields = request.Fields ?? [];
        if (fields.Count == 0)
        {
            Add("fields", "List at least one DAT column.");
        }
        else if (fields.Count > MaxColumns)
        {
            Add("fields", $"At most {MaxColumns} columns.");
        }

        for (var i = 0; i < fields.Count && fields.Count <= MaxColumns; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"fields[{i}]");
            var item = fields[i];
            if (item is null || (item.FieldId is null) == (item.Column is null))
            {
                Add(key, "Give exactly one of fieldId and column.");
                continue;
            }

            string defaultHeader;
            ExportColumn column;
            if (item.FieldId is { } fieldId)
            {
                var field = catalog.Find(fieldId);
                if (field is null || field.IsDeleted || restricted.Contains(fieldId))
                {
                    Add(key, string.Create(CultureInfo.InvariantCulture, $"Field {fieldId} does not exist."));
                    continue;
                }

                defaultHeader = field.Name;
                column = new ExportColumn(ExportColumnKind.Field, fieldId, defaultHeader);
            }
            else
            {
                if (!Enum.IsDefined(item.Column!.Value))
                {
                    Add(key, "Unknown column.");
                    continue;
                }

                var kind = Enum.Parse<ExportColumnKind>(item.Column!.Value.ToString());
                if (IsProductionColumn(kind) && !production)
                {
                    Add(key, "Only a production's load file has this column.");
                    continue;
                }

                if ((kind == ExportColumnKind.NativePath && !request.IncludeNatives) || (kind == ExportColumnKind.TextPath && !request.IncludeText))
                {
                    Add(key, "A path column needs those files included.");
                    continue;
                }

                defaultHeader = DefaultHeaders[kind];
                column = new ExportColumn(kind, null, defaultHeader);
            }

            var header = item.Header?.Trim() is { Length: > 0 } h ? h : defaultHeader;
            if (header.Length > MaxHeaderLength || header.Any(char.IsControl))
            {
                Add(key, $"A header has 1 to {MaxHeaderLength} characters and no control characters.");
                continue;
            }

            columns.Add(column with { Header = header });
        }

        if (request.IncludeNatives && columns.All(c => c.Kind != ExportColumnKind.NativePath))
        {
            columns.Add(new ExportColumn(ExportColumnKind.NativePath, null, DefaultHeaders[ExportColumnKind.NativePath]));
        }

        if (request.IncludeText && columns.All(c => c.Kind != ExportColumnKind.TextPath))
        {
            columns.Add(new ExportColumn(ExportColumnKind.TextPath, null, DefaultHeaders[ExportColumnKind.TextPath]));
        }

        foreach (var duplicate in columns.GroupBy(c => c.Header, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            Add("fields", $"The header '{duplicate.Key}' is used more than once.");
        }

        if (problems.Count > 0)
        {
            errors = problems.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
            return null;
        }

        errors = [];
        return new ExportSettings
        {
            Columns = columns,
            Delimiters = profile!.Name,
            Encoding = encoding,
            TextEncoding = textEncoding,
            IncludeNatives = request.IncludeNatives,
            IncludeText = request.IncludeText,
            IncludeImages = request.IncludeImages,
            VolumePrefix = prefix,
            VolumeStart = start,
            VolumePadding = padding,
            MaxFilesPerFolder = maxFiles,
            PathSeparator = separator,
        };
    }

    public static LoadFileEncodingKind? ParseDatEncoding(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "utf-8" => LoadFileEncodingKind.Utf8,
        "utf-16le" => LoadFileEncodingKind.Utf16LE,
        "windows-1252" => LoadFileEncodingKind.Windows1252,
        _ => null,
    };

    public static LoadFileEncodingKind? ParseTextEncoding(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "utf-8" => LoadFileEncodingKind.Utf8,
        "utf-16le" => LoadFileEncodingKind.Utf16LE,
        _ => null,
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumePrefixPattern();
}
