using System.Globalization;
using System.IO.Compression;

using Opportunity.Application.Import;
using Opportunity.Import.Jobs;

namespace Opportunity.Import.Volumes;

/// <summary>
/// Archive and decompression-bomb limits (ADR-015 D15.2, T-44, E08-T09) for ZIP natives, Office Open XML and OpenDocument
/// included. Reads the central directories only: the declared uncompressed sizes against the file size (expansion
/// ratio, checked once the expansion is at least <see cref="RatioFloorBytes"/>), the number of entries and the nesting
/// depth. A nested archive (an entry named like an archive) up to <see cref="ImportLimits.MaxNestedArchiveBytes"/> is
/// expanded in memory, never past its declared size, and inspected the same way. Other archive formats and damaged
/// ZIPs are left to the malware scanner. A violation makes the file <see cref="MalwareScanVerdict.Unscannable"/>, so it
/// is quarantined.
/// </summary>
public static class ArchiveInspector
{
    public const string Engine = "archive-limits";

    /// <summary>Expansion below this many bytes is never a bomb, whatever the ratio (small, very compressible files).</summary>
    public const long RatioFloorBytes = 16L * 1024 * 1024;

    private static readonly string[] ArchiveSuffixes =
        [".zip", ".jar", ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".odt", ".ods", ".odp", ".epub", ".xps", ".kmz"];

    /// <summary>The violation of a ZIP file at <paramref name="path"/>, or null when it is within the limits (or not a readable ZIP).</summary>
    public static MalwareScanResult? Inspect(string path, ImportLimits limits)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(limits);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return Inspect(file, limits);
    }

    /// <summary>As <see cref="Inspect(string, ImportLimits)"/> over a seekable stream holding the whole file.</summary>
    public static MalwareScanResult? Inspect(Stream archive, ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(limits);
        var size = Math.Max(1, archive.Length);
        var totals = new Totals();
        try
        {
            if (Walk(archive, 1, limits, totals) is { } violation)
            {
                return violation;
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        if (totals.Expanded >= RatioFloorBytes && totals.Expanded / (double)size > limits.MaxArchiveRatio)
        {
            return Violation(MalwareScanReasons.ArchiveRatio, string.Create(CultureInfo.InvariantCulture,
                $"expands to {totals.Expanded} bytes, more than {limits.MaxArchiveRatio}:1 of its {size} bytes"));
        }

        return null;
    }

    private static MalwareScanResult? Walk(Stream stream, int depth, ImportLimits limits, Totals totals)
    {
        if (depth > limits.MaxArchiveDepth)
        {
            return Violation(MalwareScanReasons.ArchiveDepth, string.Create(CultureInfo.InvariantCulture,
                $"nests archives more than {limits.MaxArchiveDepth} levels deep"));
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            totals.Entries++;
            totals.Expanded += Math.Max(0, entry.Length);
            if (totals.Entries > limits.MaxArchiveEntries)
            {
                return Violation(MalwareScanReasons.ArchiveEntries, string.Create(CultureInfo.InvariantCulture,
                    $"holds more than {limits.MaxArchiveEntries} entries"));
            }

            if (!IsArchiveName(entry.FullName) || entry.Length > limits.MaxNestedArchiveBytes)
            {
                continue;
            }

            using var nested = new MemoryStream(checked((int)entry.Length));
            using (var content = entry.Open())
            {
                if (!CopyAtMost(content, nested, entry.Length))
                {
                    return Violation(MalwareScanReasons.ArchiveRatio, "an entry expands beyond its declared size");
                }
            }

            nested.Position = 0;
            if (nested.Length < 4 || nested.GetBuffer() is not [0x50, 0x4B, 0x03 or 0x05, 0x04 or 0x06, ..])
            {
                continue;
            }

            try
            {
                if (Walk(nested, depth + 1, limits, totals) is { } violation)
                {
                    return violation;
                }
            }
            catch (InvalidDataException)
            {
                // A damaged nested archive: left to the scanner, like a damaged outer one.
            }
        }

        return null;
    }

    /// <summary>Copies the stream, failing when it holds more than <paramref name="limit"/> bytes.</summary>
    private static bool CopyAtMost(Stream source, Stream target, long limit)
    {
        var buffer = new byte[81_920];
        long copied = 0;
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += n;
            if (copied > limit)
            {
                return false;
            }

            target.Write(buffer, 0, n);
        }

        return true;
    }

    private static bool IsArchiveName(string name) =>
        ArchiveSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static MalwareScanResult Violation(string reason, string detail) =>
        MalwareScanResult.Unscannable($"{reason}: the archive {detail}", Engine);

    private sealed class Totals
    {
        public long Entries { get; set; }

        public long Expanded { get; set; }
    }
}
