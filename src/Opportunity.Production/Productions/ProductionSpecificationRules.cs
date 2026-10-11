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
    public const int DefaultMargin = 18;
    public const int MaxLegendLength = 100;

    /// <summary>The choice whose default legend is empty (nothing is stamped), compared without case.</summary>
    public const string NoDesignationChoiceName = "None";

    /// <summary>Spreadsheets and audio/video are produced natively with a slip sheet unless a rule says otherwise (E12-T05).</summary>
    public static IReadOnlyList<ProductionFileTypeRule> DefaultRules { get; } =
    [
        new(["csv", "xls", "xlsb", "xlsm", "xlsx"], ProductionOutputResource.Native),
        new(["avi", "m4a", "mov", "mp3", "mp4", "wav", "wma", "wmv"], ProductionOutputResource.Native),
    ];

    /// <summary>Pages of these file types are produced in the colour format (Q-21: by file type, not by detection).</summary>
    public static IReadOnlyList<string> DefaultColorFileTypes { get; } = ["bmp", "gif", "heic", "jpeg", "jpg", "png", "ppt", "pptx"];

    public const string DefaultWithheldText = "Withheld – Privileged";
    public const string DefaultTechnicalIssueText = "Technical Issue";
    public const string DefaultNativeSlipSheetText = "Document Produced in Native Format";
    public const int MaxPlaceholderLength = 200;

    /// <summary>
    /// The default DAT columns of a production (E12-T05, Q-19): header and source in order. Metadata fields that are not
    /// system fields (Custodian, From, To, CC, BCC, Subject) are found by name and left out when the workspace has none.
    /// </summary>
    private static readonly (string Header, ExportColumnKind? Column, int? FieldId, string? FieldName)[] DefaultLoadFileColumns =
    [
        ("ProdBegBates", ExportColumnKind.ProdBegBates, null, null),
        ("ProdEndBates", ExportColumnKind.ProdEndBates, null, null),
        ("ProdBegAttach", ExportColumnKind.ProdBegAttach, null, null),
        ("ProdEndAttach", ExportColumnKind.ProdEndAttach, null, null),
        ("Custodian", null, null, "Custodian"),
        ("AllCustodians", null, SystemFields.AllCustodians, null),
        ("FileName", null, SystemFields.FileName, null),
        ("FileExtension", null, SystemFields.FileExtension, null),
        ("DateSent", null, SystemFields.DateSent, null),
        ("DateCreated", null, SystemFields.DateCreated, null),
        ("DateLastModified", null, SystemFields.DateLastModified, null),
        ("From", null, null, "From"),
        ("To", null, null, "To"),
        ("CC", null, null, "CC"),
        ("BCC", null, null, "BCC"),
        ("Subject", null, null, "Subject"),
        ("MD5Hash", null, SystemFields.Md5, null),
        ("Confidentiality", ExportColumnKind.Confidentiality, null, null),
        ("Redacted", ExportColumnKind.Redacted, null, null),
        ("PageCount", ExportColumnKind.ProducedPages, null, null),
        ("NativeLink", ExportColumnKind.NativePath, null, null),
        ("TextLink", ExportColumnKind.TextPath, null, null),
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

        var colorTypes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in images.ColorFileTypes ?? DefaultColorFileTypes)
        {
            if (NormalizeExtension(raw) is not { } extension || !ExtensionPattern().IsMatch(extension))
            {
                Add("images.colorFileTypes", $"'{raw}' is not a file extension (letters, digits, '_', '+', '-'; up to 20).");
            }
            else
            {
                colorTypes.Add(extension);
            }
        }

        if (colorTypes.Count > MaxExtensionsPerRule)
        {
            Add("images.colorFileTypes", string.Create(CultureInfo.InvariantCulture, $"At most {MaxExtensionsPerRule} colour file types."));
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
        // E12-T05: the production volume defaults to <Bates prefix>_VOL001 (ticket review), named like an export's otherwise.
        var volume = loadFile.Volume ?? new ExportVolumeRequest();
        if (string.IsNullOrWhiteSpace(volume.Prefix))
        {
            volume = volume with { Prefix = DefaultVolumePrefix(prefix) };
        }

        var exportRequest = new CreateExportRequest(
            Guid.Empty,
            loadFile.Fields is { Count: > 0 } fields ? fields : DefaultLoadFileFields(catalog, restricted, includeNatives, input.IncludeText),
            null,
            loadFile.Delimiters,
            loadFile.Encoding,
            includeNatives,
            input.IncludeText,
            IncludeImages: true,
            volume,
            loadFile.PathSeparator,
            loadFile.TextEncoding);
        var settings = ExportSettingsRules.Normalize(exportRequest, catalog, restricted, production: true, out var loadFileErrors);
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

        var margin = endorsements.Margin ?? DefaultMargin;
        if (margin is < 0 or > 72)
        {
            Add("endorsements.margin", "The margin is 0 to 72 points.");
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

        var designations = NormalizeDesignations(input.Designations, catalog, restricted, Add);

        // Placeholder, technical-issue and slip-sheet texts (E12-T05).
        var placeholders = input.Placeholders ?? new ProductionPlaceholderSettings();
        string Placeholder(string key, string? text, string fallback)
        {
            var value = text is null ? fallback : text.Trim();
            if (value.Length is 0 or > MaxPlaceholderLength || value.Any(char.IsControl))
            {
                Add("placeholders." + key, string.Create(CultureInfo.InvariantCulture, $"A placeholder text has 1 to {MaxPlaceholderLength} characters and no control characters."));
            }
            else if (TokenPattern().Matches(value).Select(m => m.Groups[1].Value).FirstOrDefault(t => !Tokens.Contains(t)) is { } unknown)
            {
                Add("placeholders." + key, $"Unknown placeholder {{{unknown}}}; use {{bates}}, {{confidentiality}} or {{production}}.");
            }

            return value;
        }

        var withheldDocuments = input.WithheldDocuments ?? WithheldDocumentsResource.Block;
        if (!Enum.IsDefined(withheldDocuments))
        {
            Add("withheldDocuments", "Use block or placeholder.");
        }

        var withheld = Placeholder("withheld", placeholders.Withheld, DefaultWithheldText);
        var technical = Placeholder("technicalIssue", placeholders.TechnicalIssue, DefaultTechnicalIssueText);
        var slipSheet = Placeholder("nativeSlipSheet", placeholders.NativeSlipSheet, DefaultNativeSlipSheetText);

        if (problems.Count > 0 || settings is null || designations is null)
        {
            errors = Collect(problems);
            return null;
        }

        errors = [];
        var normalized = new ProductionSpecification(
            new ProductionBatesSettings(prefix, bates.StartNumber, bates.Padding, suffix, bates.Level),
            new ProductionImageSettings(format, colorFormat, dpi, [.. colorTypes]),
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
            new ProductionEndorsementSettings([.. stamps.OrderBy(s => s.Position)], fontSize, endorsements.ExpandCanvas ?? true, margin),
            designations,
            new ProductionPlaceholderSettings(withheld, technical, slipSheet),
            input.RedactionSetId,
            withheldDocuments);
        var json = Serialize(normalized);
        return new NormalizedSpecification(normalized, json, Hash(json));
    }

    /// <summary>The default volume prefix of a Bates prefix: <c>ABC</c> → <c>ABC_VOL</c> (letters, digits, '_' and '-', at most 20).</summary>
    public static string DefaultVolumePrefix(string batesPrefix)
    {
        var safe = new string([.. (batesPrefix ?? string.Empty).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
        return (safe.Length > 16 ? safe[..16] : safe) + "_VOL";
    }

    /// <summary>The default production DAT columns that exist in the workspace and the caller may see (E12-T05).</summary>
    public static IReadOnlyList<ExportFieldRequest> DefaultLoadFileFields(FieldCatalog catalog, IReadOnlySet<int> restricted, bool includeNatives, bool includeText)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(restricted);
        var fields = new List<ExportFieldRequest>();
        foreach (var (header, column, fieldId, fieldName) in DefaultLoadFileColumns)
        {
            if (column is { } kind)
            {
                if ((kind == ExportColumnKind.NativePath && !includeNatives) || (kind == ExportColumnKind.TextPath && !includeText))
                {
                    continue;
                }

                fields.Add(new ExportFieldRequest(Column: Enum.Parse<ExportColumnResource>(kind.ToString()), Header: header));
                continue;
            }

            var field = fieldId is { } id
                ? catalog.Find(id)
                : catalog.Fields.Where(f => !f.IsDeleted && !f.IsSystem
                        && string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.FieldId).FirstOrDefault();
            if (field is { IsDeleted: false } && !restricted.Contains(field.FieldId))
            {
                fields.Add(new ExportFieldRequest(FieldId: field.FieldId, Header: header));
            }
        }

        return fields;
    }

    /// <summary>The designation levels of a normalized specification, lowest first.</summary>
    public static IReadOnlyList<DesignationLevel> LevelsOf(ProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return [.. (specification.Designations?.Levels ?? []).Select((l, i) => new DesignationLevel(l.ChoiceId, i, l.Legend ?? string.Empty))];
    }

    public static DesignationFamilyRule RuleOf(ProductionSpecification specification) =>
        specification?.Designations?.FamilyRule == DesignationFamilyRuleResource.Document ? DesignationFamilyRule.Document : DesignationFamilyRule.HighestInFamily;

    /// <summary>True when an endorsement of the specification stamps the designation (<c>{confidentiality}</c>).</summary>
    public static bool StampsDesignation(ProductionSpecification specification) =>
        (specification?.Endorsements?.Items ?? []).Any(e => e.Template.Contains("{confidentiality}", StringComparison.Ordinal));

    /// <summary>The default legend of a choice: its name, except "None" (nothing is stamped).</summary>
    public static string DefaultLegend(string choiceName) =>
        string.Equals(choiceName?.Trim(), NoDesignationChoiceName, StringComparison.OrdinalIgnoreCase) ? string.Empty : choiceName?.Trim() ?? string.Empty;

    /// <summary>The workspace's designation fields: single-choice, security-affecting confidentiality coding fields.</summary>
    public static IEnumerable<FieldDefinition> DesignationFields(FieldCatalog catalog, IReadOnlySet<int> restricted)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(restricted);
        return catalog.Fields
            .Where(f => !f.IsDeleted && f.Type == FieldType.SingleChoice && f.SecurityClass == SecurityClass.ConfidentialityDesignation
                && f.Storage == FieldStorage.Coding && !restricted.Contains(f.FieldId))
            .OrderBy(f => f.FieldId);
    }

    private static ProductionDesignationSettings? NormalizeDesignations(
        ProductionDesignationSettings? input, FieldCatalog catalog, IReadOnlySet<int> restricted, Action<string, string> add)
    {
        input ??= new ProductionDesignationSettings();
        var rule = input.FamilyRule ?? DesignationFamilyRuleResource.HighestInFamily;
        if (!Enum.IsDefined(rule))
        {
            add("designations.familyRule", "Use highestInFamily or document.");
            return null;
        }

        var candidates = DesignationFields(catalog, restricted).ToList();
        FieldDefinition? field;
        if (input.FieldId is { } fieldId)
        {
            field = candidates.FirstOrDefault(f => f.FieldId == fieldId);
            if (field is null)
            {
                add("designations.fieldId", "Use a single-choice confidentiality designation field.");
                return null;
            }
        }
        else if (candidates.Count > 1)
        {
            add("designations.fieldId", "The workspace has several confidentiality designation fields; give the one to produce.");
            return null;
        }
        else
        {
            field = candidates.SingleOrDefault();
        }

        if (field is null)
        {
            if (input.Levels is { Count: > 0 })
            {
                add("designations.levels", "Levels need a designation field.");
                return null;
            }

            return new ProductionDesignationSettings(null, rule, []);
        }

        var choices = catalog.ChoicesOf(field.FieldId);
        if (input.Levels is null)
        {
            return new ProductionDesignationSettings(field.FieldId, rule, [.. choices.Select(c => new ProductionDesignationLevel(c.ChoiceId, DefaultLegend(c.Name)))]);
        }

        var byId = choices.ToDictionary(c => c.ChoiceId);
        var levels = new List<ProductionDesignationLevel>();
        var seen = new HashSet<int>();
        var valid = true;
        for (var i = 0; i < input.Levels.Count; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"designations.levels[{i}]");
            var level = input.Levels[i];
            if (level is null || !byId.TryGetValue(level.ChoiceId, out var choice))
            {
                add(key, "Use a choice of the designation field.");
                valid = false;
                continue;
            }

            if (!seen.Add(choice.ChoiceId))
            {
                add(key, $"The choice '{choice.Name}' is listed twice.");
                valid = false;
                continue;
            }

            var legend = level.Legend is null ? DefaultLegend(choice.Name) : level.Legend.Trim();
            if (legend.Length > MaxLegendLength || legend.Any(char.IsControl))
            {
                add(key, string.Create(CultureInfo.InvariantCulture, $"A legend has at most {MaxLegendLength} characters and no control characters."));
                valid = false;
                continue;
            }

            levels.Add(new ProductionDesignationLevel(choice.ChoiceId, legend));
        }

        if (valid && choices.Where(c => !seen.Contains(c.ChoiceId)).Select(c => c.Name).ToList() is { Count: > 0 } missing)
        {
            add("designations.levels", "List every choice of the designation field, lowest first; missing: " + string.Join(", ", missing) + ".");
            valid = false;
        }

        return valid ? new ProductionDesignationSettings(field.FieldId, rule, levels) : null;
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
