namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>Byte encoding of a load file or extracted-text file.</summary>
public enum LoadFileEncoding
{
    /// <summary>UTF-8 without a byte-order mark.</summary>
    Utf8,

    /// <summary>UTF-8 with the <c>EF BB BF</c> byte-order mark.</summary>
    Utf8Bom,

    /// <summary>UTF-16 little-endian with the <c>FF FE</c> byte-order mark (Relativity/Nuix style).</summary>
    Utf16Le,

    /// <summary>Windows-1252 ("ANSI"); characters outside the code page are written as <c>?</c>.</summary>
    Windows1252,
}

/// <summary>How DateTime and Date values are rendered in the DAT.</summary>
public enum DatDateFormat
{
    /// <summary><c>yyyy-MM-ddTHH:mm:ss±hh:mm</c> in each value's own offset; dates <c>yyyy-MM-dd</c>.</summary>
    Iso,

    /// <summary><c>MM/dd/yyyy hh:mm:ss tt</c> converted to <see cref="VolumeOptions.TimeZoneOffset"/>; dates <c>MM/dd/yyyy</c>.</summary>
    Us,

    /// <summary><c>dd/MM/yyyy HH:mm:ss</c> converted to <see cref="VolumeOptions.TimeZoneOffset"/>; dates <c>dd/MM/yyyy</c>.</summary>
    Eu,
}

/// <summary>Page image rendition.</summary>
public enum PageImageFormat
{
    /// <summary>Single-page bitonal TIFF (CCITT G4, 300 DPI letter) per page; JPG pages for image natives (jpg/png).</summary>
    Auto,

    /// <summary>Single-page TIFF G4 for every page.</summary>
    Tiff,

    /// <summary>Single-page JPG for every page.</summary>
    Jpg,

    /// <summary>Single-page PNG for every page.</summary>
    Png,

    /// <summary>One multi-page TIFF G4 per document, referenced by a single OPT row.</summary>
    MultiPageTiff,
}

/// <summary>
/// Defects the volume writer can inject. Names (camelCase) are part of the ground-truth contract: never rename.
/// Each type has an eligibility rule (see the README); its rate applies to eligible items only.
/// </summary>
public enum DefectType
{
    /// <summary>A family parent's row, files and OPT rows are withheld, so its attachments are orphans.</summary>
    OrphanAttachments,

    /// <summary>BegAttach/EndAttach on every row of a family are wrong (truncated, reversed or prefix mismatch).</summary>
    BrokenFamilyRange,

    /// <summary>A standalone document's ControlNumber (and OPT image key) repeats an earlier document's.</summary>
    DuplicateControlNumber,

    /// <summary>NativeLink names a file that is not written.</summary>
    MissingNative,

    /// <summary>DAT MD5Hash/SHA256Hash disagree with the native bytes actually written.</summary>
    HashMismatch,

    /// <summary>TextLink names a file that is not written.</summary>
    MissingText,

    /// <summary>The extracted-text file is written in a different encoding than the volume declares.</summary>
    TextEncoding,

    /// <summary>One page image referenced by the OPT is not written (the whole file for multi-page TIFF).</summary>
    MissingImage,

    /// <summary>The PageCount on the OPT document-break row differs from the number of pages.</summary>
    OptPageCountMismatch,

    /// <summary>A date field holds an unparseable value.</summary>
    BadDate,

    /// <summary>A raw text qualifier inside a value is not escaped (not doubled).</summary>
    UnescapedQualifier,

    /// <summary>The row has one field more or fewer than the header.</summary>
    FieldCountMismatch,

    /// <summary>One DAT row is encoded differently from the rest of the file (mixed encoding).</summary>
    DatRowEncoding,

    /// <summary>The overlay DAT contains a row whose key is not in the volume.</summary>
    OverlayUnknownKey,
}

/// <summary>Settings of the load-file volume writer. Every value is recorded in <c>volume-manifest.json</c>.</summary>
public sealed class VolumeOptions
{
    public DelimiterProfile Delimiters { get; init; } = DelimiterProfile.Concordance;

    public LoadFileEncoding DatEncoding { get; init; } = LoadFileEncoding.Utf8Bom;

    public LoadFileEncoding TextEncoding { get; init; } = LoadFileEncoding.Utf8;

    public DatDateFormat DateFormat { get; init; } = DatDateFormat.Iso;

    /// <summary>Zone that <see cref="DatDateFormat.Us"/>/<see cref="DatDateFormat.Eu"/> values are converted to (no offset in the value).</summary>
    public TimeSpan TimeZoneOffset { get; init; } = TimeSpan.Zero;

    public PageImageFormat ImageFormat { get; init; } = PageImageFormat.Auto;

    public string VolumePrefix { get; init; } = "VOL";

    /// <summary>Documents per volume (families are never split); 0 = a single volume.</summary>
    public long DocumentsPerVolume { get; init; }

    /// <summary>Maximum files per IMAGES/NATIVES/TEXT subfolder (a document's pages are never split).</summary>
    public int FilesPerFolder { get; init; } = 1000;

    public bool IncludeNatives { get; init; } = true;

    public bool IncludeText { get; init; } = true;

    public bool IncludeImages { get; init; } = true;

    /// <summary>Characters of extracted text embedded in each synthetic native.</summary>
    public int NativeTextChars { get; init; } = 2000;

    /// <summary>Share of documents listed in the overlay DAT (0 = no overlay file).</summary>
    public double OverlayRate { get; init; }

    /// <summary>Injection rate per defect type, as a share of the type's eligible items. Missing = 0.</summary>
    public IReadOnlyDictionary<DefectType, double> DefectRates { get; init; } = new Dictionary<DefectType, double>();

    public double RateOf(DefectType type) => DefectRates.TryGetValue(type, out double rate) ? rate : 0;

    public static Dictionary<DefectType, double> AllDefects(double rate) => Enum.GetValues<DefectType>().ToDictionary(t => t, _ => rate);

    public void Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(VolumePrefix) || VolumePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errors.Add("volume prefix must be a valid folder name");
        }

        if (DocumentsPerVolume < 0)
        {
            errors.Add("documents per volume must be ≥ 0");
        }

        if (FilesPerFolder < 1)
        {
            errors.Add("files per folder must be ≥ 1");
        }

        if (NativeTextChars < 0)
        {
            errors.Add("native text chars must be ≥ 0");
        }

        if (OverlayRate is < 0 or > 1 || double.IsNaN(OverlayRate))
        {
            errors.Add("overlay rate must be in [0, 1]");
        }

        if (TimeZoneOffset.Ticks % TimeSpan.TicksPerMinute != 0 || TimeZoneOffset.Duration() > TimeSpan.FromHours(14))
        {
            errors.Add("time zone offset must be whole minutes within ±14:00");
        }

        foreach ((DefectType type, double rate) in DefectRates)
        {
            if (rate is < 0 or > 1 || double.IsNaN(rate))
            {
                errors.Add($"defect rate for {DefectNames.Name(type)} must be in [0, 1]");
            }
        }

        errors.AddRange(Delimiters.Validate());
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid volume options: " + string.Join("; ", errors));
        }
    }
}

public static class DefectNames
{
    public static string Name(DefectType type)
    {
        string s = type.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static bool TryParse(string name, out DefectType type)
    {
        foreach (DefectType t in Enum.GetValues<DefectType>())
        {
            if (string.Equals(Name(t), name, StringComparison.OrdinalIgnoreCase))
            {
                type = t;
                return true;
            }
        }

        type = default;
        return false;
    }
}
