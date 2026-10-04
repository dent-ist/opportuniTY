using System.Globalization;
using System.Security.Cryptography;

using Opportunity.Application.Import;
using Opportunity.Application.Storage;
using Opportunity.Import.Images;
using Opportunity.Import.Volumes;

using CoreEncryption = Opportunity.Core.Storage.EncryptionScheme;

namespace Opportunity.Import.Jobs;

/// <summary>
/// Turns the OPT rows of one document into its Imported page set (ADR-012 §1.6): resolves every image path in the
/// import-root jail, stores each image file once under the document's content-addressed image key (ADR-011, so a retried
/// chunk re-puts identical bytes and writes nothing new), reads its geometry, and reconciles the document's pages with
/// the break row's PageCount. One page per OPT row; a TIFF that a document references once expands to one page per
/// frame (a multi-page TIFF), and a TIFF referenced by several rows gives the n-th row its n-th frame. Every page whose
/// image cannot be used stays in the set as a missing page, with a warning on its OPT row.
/// </summary>
public static class ImportImageLinker
{
    /// <summary>Largest image file stored for one OPT row.</summary>
    public const long MaxImageBytes = 2L * 1024 * 1024 * 1024;

    public static class Codes
    {
        public const string RowMalformed = "opt-row-malformed";
        public const string PathRejected = "opt-path-rejected";
        public const string ImageMissing = "opt-image-missing";
        public const string ImageUnreadable = "opt-image-unreadable";
        public const string ImageUnsupported = "opt-image-unsupported";
        public const string ImageTooLarge = "opt-image-too-large";
        public const string FrameMissing = "opt-image-frame-missing";
        public const string PageCountMismatch = "opt-page-count-mismatch";
        public const string VolumeUnavailable = "opt-volume-unavailable";
        public const string DocumentNotFound = "opt-document-not-found";

        /// <summary>A DAT row that an import with an OPT gives no images.</summary>
        public const string NoImages = "no-images";
    }

    public static async Task<(ImportImages Images, IReadOnlyList<ImportOptIssue> Issues)> LinkAsync(
        IObjectStore store, Guid workspaceId, Guid documentId, IReadOnlyList<ImportImageRow> rows, ImportVolume? volume, string? stripPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfZero(rows.Count);
        var breakRow = rows.FirstOrDefault(r => r.IsBreak) ?? rows[0];
        var issues = new List<ImportOptIssue>();
        void Warn(ImportImageRow row, string code, string message) =>
            issues.Add(new ImportOptIssue(row.OptRow, row.LineNo, Key(row), new ImportRowIssue(ImportIssueSeverity.Warning, code, Truncate(message), "Path")));

        // Resolve every row first: a file referenced by several rows is a multi-page TIFF read frame by frame.
        var resolved = new List<(ImportImageRow Row, string? File)>(rows.Count);
        foreach (var row in rows)
        {
            string? file = null;
            if (row.Problem is { } problem)
            {
                Warn(row, Codes.RowMalformed, $"OPT row {row.OptRow} cannot be read: {problem} Its page has no image.");
            }
            else if (volume is null)
            {
                Warn(row, Codes.VolumeUnavailable, $"No volume root is available to read image {row.Path}.");
            }
            else if (!volume.TryResolve(row.Path, stripPrefix, out file, out var error))
            {
                Warn(row, Codes.PathRejected, $"The image path '{row.Path}' was rejected: {error}");
            }

            resolved.Add((row, file));
        }

        var references = resolved.Where(r => r.File is not null).GroupBy(r => r.File!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var files = new Dictionary<string, StoredFile>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var objects = new Dictionary<string, ImportImageObject>(StringComparer.Ordinal);
        var pages = new List<(string? ImageKey, int Frame, ImportPageRaster? Raster)>();
        foreach (var (row, file) in resolved)
        {
            if (file is null)
            {
                pages.Add((Key(row), 0, null));
                continue;
            }

            if (!files.TryGetValue(file, out var stored))
            {
                stored = await StoreAsync(store, workspaceId, documentId, file, cancellationToken).ConfigureAwait(false);
                files[file] = stored;
                if (stored.Object is { } obj)
                {
                    objects.TryAdd(obj.LogicalKey, obj);
                }
            }

            var occurrence = occurrences[file] = occurrences.GetValueOrDefault(file) + 1;
            if (stored.Info is not { } info)
            {
                Warn(row, stored.Code!, $"The image file '{row.Path}' of page {Key(row)} {stored.Message}");

                // A missing multi-page file of a one-row document: the break row's count says how many pages are gone.
                var missing = rows.Count == 1 && breakRow.PageCount is > 1 and <= 100_000 ? breakRow.PageCount.Value : 1;
                for (var i = 0; i < missing; i++)
                {
                    pages.Add((i == 0 ? Key(row) : null, i, null));
                }

                continue;
            }

            if (references[file] == 1)
            {
                for (var frame = 0; frame < info.Frames.Count; frame++)
                {
                    pages.Add((frame == 0 ? Key(row) : null, frame, Raster(stored, frame)));
                }
            }
            else if (occurrence <= info.Frames.Count)
            {
                pages.Add((Key(row), occurrence - 1, Raster(stored, occurrence - 1)));
            }
            else
            {
                Warn(row, Codes.FrameMissing,
                    $"OPT row {row.OptRow} is reference {occurrence} to '{row.Path}', which has only {info.Frames.Count} page(s).");
                pages.Add((Key(row), occurrence - 1, null));
            }
        }

        if (breakRow.PageCount is { } declared && declared != pages.Count)
        {
            issues.Add(new ImportOptIssue(breakRow.OptRow, breakRow.LineNo, Key(breakRow), new ImportRowIssue(
                ImportIssueSeverity.Warning, Codes.PageCountMismatch,
                Truncate(string.Create(CultureInfo.InvariantCulture,
                    $"The OPT declares {declared} page(s) for {Key(breakRow)} but has {pages.Count} page(s) ({rows.Count} OPT row(s) up to the next document break)."))
                , "PageCount")));
        }

        var images = new ImportImages
        {
            DocumentId = documentId,
            BreakOptRow = breakRow.OptRow,
            BreakLineNo = breakRow.LineNo,
            BreakImageKey = Key(breakRow),
            Objects = [.. objects.Values],
            Pages = [.. pages.Select((p, i) => new ImportPage(i + 1, p.ImageKey, p.Frame, p.Raster))],
        };
        return (images, [.. issues.OrderBy(i => i.OptRow)]);
    }

    private static ImportPageRaster Raster(StoredFile stored, int frame)
    {
        var f = stored.Info!.Frames[frame];
        return new ImportPageRaster(stored.Object!.LogicalKey, f.WidthPx, f.HeightPx, f.DpiX, f.DpiY, stored.Info.Format, f.ColorMode);
    }

    private static async Task<StoredFile> StoreAsync(
        IObjectStore store, Guid workspaceId, Guid documentId, string path, CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return StoredFile.Failed(Codes.ImageMissing, "was not found in the volume.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StoredFile.Failed(Codes.ImageUnreadable, $"cannot be read ({ex.GetType().Name}).");
        }

        await using (stream.ConfigureAwait(false))
        {
            var length = stream.Length;
            if (length > MaxImageBytes)
            {
                return StoredFile.Failed(Codes.ImageTooLarge, string.Create(CultureInfo.InvariantCulture,
                    $"has {length} bytes; at most {MaxImageBytes} are loaded."));
            }

            // The type comes from the bytes (ADR-011 §2.3), never from the file name or the load file.
            var info = ImageProbe.Probe(stream);
            if (info is null)
            {
                return StoredFile.Failed(Codes.ImageUnsupported,
                    "is not a readable TIFF, JPEG or PNG page image.");
            }

            stream.Position = 0;
            var sha = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var digest = Sha256Digest.FromBytes(sha);
            var key = ObjectKeys.Image(workspaceId, documentId, digest);
            stream.Position = 0;
            var put = await store.PutAsync(key, stream, new PutObjectOptions
            {
                ContentType = info.ContentType,
                ExpectedSha256 = digest,
                ExpectedLength = length,
            }, cancellationToken).ConfigureAwait(false);
            var scheme = put.EncryptionScheme == EncryptionScheme.Envelope ? CoreEncryption.Envelope : CoreEncryption.ProviderSse;
            return new StoredFile(info, new ImportImageObject(key.Value, sha, length, info.ContentType, put.KeyId, scheme), null, null);
        }
    }

    private static string Key(ImportImageRow row) => row.ImageKey;

    private static string Truncate(string message) =>
        message.Length <= ImportRowIssue.MaxMessageLength ? message : message[..ImportRowIssue.MaxMessageLength];

    private sealed record StoredFile(ImageInfo? Info, ImportImageObject? Object, string? Code, string? Message)
    {
        public static StoredFile Failed(string code, string message) => new(null, null, code, message);
    }
}
