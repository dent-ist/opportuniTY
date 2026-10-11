using Opportunity.Application.Import;
using Opportunity.Import.Jobs;
using Opportunity.Import.Volumes;

namespace Opportunity.Import.Scanning;

/// <summary>
/// Screens a volume file before anything renders it (E08-T09, ADR-015 D15/D16): ZIP natives against the archive limits
/// (<see cref="ArchiveInspector"/>), then the bytes through the configured <see cref="IMalwareScanner"/>. The result is
/// null when nothing screened the file (scanning off and no archive verdict).
/// </summary>
public sealed class IngestScreening(ImportLimits limits, IMalwareScanner? scanner)
{
    public ImportLimits Limits => limits;

    public IMalwareScanner? Scanner => scanner;

    /// <summary>A volume file at its (link-free, resolved) path, with the type sniffed from its bytes.</summary>
    public async Task<MalwareScanResult?> ScreenAsync(string fullPath, string sniffedType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        ArgumentNullException.ThrowIfNull(sniffedType);
        if (sniffedType == ContentSniffer.Zip && ArchiveInspector.Inspect(fullPath, limits) is { } violation)
        {
            return violation;
        }

        if (scanner is null)
        {
            return null;
        }

        var file = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 81_920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        await using (file.ConfigureAwait(false))
        {
            return await scanner.ScanAsync(file, file.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Bytes already open (a page image being stored): rewound before and after the scan.</summary>
    public async Task<MalwareScanResult?> ScreenAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (scanner is null)
        {
            return null;
        }

        content.Position = 0;
        var result = await scanner.ScanAsync(content, content.Length, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        return result;
    }
}
