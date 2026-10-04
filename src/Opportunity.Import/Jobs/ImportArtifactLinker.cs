using Opportunity.Application.Import;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Import;
using Opportunity.Core.Documents;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;
using Opportunity.Import.Volumes;

using CoreArea = Opportunity.Core.Storage.ObjectArea;

namespace Opportunity.Import.Jobs;

/// <summary>
/// Links a row's native and extracted text (E08-T04, Q-29): resolves <c>NativeLink</c>/<c>TextLink</c> inside the
/// volume (<see cref="ImportVolume"/>), stores the files content-addressed under the row's document
/// (<see cref="VolumeObjectWriter"/>), checks load-file hashes against the native (a mismatch is a warning and the
/// computed value is stored, ADR-009 R17) and sets the artifact flags: <c>NativeMissing</c>, <c>TextMissing</c>,
/// <c>TextLength</c> (full length), <c>TextTruncated</c> (above the indexed-text cap; the full text stays in storage)
/// and <c>TextEncodingWarning</c>. A missing file flags the row (or, configured, fails it); a path that leaves the
/// volume always fails it. Neither fails the chunk.
/// </summary>
public sealed class ImportArtifactLinker
{
    private readonly ColumnBinding? _native;
    private readonly ColumnBinding? _text;
    private readonly PathSettings _paths;
    private readonly LoadFileEncodingKind? _textEncoding;
    private readonly ImportVolume? _volume;
    private readonly string? _volumeError;
    private readonly VolumeObjectWriter _writer;
    private readonly int _textCap;

    private ImportArtifactLinker(
        ColumnBinding? native, ColumnBinding? text, PathSettings paths, LoadFileEncodingKind? textEncoding, ImportVolume? volume,
        string? volumeError, VolumeObjectWriter writer, int textCap)
    {
        _native = native;
        _text = text;
        _paths = paths;
        _textEncoding = textEncoding;
        _volume = volume;
        _volumeError = volumeError;
        _writer = writer;
        _textCap = textCap;
    }

    /// <summary>
    /// The columns whose non-blank values are files in the volume: the native path, and the text path unless the text
    /// is given in the load file itself.
    /// </summary>
    public static IReadOnlyList<int> VolumeColumns(CompiledMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var columns = new List<int>();
        if (Column(mapping, StructuralTarget.NativePath) is { } native)
        {
            columns.Add(native.Index);
        }

        if (Column(mapping, StructuralTarget.TextPath) is { } text && !mapping.EffectiveProfile.Paths.TextInLoadFile)
        {
            columns.Add(text.Index);
        }

        return columns;
    }

    /// <summary>Whether a record names a file in the volume (see <see cref="VolumeColumns"/>).</summary>
    public static bool LinksFile(IReadOnlyList<int> volumeColumns, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(volumeColumns);
        ArgumentNullException.ThrowIfNull(values);
        return volumeColumns.Any(i => i < values.Count && !string.IsNullOrWhiteSpace(values[i]));
    }

    /// <summary>The preparation-time check: the volume is configured and present (null when fine).</summary>
    public static string? VolumeProblem(CompiledMapping mapping, ImportVolumeOptions volumes)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(volumes);
        return ImportVolume.TryOpen(volumes, mapping.EffectiveProfile.Paths.VolumeRoot, out _, out var error) ? null : error;
    }

    /// <summary>
    /// The linker of a chunk, or null when the mapping links no files. When the volume is not available now (an
    /// unmounted share), the first row that names a file throws <see cref="DirectoryNotFoundException"/>, so the chunk
    /// is retried; rows with blank links still load.
    /// </summary>
    public static ImportArtifactLinker? Create(CompiledMapping mapping, ImportJobOptions options, ImportVolumeOptions volumes, IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(store);
        var native = Column(mapping, StructuralTarget.NativePath);
        var text = Column(mapping, StructuralTarget.TextPath);
        if (native is null && text is null)
        {
            return null;
        }

        var paths = mapping.EffectiveProfile.Paths;
        ImportVolume? volume = null;
        string? volumeError = null;
        if (VolumeColumns(mapping).Count > 0 && !ImportVolume.TryOpen(volumes, paths.VolumeRoot, out volume, out volumeError))
        {
            volume = null;
        }

        var encoding = LoadFileSettingsResolver.ResolveEncoding(mapping.EffectiveProfile.LoadFile.TextEncoding, "text", []);
        return new ImportArtifactLinker(native, text, paths, encoding, volume, volumeError, new VolumeObjectWriter(store), options.IndexedTextCap);
    }

    /// <summary>
    /// Stores the row's files and returns the row with its objects, flags and issues. <paramref name="values"/> are the
    /// record's raw values; <paramref name="existing"/> marks an overlay of an existing document, whose artifacts a
    /// blank or missing file never clears.
    /// </summary>
    public async Task<ImportRow> LinkAsync(ImportRow row, IReadOnlyList<string> values, bool existing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(values);
        if (row.HasErrors || row.Document is not { } document)
        {
            return row;
        }

        var issues = row.Issues.ToList();
        var supplied = new HashSet<string>(row.SuppliedColumns, StringComparer.Ordinal);
        var objects = new List<ImportObject>();
        if (_native is { } nativeColumn)
        {
            await LinkNativeAsync(document, Raw(values, nativeColumn), nativeColumn.Column, existing, issues, supplied, objects, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_text is { } textColumn)
        {
            await LinkTextAsync(document, Raw(values, textColumn), textColumn.Column, existing, issues, supplied, objects, cancellationToken)
                .ConfigureAwait(false);
        }

        if (issues.Any(i => i.Severity == ImportIssueSeverity.Error))
        {
            return row with { Document = null, Issues = issues, Objects = [] };
        }

        return row with { Issues = issues, SuppliedColumns = supplied, Objects = objects };
    }

    private async Task LinkNativeAsync(
        Document document, string raw, string column, bool existing, List<ImportRowIssue> issues, HashSet<string> supplied, List<ImportObject> objects,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            document.NativeMissing = !existing;
            return;
        }

        var file = Volume().Resolve(raw, _paths.StripPrefix);
        if (file.Status != VolumeFileStatus.Found)
        {
            Missing(document, file, column, "native", existing, issues, supplied, d => d.NativeMissing = true, "native_missing");
            return;
        }

        var stored = await _writer.StoreFileAsync(document.WorkspaceId, document.DocumentId, CoreArea.Native, file.FullPath!, cancellationToken)
            .ConfigureAwait(false);
        objects.Add(stored.Stored);
        CompareHash(document.Md5, stored.Md5, "MD5", "md5", column, issues);
        CompareHash(document.Sha1, stored.Sha1, "SHA-1", "sha1", column, issues);
        CompareHash(document.Sha256, stored.Stored.Sha256, "SHA-256", "sha256", column, issues);
        document.Md5 = stored.Md5;
        document.Sha1 = stored.Sha1;
        document.Sha256 = stored.Stored.Sha256;
        document.NativeMissing = false;
        supplied.UnionWith(["md5", "sha1", "sha256", "native_missing", "native_object_id"]);
        if (document.FileSize is null)
        {
            document.FileSize = stored.Stored.SizeBytes;
            supplied.Add("file_size");
        }
    }

    private async Task LinkTextAsync(
        Document document, string raw, string column, bool existing, List<ImportRowIssue> issues, HashSet<string> supplied, List<ImportObject> objects,
        CancellationToken cancellationToken)
    {
        StoredVolumeText stored;
        if (_paths.TextInLoadFile)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                document.TextMissing = !existing;
                return;
            }

            stored = await _writer.StoreTextAsync(document.WorkspaceId, document.DocumentId, raw, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                document.TextMissing = !existing;
                return;
            }

            var file = Volume().Resolve(raw, _paths.StripPrefix);
            if (file.Status != VolumeFileStatus.Found)
            {
                Missing(document, file, column, "text", existing, issues, supplied, d => d.TextMissing = true, "text_missing");
                return;
            }

            stored = await _writer.StoreTextFileAsync(document.WorkspaceId, document.DocumentId, file.FullPath!, _textEncoding, cancellationToken)
                .ConfigureAwait(false);
        }

        objects.Add(stored.Stored);
        document.TextLength = stored.Chars;
        document.TextTruncated = stored.Chars > _textCap;
        document.TextMissing = false;
        document.TextEncodingWarning = stored.EncodingWarning;
        supplied.UnionWith(["text_object_id", "text_length", "text_truncated", "text_missing", "text_encoding_warning"]);
        if (stored.EncodingWarning)
        {
            issues.Add(new ImportRowIssue(ImportIssueSeverity.Warning, "text-encoding",
                "The extracted text has byte sequences invalid in its encoding; they were replaced with U+FFFD.", column));
        }
    }

    /// <summary>A file that is not there (flag or error, per policy) or a path that is refused (always an error).</summary>
    private void Missing(
        Document document, VolumeFile file, string column, string kind, bool existing, List<ImportRowIssue> issues, HashSet<string> supplied,
        Action<Document> flag, string flagColumn)
    {
        if (file.Status == VolumeFileStatus.Rejected)
        {
            issues.Add(new ImportRowIssue(ImportIssueSeverity.Error, kind + "-path-rejected", Truncate($"The {kind} path is refused: {file.Reason}"), column));
            return;
        }

        if (_paths.MissingFiles == MissingFilePolicy.Error)
        {
            issues.Add(new ImportRowIssue(ImportIssueSeverity.Error, kind + "-missing", Truncate($"The {kind} file is missing: {file.Reason}"), column));
            return;
        }

        issues.Add(new ImportRowIssue(ImportIssueSeverity.Warning, kind + "-missing", Truncate($"The {kind} file is missing: {file.Reason}"), column));
        if (!existing)
        {
            flag(document);
            supplied.Add(flagColumn);
        }
    }

    private static void CompareHash(byte[]? fromLoadFile, byte[] computed, string name, string code, string column, List<ImportRowIssue> issues)
    {
        if (fromLoadFile is not null && !fromLoadFile.AsSpan().SequenceEqual(computed))
        {
            issues.Add(new ImportRowIssue(ImportIssueSeverity.Warning, "hash-mismatch-" + code,
                $"{name} in the load file ({Convert.ToHexStringLower(fromLoadFile)}) differs from the native's ({Convert.ToHexStringLower(computed)}); the computed value is stored.",
                column));
        }
    }

    private ImportVolume Volume() => _volume ?? throw new DirectoryNotFoundException(_volumeError ?? "The import volume is not available.");

    private static string Raw(IReadOnlyList<string> values, ColumnBinding column) => column.Index < values.Count ? values[column.Index] : string.Empty;

    private static ColumnBinding? Column(CompiledMapping mapping, StructuralTarget target) =>
        mapping.Columns.FirstOrDefault(c => c.Status == ColumnStatus.Mapped && c.Targets.Any(t => t.Usable && t.Structural == target));

    private static string Truncate(string message) =>
        message.Length <= ImportRowIssue.MaxMessageLength ? message : message[..ImportRowIssue.MaxMessageLength];
}
