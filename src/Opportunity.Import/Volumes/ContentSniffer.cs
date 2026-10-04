namespace Opportunity.Import.Volumes;

/// <summary>
/// The stored content type of an imported file, from its leading bytes only (ADR-011 §2.3: never from the load file or
/// the file name). Coarse on purpose: the gateway always delivers natives as <c>application/octet-stream</c>
/// attachments; the value informs rendering and type verification (<c>E08-T09</c>).
/// </summary>
public static class ContentSniffer
{
    public const int SampleBytes = 16;

    public const string OctetStream = "application/octet-stream";

    public static string Sniff(ReadOnlySpan<byte> head) => head switch
    {
        [0x25, 0x50, 0x44, 0x46, ..] => "application/pdf",
        [0x50, 0x4B, 0x03, 0x04, ..] => "application/zip",
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, ..] => "application/x-ole-storage",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => "image/tiff",
        [0x7B, 0x5C, 0x72, 0x74, 0x66, ..] => "application/rtf",
        _ => OctetStream,
    };
}
