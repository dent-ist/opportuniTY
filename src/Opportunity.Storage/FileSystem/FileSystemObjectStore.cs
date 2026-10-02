using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

using Opportunity.Application.Storage;

namespace Opportunity.Storage.FileSystem;

/// <summary>
/// Lite provider (Q-01, Q-38): objects under <c>{root}/objects/{logicalKey}</c>, metadata in a parallel
/// <c>{root}/meta/</c> tree, uploads staged in <c>{root}/staging/</c> and linked into place without overwrite. Files are
/// <c>0600</c>, directories <c>0700</c>. Write-once is enforced in process only (ADR-011 §4.4, a documented reduced
/// guarantee). Content is always streamed through the API, never presigned.
/// </summary>
public sealed class FileSystemObjectStore : ObjectStoreBase, IObjectUrlSigner
{
    private const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirMode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _objectsRoot;
    private readonly string _metaRoot;
    private readonly string _stagingRoot;

    public FileSystemObjectStore(FileSystemObjectStoreOptions options, string keyId = ObjectStorageOptions.InstallationDefaultKeyId)
        : base(keyId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath, nameof(options.RootPath));
        var root = Path.GetFullPath(options.RootPath);
        _objectsRoot = Path.Combine(root, "objects");
        _metaRoot = Path.Combine(root, "meta");
        _stagingRoot = Path.Combine(root, "staging");
        foreach (var dir in new[] { root, _objectsRoot, _metaRoot, _stagingRoot })
        {
            CreateDirectory(dir);
        }
    }

    public ObjectDeliveryMode DeliveryMode => ObjectDeliveryMode.Stream;

    public override async Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        options ??= new PutObjectOptions();
        var expected = ResolveExpectedSha256(key, options);
        var path = ObjectPath(key.Value);

        if (File.Exists(path))
        {
            var (sha, length) = await HashStreamAsync(content, cancellationToken).ConfigureAwait(false);
            VerifyWritten(key, options, expected, sha, length);
            return await ResolveExistingAsync(key, sha, length, cancellationToken).ConfigureAwait(false);
        }

        var staging = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var (sha, length) = await WriteStagingAsync(staging, content, cancellationToken).ConfigureAwait(false);
            VerifyWritten(key, options, expected, sha, length);

            CreateDirectory(Path.GetDirectoryName(path)!);
            if (!NoClobberMove.TryMove(staging, path))
            {
                return await ResolveExistingAsync(key, sha, length, cancellationToken).ConfigureAwait(false);
            }

            await WriteMetadataAsync(key, new Metadata(sha.Hex, KeyId, options.ContentType), cancellationToken).ConfigureAwait(false);
            return new PutObjectResult(key, PutOutcome.Created, sha, length, KeyId, EncryptionScheme.ProviderSse);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    public override Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        FileStream file;
        try
        {
            file = new FileStream(ObjectPath(key.Value), new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ObjectNotFoundException(key, ex);
        }

        if (range is not { } r)
        {
            return Task.FromResult<Stream>(file);
        }

        if (r.Offset >= file.Length)
        {
            file.Dispose();
            throw new ArgumentOutOfRangeException(nameof(range), "The range starts at or after the end of the object.");
        }

        file.Seek(r.Offset, SeekOrigin.Begin);
        var available = file.Length - r.Offset;
        return Task.FromResult<Stream>(new BoundedReadStream(file, Math.Min(available, r.Length ?? available)));
    }

    public override async Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var info = new FileInfo(ObjectPath(key.Value));
        if (!info.Exists)
        {
            return null;
        }

        var meta = await ReadMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        Sha256Digest? sha = meta is not null && Sha256Digest.TryParse(meta.Sha256, out var parsed) ? parsed : null;
        return new ObjectInfo(
            key,
            info.Length,
            sha,
            meta?.KeyId,
            meta?.ContentType ?? ContentDispositionHeader.OctetStream,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public override async IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        foreach (var (key, length) in Enumerate(prefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ObjectListing(key, length);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    protected override Task<DeletePrefixResult> DeletePrefixCoreAsync(ObjectPrefix prefix, CancellationToken cancellationToken)
    {
        long count = 0, bytes = 0;
        var dir = ObjectPath(prefix.Value.TrimEnd('/'));
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes += new FileInfo(file).Length;
                count++;
                File.Delete(file);
            }

            Directory.Delete(dir, recursive: true);
        }

        var metaDir = MetaPath(prefix.Value.TrimEnd('/'), suffix: string.Empty);
        if (Directory.Exists(metaDir))
        {
            Directory.Delete(metaDir, recursive: true);
        }

        return Task.FromResult(new DeletePrefixResult(count, bytes, []));
    }

    public Task<PresignedObjectUrl> PresignGetAsync(ObjectKey key, PresignGetOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The filesystem provider is always streamed through the API (ADR-015 D12.2).");

    public Task<PresignedObjectUrl> PresignPutAsync(ObjectKey key, PresignPutOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The filesystem provider is always streamed through the API (ADR-015 D12.2).");

    private List<(ObjectKey Key, long Length)> Enumerate(ObjectPrefix prefix)
    {
        var dir = ObjectPath(prefix.Value.TrimEnd('/'));
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => (Logical: Path.GetRelativePath(_objectsRoot, f).Replace(Path.DirectorySeparatorChar, '/'), File: f))
            .Select(x => (Key: ObjectKey.TryParse(x.Logical, out var k) ? k : null, x.File))
            .Where(x => x.Key is not null)
            .OrderBy(x => x.Key!.Value, StringComparer.Ordinal)
            .Select(x => (x.Key!, new FileInfo(x.File).Length))];
    }

    private static async Task<(Sha256Digest Sha256, long Length)> WriteStagingAsync(string staging, Stream content, CancellationToken cancellationToken)
    {
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = FileMode0600;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long length = 0;
        try
        {
            var file = new FileStream(staging, fileOptions);
            await using (file.ConfigureAwait(false))
            {
                int n;
                while ((n = await content.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    length += n;
                }

                file.Flush(flushToDisk: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (Sha256Digest.FromBytes(hash.GetHashAndReset()), length);
    }

    private async Task WriteMetadataAsync(ObjectKey key, Metadata metadata, CancellationToken cancellationToken)
    {
        var path = MetaPath(key.Value);
        CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + ".meta");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode0600;
        }

        var file = new FileStream(temp, options);
        await using (file.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(file, metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    private async Task<Metadata?> ReadMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var file = File.OpenRead(MetaPath(key.Value));
            await using (file.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<Metadata>(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private string ObjectPath(string logical) => Contained(_objectsRoot, logical, string.Empty);

    private string MetaPath(string logical, string suffix = ".json") => Contained(_metaRoot, logical, suffix);

    // Keys are grammar-checked, so traversal is impossible; the containment check is defense in depth (ADR-011 §3.2).
    private static string Contained(string root, string logical, string suffix)
    {
        var full = Path.GetFullPath(Path.Combine(root, logical.Replace('/', Path.DirectorySeparatorChar) + suffix));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved path escapes the store root.");
        }

        return full;
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, DirMode0700);
        }
    }

    private sealed record Metadata(string Sha256, string KeyId, string ContentType);
}
