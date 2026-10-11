using System.Collections.Frozen;

namespace Opportunity.Import.Volumes;

/// <summary>
/// Magic-byte type verification (E08-T09, SEC-15): the type sniffed from a native's bytes (<see cref="ContentSniffer"/>)
/// against the extension of the file name the load file gives. A mismatch is reported (row warning
/// <c>native-type-mismatch</c>) and the sniffed type is what the platform stores and acts on; nothing ever trusts the
/// extension. Flagged: native code under any non-program extension, and a recognised type that the extension does not
/// allow (a <c>.pdf</c> whose bytes are a ZIP). Files whose type the sniffer does not know are not judged.
/// </summary>
public static class FileTypeVerifier
{
    private static readonly FrozenSet<string> ProgramExtensions = FrozenSet.ToFrozenSet(
        ["exe", "dll", "sys", "com", "scr", "cpl", "ocx", "drv", "efi", "so", "o", "ko", "elf", "bin", "out", "dylib", "bundle", "app", "axf", "prx"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ZipFamily = [ContentSniffer.Zip];
    private static readonly string[] OleFamily = [ContentSniffer.OleStorage];

    /// <summary>Extension → the sniffed types its files may have.</summary>
    private static readonly FrozenDictionary<string, string[]> Expected = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = [ContentSniffer.Pdf],
        ["doc"] = OleFamily,
        ["dot"] = OleFamily,
        ["xls"] = OleFamily,
        ["xlt"] = OleFamily,
        ["ppt"] = OleFamily,
        ["pps"] = OleFamily,
        ["msg"] = OleFamily,
        ["vsd"] = OleFamily,
        ["pub"] = OleFamily,
        ["mpp"] = OleFamily,
        ["msi"] = OleFamily,
        ["docx"] = ZipFamily,
        ["docm"] = ZipFamily,
        ["dotx"] = ZipFamily,
        ["xlsx"] = ZipFamily,
        ["xlsm"] = ZipFamily,
        ["pptx"] = ZipFamily,
        ["pptm"] = ZipFamily,
        ["ppsx"] = ZipFamily,
        ["vsdx"] = ZipFamily,
        ["odt"] = ZipFamily,
        ["ods"] = ZipFamily,
        ["odp"] = ZipFamily,
        ["zip"] = ZipFamily,
        ["jar"] = ZipFamily,
        ["epub"] = ZipFamily,
        ["xps"] = ZipFamily,
        ["oxps"] = ZipFamily,
        ["kmz"] = ZipFamily,
        ["png"] = [ContentSniffer.Png],
        ["jpg"] = [ContentSniffer.Jpeg],
        ["jpeg"] = [ContentSniffer.Jpeg],
        ["jpe"] = [ContentSniffer.Jpeg],
        ["jfif"] = [ContentSniffer.Jpeg],
        ["gif"] = [ContentSniffer.Gif],
        ["tif"] = [ContentSniffer.Tiff],
        ["tiff"] = [ContentSniffer.Tiff],
        ["rtf"] = [ContentSniffer.Rtf],
        ["gz"] = [ContentSniffer.Gzip],
        ["tgz"] = [ContentSniffer.Gzip],
        ["7z"] = [ContentSniffer.SevenZip],
        ["rar"] = [ContentSniffer.Rar],
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The mismatch between a file's name and its bytes, or null when they agree or cannot be judged.
    /// </summary>
    /// <param name="fileName">The file name or path the load file gives (only its extension is used).</param>
    /// <param name="sniffedType">The type <see cref="ContentSniffer.Sniff"/> found.</param>
    public static string? Mismatch(string? fileName, string sniffedType)
    {
        ArgumentNullException.ThrowIfNull(sniffedType);
        var extension = Extension(fileName);
        if (extension is null)
        {
            return null;
        }

        if (ContentSniffer.IsExecutable(sniffedType))
        {
            return ProgramExtensions.Contains(extension)
                ? null
                : $"The bytes are executable program code ({sniffedType}), not the .{extension} file the name claims.";
        }

        if (sniffedType == ContentSniffer.OctetStream || !Expected.TryGetValue(extension, out var allowed) || allowed.Contains(sniffedType))
        {
            return null;
        }

        return $"The bytes are {sniffedType}, not the .{extension} file the name claims.";
    }

    /// <summary>The extension without the dot (either path separator), or null.</summary>
    public static string? Extension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = fileName.Trim();
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return null;
        }

        var extension = name[(dot + 1)..];
        return extension.Length <= 16 && extension.All(char.IsAsciiLetterOrDigit) ? extension : null;
    }
}
