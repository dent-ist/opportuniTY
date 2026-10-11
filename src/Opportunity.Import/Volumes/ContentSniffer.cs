namespace Opportunity.Import.Volumes;

/// <summary>
/// The stored content type of an imported file, from its leading bytes only (ADR-011 §2.3: never from the load file or
/// the file name). Coarse on purpose: the gateway always delivers natives as <c>application/octet-stream</c>
/// attachments; the value informs rendering and type verification (<see cref="FileTypeVerifier"/>, E08-T09).
/// </summary>
public static class ContentSniffer
{
    public const int SampleBytes = 16;

    public const string OctetStream = "application/octet-stream";
    public const string Pdf = "application/pdf";
    public const string Zip = "application/zip";
    public const string OleStorage = "application/x-ole-storage";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Tiff = "image/tiff";
    public const string Rtf = "application/rtf";
    public const string Gzip = "application/gzip";
    public const string SevenZip = "application/x-7z-compressed";
    public const string Rar = "application/vnd.rar";
    public const string WindowsExecutable = "application/x-msdownload";
    public const string ElfExecutable = "application/x-executable";
    public const string MachOExecutable = "application/x-mach-binary";

    public static string Sniff(ReadOnlySpan<byte> head) => head switch
    {
        [0x25, 0x50, 0x44, 0x46, ..] => Pdf,
        [0x50, 0x4B, 0x03, 0x04, ..] or [0x50, 0x4B, 0x05, 0x06, ..] => Zip,
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, ..] => OleStorage,
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Png,
        [0xFF, 0xD8, 0xFF, ..] => Jpeg,
        [0x47, 0x49, 0x46, 0x38, ..] => Gif,
        [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => Tiff,
        [0x7B, 0x5C, 0x72, 0x74, 0x66, ..] => Rtf,
        [0x1F, 0x8B, ..] => Gzip,
        [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, ..] => SevenZip,
        [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, ..] => Rar,
        [0x4D, 0x5A, ..] => WindowsExecutable,
        [0x7F, 0x45, 0x4C, 0x46, ..] => ElfExecutable,
        [0xFE, 0xED, 0xFA, 0xCE or 0xCF, ..] or [0xCE or 0xCF, 0xFA, 0xED, 0xFE, ..] => MachOExecutable,
        _ => OctetStream,
    };

    /// <summary>Whether the sniffed type is native code (a program, library or driver).</summary>
    public static bool IsExecutable(string contentType) =>
        contentType is WindowsExecutable or ElfExecutable or MachOExecutable;
}
