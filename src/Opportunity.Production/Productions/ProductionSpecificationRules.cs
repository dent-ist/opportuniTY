using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Opportunity.Contracts.Api;
using Opportunity.Core.Fields;
using Opportunity.Core.Productions;
using Opportunity.Production.Exports;

namespace Opportunity.Production.Productions;

/// <summary>A normalized specification with its canonical JSON and SHA-256.</summary>
public sealed record NormalizedSpecification(ProductionSpecification Specification, string Json, byte[] Sha256)
{
    public BatesFormat Format => ProductionSpecificationRules.FormatOf(Specification);
}

/// <summary>
/// Validates and normalizes a production specification (E12-T02): every member is filled in with its default, so the
/// stored specification states every value the production uses, and it is serialized canonically (fixed member order,
/// no whitespace), so equal specifications have equal bytes and equal SHA-256. Load-file options are validated exactly
/// like an export's (<see cref="ExportSettingsRules"/>).
/// </summary>
public static partial class ProductionSpecificationRules
{
    public const int MaxRules = 100;
    public const int MaxExtensionsPerRule = 100;
    public const int MaxEndorsements = 6;
    public const int MaxTemplateLength = 200;
    public const int DefaultDpi = 300;

    /// <summary>Spreadsheets and audio/video are produced natively with a slip sheet unless a rule says otherwise (E12-T05).</summary>
    public static IReadOnlyList<ProductionFileTypeRule> DefaultRules { get; } =
    [
        new(["csv", "xls", "xlsb", "xlsm", "xlsx"], ProductionOutputResource.Native),
        new(["avi", "m4a", "mov", "mp3", "mp4", "wav", "wma", "wmv"], ProductionOutputResource.Native),
    ];

    public static IReadOnlyList<ProductionEndorsement> DefaultEndorsements { get; } =
    [
        new(EndorsementPositionResource.BottomLeft, "{confidentiality}"),
        new(EndorsementPositionResource.BottomRight, "{bates}"),
    ];

    private static readonly string[] Tokens = ["bates", "confidentiality", "production"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Serialize(ProductionSpecification specification) => JsonSerializer.Serialize(specification, Json);

    public static ProductionSpecification Deserialize(string json) =>
        JsonSerializer.Deserialize<ProductionSpecification>(json, Json) ?? throw new JsonException("Empty production specification.");

    public static byte[] Hash(string json) => SHA256.HashData(Encoding.UTF8.GetBytes(json));

    public static BatesFormat FormatOf(ProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var bates = specification.Bates;
        return new BatesFormat(bates.Prefix, bates.Padding, bates.Suffix ?? string.Empty,
            bates.Level == BatesLevelResource.Document ? BatesNumberingLevel.Document : BatesNumberingLevel.Page);
    }

    /// <summary>How a document with <paramref name="extension"/> is produced under the (normalized) specification.</summary>
    public static Func<string?, ProductionOutputKind> OutputFor(ProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var byExtension = new Dictionary<string, ProductionOutputKind>(StringComparer.Ordinal);
        foreach (var rule in specification.FileTypeRules ?? [])
        {
            foreach (var extension in rule.Extensions)
            {
                byExtension[extension] = Kind(rule.Output);
            }
        }

        var fallback = Kind(specification.DefaultOutput ?? ProductionOutputResource.Image);
        return extension => NormalizeExtension(extension) is { } e && byExtension.TryGetValue(e, out var kind) ? kind : fallback;
    }

    public static ProductionOutputKind Kind(ProductionOutputResource output) => output switch
    {
        ProductionOutputResource.Native => ProductionOutputKind.Native,
        ProductionOutputResource.Placeholder => ProductionOutputKind.Placeholder,
        _ => ProductionOutputKind.Image,
    };

    public static ProductionOutputResource Resource(ProductionOutputKind output) => output switch
    {
        ProductionOutputKind.Native => ProductionOutputResource.Native,
        ProductionOutputKind.Placeholder => ProductionOutputResource.Placeholder,
        _ => ProductionOutputResource.Image,
    };

    /// <summary>The normalized specification, or the validation errors keyed by request member (<c>specification.…</c>).</summary>
    public static NormalizedSpecification? Normalize(
        ProductionSpecification? input, FieldCatalog catalog, IReadOnlySet<int> restricted, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(restricted);
        var problems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string message)
        {
            key = "specification" + (key.Length > 0 ? "." + key : string.Empty);
            if (!problems.TryGetValue(key, out var list))
            {
                problems[key] = list = [];
            }

            list.Add(message);
        }

        if (input?.Bates is null)
        {
            Add("bates", "Give the Bates numbering (at least a prefix).");
            errors = Collect(problems);
            return null;
        }

        // Bates numbering.
        var bates = input.Bates;
        var prefix = bates.Prefix?.Trim() ?? string.Empty;
        var suffix = bates.Suffix?.Trim() ?? string.Empty;
        if (BatesFormat.Validate(prefix, bates.Padding, suffix) is { } formatError)
        {
            Add("bates", formatError);
        }
        else if (bates.StartNumber < 1 || bates.StartNumber > BatesFormat.MaxFor(bates.Padding))
        {
            Add("bates.startNumber", string.Create(CultureInfo.InvariantCulture, $"The start number is 1 to {BatesFormat.MaxFor(bates.Padding)} for {bates.Padding} digits."));
        }

        if (!Enum.IsDefined(bates.Level))
        {
            Add("bates.level", "Use page or document.");
        }

        // Images.
        var images = input.Images ?? new ProductionImageSettings();
        var format = images.Format ?? ProductionImageFormatResource.TiffG4;
        var colorFormat = images.ColorFormat ?? ProductionImageFormatResource.Jpeg;
        var dpi = images.Dpi ?? DefaultDpi;
        if (!Enum.IsDefined(format) || !Enum.IsDefined(colorFormat))
        {
            Add("images", "Use tiffG4 or jpeg.");
        }

        if (dpi is < 72 or > 600)
        {
            Add("images.dpi", "Resolution is 72 to 600 DPI.");
        }

        // Output by file type.
        var defaultOutput = input.DefaultOutput ?? ProductionOutputResource.Image;
        if (!Enum.IsDefined(defaultOutput))
        {
            Add("defaultOutput", "Use image, native or placeholder.");
        }

        var rules = new List<ProductionFileTypeRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inputRules = input.FileTypeRules ?? DefaultRules;
        if (inputRules.Count > MaxRules)
        {
            Add("fileTypeRules", string.Create(CultureInfo.InvariantCulture, $"At most {MaxRules} rules."));
        }

        for (var i = 0; i < inputRules.Count && inputRules.Count <= MaxRules; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"fileTypeRules[{i}]");
            var rule = inputRules[i];
            if (rule?.Extensions is null || rule.Extensions.Count is 0 or > MaxExtensionsPerRule || !Enum.IsDefined(rule.Output))
            {
                Add(key, string.Create(CultureInfo.InvariantCulture, $"A rule lists 1 to {MaxExtensionsPerRule} extensions and an output (image, native or placeholder)."));
                continue;
            }

            var extensions = new List<string>();
            foreach (var raw in rule.Extensions)
            {
                if (NormalizeExtension(raw) is not { } extension || !ExtensionPattern().IsMatch(extension))
                {
                    Add(key, $"'{raw}' is not a file extension (letters, digits, '_', '+', '-'; up to 20).");
                }
                else if (!seen.Add(extension))
                {
                    Add(key, $"The extension '{extension}' is named by more than one rule.");
                }
                else
                {
                    extensions.Add(extension);
                }
            }

            extensions.Sort(StringComparer.Ordinal);
            rules.Add(new ProductionFileTypeRule(extensions, rule.Output));
        }

        // Load file (as for exports) plus date format and time zone.
        var loadFile = input.LoadFile ?? new ProductionLoadFileSettings();
        var includeNatives = defaultOutput == ProductionOutputResource.Native || rules.Any(r => r.Output == ProductionOutputResource.Native);
        var exportRequest = new CreateExportRequest(
            Guid.Empty,
            loadFile.Fields is { Count: > 0 } fields ? fields : [new ExportFieldRequest(FieldId: SystemFields.ControlNumber)],
            null,
            loadFile.Delimiters,
            loadFile.Encoding,
            includeNatives,
            input.IncludeText,
            IncludeImages: true,
            loadFile.Volume,
            loadFile.PathSeparator,
            loadFile.TextEncoding);
        var settings = ExportSettingsRules.Normalize(exportRequest, catalog, restricted, out var loadFileErrors);
        foreach (var (key, messages) in loadFileErrors)
        {
            foreach (var message in messages)
            {
                Add("loadFile." + key, message);
            }
        }

        var dateFormat = string.IsNullOrWhiteSpace(loadFile.DateFormat) ? "yyyy-MM-dd" : loadFile.DateFormat.Trim();
        if (dateFormat.Length > 50 || !ValidDateFormat(dateFormat))
        {
            Add("loadFile.dateFormat", "Use a date pattern such as yyyy-MM-dd or MM/dd/yyyy (at most 50 characters).");
        }

        var timeZone = string.IsNullOrWhiteSpace(loadFile.TimeZone) ? "UTC" : loadFile.TimeZone.Trim();
        if (timeZone.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            Add("loadFile.timeZone", "Use an IANA time zone such as UTC, America/New_York or Europe/London.");
        }

        // Endorsements.
        var endorsements = input.Endorsements ?? new ProductionEndorsementSettings();
        var items = endorsements.Items ?? DefaultEndorsements;
        var fontSize = endorsements.FontSize ?? 10;
        if (fontSize is < 6 or > 24)
        {
            Add("endorsements.fontSize", "Font size is 6 to 24 points.");
        }

        if (items.Count > MaxEndorsements)
        {
            Add("endorsements.items", string.Create(CultureInfo.InvariantCulture, $"At most {MaxEndorsements} endorsements, one per position."));
        }

        var stamps = new List<ProductionEndorsement>();
        for (var i = 0; i < items.Count && items.Count <= MaxEndorsements; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"endorsements.items[{i}]");
            var item = items[i];
            var template = item?.Template?.Trim() ?? string.Empty;
            if (item is null || !Enum.IsDefined(item.Position))
            {
                Add(key, "Give a position (topLeft … bottomRight).");
            }
            else if (template.Length is 0 or > MaxTemplateLength || template.Any(char.IsControl))
            {
                Add(key, string.Create(CultureInfo.InvariantCulture, $"A template has 1 to {MaxTemplateLength} characters and no control characters."));
            }
            else if (TokenPattern().Matches(template).Select(m => m.Groups[1].Value).FirstOrDefault(t => !Tokens.Contains(t)) is { } unknown)
            {
                Add(key, $"Unknown placeholder {{{unknown}}}; use {{bates}}, {{confidentiality}} or {{production}}.");
            }
            else if (stamps.Any(s => s.Position == item.Position))
            {
                Add(key, "One endorsement per position.");
            }
            else
            {
                stamps.Add(new ProductionEndorsement(item.Position, template));
            }
        }

        if (problems.Count > 0 || settings is null)
        {
            errors = Collect(problems);
            return null;
        }

        errors = [];
        var normalized = new ProductionSpecification(
            new ProductionBatesSettings(prefix, bates.StartNumber, bates.Padding, suffix, bates.Level),
            new ProductionImageSettings(format, colorFormat, dpi),
            defaultOutput,
            rules,
            input.IncludeText,
            new ProductionLoadFileSettings(
                [.. settings.Columns.Select(c => c.Kind == ExportColumnKind.Field
                    ? new ExportFieldRequest(FieldId: c.FieldId, Header: c.Header)
                    : new ExportFieldRequest(Column: Enum.Parse<ExportColumnResource>(c.Kind.ToString()), Header: c.Header))],
                settings.Delimiters,
                settings.Encoding,
                settings.TextEncoding,
                settings.PathSeparator,
                new ExportVolumeRequest(settings.VolumePrefix, settings.VolumeStart, settings.VolumePadding, settings.MaxFilesPerFolder),
                dateFormat,
                timeZone),
            new ProductionEndorsementSettings([.. stamps.OrderBy(s => s.Position)], fontSize, endorsements.ExpandCanvas ?? true));
        var json = Serialize(normalized);
        return new NormalizedSpecification(normalized, json, Hash(json));
    }

    public static string? NormalizeExtension(string? extension) =>
        extension?.Trim().TrimStart('.').ToLowerInvariant() is { Length: > 0 } e ? e : null;

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

    private static Dictionary<string, string[]> Collect(Dictionary<string, List<string>> problems) =>
        problems.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9_+-]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
