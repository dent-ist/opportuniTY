using System.Globalization;
using System.Text;

using Opportunity.Application.Storage;
using Opportunity.Core.Pages;

namespace Opportunity.Production.Exports;

/// <summary>
/// The deterministic layout of an export volume (ticket review E12-T01): <c>VOL001/DATA/VOL001.dat|.opt</c>,
/// <c>VOL001/NATIVES/NATIVE0001/&lt;Control Number&gt;.&lt;ext&gt;</c>, <c>VOL001/TEXT/TEXT0001/&lt;Control Number&gt;.txt</c>
/// and <c>VOL001/IMAGES/IMG0001/&lt;page key&gt;.&lt;ext&gt;</c>, with the manifest and exclusion report next to the volume.
/// A document's subfolder follows from its snapshot ordinal alone (documents 1–1,000 in folder 0001, …), so every chunk
/// places its files without knowing what the others wrote, and a re-run yields the same paths. Package paths use
/// <c>/</c>; the DAT and OPT use the configured separator. Object keys hold generated names only (ADR-011 §1.3): the
/// export file rows map each key to its delivered path.
/// </summary>
public sealed class ExportLayout(ExportSettings settings)
{
    public const string ManifestJson = "MANIFEST.json";
    public const string ManifestCsv = "MANIFEST.csv";
    public const string ExclusionsCsv = "EXCLUSIONS.csv";

    private const int FolderDigits = 4;
    private const int MaxStemLength = 150;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly ExportSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public string Volume => _settings.VolumeName;

    public string DatPath => $"{Volume}/DATA/{Volume}.dat";

    public string OptPath => $"{Volume}/DATA/{Volume}.opt";

    public string NativePath(long ordinal, string controlNumber, string extension) =>
        $"{Volume}/NATIVES/{Folder("NATIVE", ordinal)}/{FileStem(controlNumber, ordinal)}.{extension}";

    public string TextPath(long ordinal, string controlNumber) =>
        $"{Volume}/TEXT/{Folder("TEXT", ordinal)}/{FileStem(controlNumber, ordinal)}.txt";

    public string ImagePath(long ordinal, string imageKey, PageImageFormat? format) =>
        $"{Volume}/IMAGES/{Folder("IMG", ordinal)}/{FileStem(imageKey, ordinal)}.{ImageExtension(format)}";

    /// <summary>A package path as the DAT and OPT reference it (relative, configured separator).</summary>
    public string LoadFilePath(string packagePath) =>
        _settings.PathSeparator == "/" ? packagePath : packagePath.Replace('/', '\\');

    /// <summary>The OPT image key of a page: the control number for page 1 (it names the document), then <c>CN.0002</c>….</summary>
    public static string PageKey(string controlNumber, int page) =>
        page == 1 ? controlNumber : controlNumber + "." + page.ToString("D4", CultureInfo.InvariantCulture);

    public static string ImageExtension(PageImageFormat? format) => format switch
    {
        PageImageFormat.TiffG4 => "tif",
        PageImageFormat.Jpeg => "jpg",
        PageImageFormat.Png => "png",
        PageImageFormat.WebP => "webp",
        _ => "bin",
    };

    /// <summary>The native's extension: the document's own when it is a plain one, else from the stored content type.</summary>
    public static string NativeExtension(string? fileExtension, string? contentType)
    {
        var ext = fileExtension?.Trim().TrimStart('.');
        if (ext is { Length: >= 1 and <= 10 } && ext.All(char.IsAsciiLetterOrDigit))
        {
            return ext.ToLowerInvariant();
        }

        return contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "application/pdf" => "pdf",
            "image/tiff" => "tif",
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "text/plain" => "txt",
            "text/html" => "html",
            "message/rfc822" => "eml",
            "application/zip" => "zip",
            _ => "bin",
        };
    }

    /// <summary>
    /// A file name stem from a control number or page key: characters that are invalid in Windows or POSIX file names
    /// become <c>_</c>, trailing dots and spaces are removed and reserved device names are prefixed. A stem that had to
    /// change gets the ordinal appended, so two control numbers can never map to the same file.
    /// </summary>
    public static string FileStem(string name, long ordinal)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) || char.IsSurrogate(c) ? '_' : c);
        }

        var stem = builder.ToString().TrimEnd('.', ' ');
        if (stem.Length == 0 || ReservedNames.Contains(stem.Split('.')[0]))
        {
            stem = "_" + stem;
        }

        if (stem.Length > MaxStemLength)
        {
            stem = stem[..MaxStemLength];
        }

        return stem == name ? stem : stem + "_" + ordinal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Key of an object a chunk attempt writes: <c>…/c{sequence}-{leaseToken}/{name}</c>. The lease token makes every
    /// attempt write fresh keys, so a retry after an access change can never collide with an earlier attempt's objects;
    /// only the committed attempt's objects are registered and delivered.
    /// </summary>
    public static ObjectKey ChunkObjectKey(Guid workspaceId, Guid exportId, Guid runId, int sequence, long leaseToken, string name) =>
        ObjectKeys.ExportFile(workspaceId, exportId, runId, string.Create(
            CultureInfo.InvariantCulture, $"c{sequence:D6}-{leaseToken}/{name}"));

    /// <summary>Key of a file the finalization writes (DAT, OPT, manifest, reports); its content is deterministic.</summary>
    public static ObjectKey FinalObjectKey(Guid workspaceId, Guid exportId, Guid runId, string name) =>
        ObjectKeys.ExportFile(workspaceId, exportId, runId, "final/" + name);

    /// <summary>Path of a chunk's DAT or OPT part (never delivered).</summary>
    public static string PartPath(int sequence, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"parts/{sequence:D6}.{extension}");

    private string Folder(string prefix, long ordinal) =>
        prefix + (((ordinal - 1) / _settings.MaxFilesPerFolder) + 1).ToString("D" + FolderDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
