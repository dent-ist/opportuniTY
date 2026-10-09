using System.Globalization;
using System.Reflection;

using Opportunity.Contracts.Api;
using Opportunity.Core.Pages;
using Opportunity.Production.Exports;
using Opportunity.Production.Productions;

namespace Opportunity.Production.Volumes;

/// <summary>Production volume worker settings (ADR-010 §6 initial values for productions).</summary>
public sealed class ProductionVolumeOptions
{
    /// <summary>Members per volume chunk (ADR-010 §6: 100 documents for productions).</summary>
    public int DocumentsPerChunk { get; init; } = 100;

    /// <summary>Produced pages per volume chunk (ADR-010 §6: at most 2,000 pages); a larger document gets a chunk of its own.</summary>
    public int PagesPerChunk { get; init; } = 2_000;

    /// <summary>Documents of one chunk imaged in parallel (each in its own render session).</summary>
    public int DocumentConcurrency { get; init; } = 2;

    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Directory for page work files and the finalization's temporary files; the system temp directory when null.</summary>
    public string? TempDirectory { get; init; }

    public string WorkerId { get; init; } = string.Create(
        CultureInfo.InvariantCulture, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");
}

/// <summary>
/// The layout of a production volume (E12-T05, ticket review): <c>&lt;VOL&gt;/IMAGES/IMG0001/&lt;page Bates&gt;.tif|jpg</c>,
/// <c>&lt;VOL&gt;/NATIVES/NATIVE0001/&lt;ProdBegBates&gt;.&lt;ext&gt;</c>, <c>&lt;VOL&gt;/TEXT/TEXT0001/&lt;ProdBegBates&gt;.txt</c> and
/// <c>&lt;VOL&gt;/DATA/&lt;VOL&gt;.dat|.opt</c>, with the manifest next to the volume. Image folders follow the page's
/// Bates offset from the start number (at page level exactly <see cref="ExportSettings.MaxFilesPerFolder"/> images a
/// folder; at document level a document's pages share its folder), native and text folders the member's production
/// sequence: every chunk places its files without knowing what the others wrote, and every run yields the same paths.
/// </summary>
public sealed class ProductionVolumeLayout(ExportSettings settings)
{
    /// <summary>What the burn-in verification (E12-T06) reads: every produced page and the boxes burned into it. Never delivered.</summary>
    public const string VerificationPath = "_verification/pages.csv";

    private readonly ExportSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public string Volume => _settings.VolumeName;

    public string DatPath => $"{Volume}/DATA/{Volume}.dat";

    public string OptPath => $"{Volume}/DATA/{Volume}.opt";

    /// <param name="offset">The page's Bates number minus the production's start number (0-based).</param>
    public string ImagePath(long offset, string pageLabel, PageImageFormat format) =>
        $"{Volume}/IMAGES/{Folder("IMG", offset)}/{ExportLayout.FileStem(pageLabel, offset + 1)}.{ExportLayout.ImageExtension(format)}";

    public string NativePath(long sequence, string prodBegBates, string extension) =>
        $"{Volume}/NATIVES/{Folder("NATIVE", sequence - 1)}/{ExportLayout.FileStem(prodBegBates, sequence)}.{extension}";

    public string TextPath(long sequence, string prodBegBates) =>
        $"{Volume}/TEXT/{Folder("TEXT", sequence - 1)}/{ExportLayout.FileStem(prodBegBates, sequence)}.txt";

    /// <summary>A volume path as the DAT and OPT reference it (relative, configured separator).</summary>
    public string LoadFilePath(string packagePath) =>
        _settings.PathSeparator == "/" ? packagePath : packagePath.Replace('/', '\\');

    private string Folder(string prefix, long index) =>
        prefix + ((index / _settings.MaxFilesPerFolder) + 1).ToString("D4", CultureInfo.InvariantCulture);
}

/// <summary>The load-file settings of a production volume, from its normalized specification (E12-T05).</summary>
public static class ProductionVolumeSettings
{
    /// <summary>Volume writer version (layout, placeholders, text rule, manifest); recorded in every volume manifest (Q-08).</summary>
    public static string WriterVersion { get; } = "volume-writer 1;" + (typeof(ProductionVolumeSettings).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown");

    /// <summary>A redacted document's text file (ADR-012 §5.4: never the original text; no OCR engine is configured).</summary>
    public const string RedactedText = "Text withheld: document contains redactions. Refer to images.";

    /// <summary>Generated pages (slip sheets, placeholders, technical issues) are US Letter at the specification's resolution.</summary>
    public const decimal GeneratedWidthInches = 8.5m;

    public const decimal GeneratedHeightInches = 11m;

    public static ExportSettings ExportSettingsOf(ProductionSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var loadFile = specification.LoadFile ?? throw new ArgumentException("The specification is not normalized.", nameof(specification));
        var volume = loadFile.Volume ?? new ExportVolumeRequest();
        var defaults = new ExportSettings();
        return new ExportSettings
        {
            Columns = [.. (loadFile.Fields ?? []).Select(f => f.FieldId is { } id
                ? new ExportColumn(ExportColumnKind.Field, id, f.Header ?? string.Empty)
                : new ExportColumn(Enum.Parse<ExportColumnKind>(f.Column!.Value.ToString()), null,
                    f.Header ?? ExportSettingsRules.DefaultHeaders[Enum.Parse<ExportColumnKind>(f.Column!.Value.ToString())]))],
            Delimiters = loadFile.Delimiters ?? defaults.Delimiters,
            Encoding = loadFile.Encoding ?? defaults.Encoding,
            TextEncoding = loadFile.TextEncoding ?? defaults.TextEncoding,
            IncludeNatives = specification.DefaultOutput == ProductionOutputResource.Native
                || (specification.FileTypeRules ?? []).Any(r => r.Output == ProductionOutputResource.Native),
            IncludeText = specification.IncludeText,
            IncludeImages = true,
            VolumePrefix = volume.Prefix ?? ProductionSpecificationRules.DefaultVolumePrefix(specification.Bates.Prefix),
            VolumeStart = volume.Start ?? defaults.VolumeStart,
            VolumePadding = volume.Padding ?? defaults.VolumePadding,
            MaxFilesPerFolder = volume.MaxFilesPerFolder ?? defaults.MaxFilesPerFolder,
            PathSeparator = loadFile.PathSeparator ?? defaults.PathSeparator,
        };
    }

    /// <summary>A generated page's centred text with <c>{bates}</c>, <c>{confidentiality}</c> and <c>{production}</c> filled in.</summary>
    public static string Fill(string template, string batesLabel, string? designation, string productionName) =>
        template.Replace("{bates}", batesLabel, StringComparison.Ordinal)
            .Replace("{confidentiality}", designation ?? string.Empty, StringComparison.Ordinal)
            .Replace("{production}", productionName, StringComparison.Ordinal)
            .Trim();
}
