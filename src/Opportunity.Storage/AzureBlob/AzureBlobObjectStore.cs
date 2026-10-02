using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;

using Opportunity.Application.Storage;

namespace Opportunity.Storage.AzureBlob;

/// <summary>
/// Azure Blob provider (Azure SDK, MIT; Azurite in tests). One container per installation; blob name
/// <c>{installationPrefix}/{logicalKey}</c>. Uploads stage blocks with writer-unique block IDs and transactional MD5,
/// then commit with SHA-256/KeyId metadata and <c>If-None-Match: *</c>; a hash or length mismatch never commits, so no
/// object is left behind (uncommitted blocks are garbage-collected by the service).
/// </summary>
public sealed class AzureBlobObjectStore : ObjectStoreBase, IObjectUrlSigner
{
    internal const string ShaMetadata = "sha256";
    internal const string KeyIdMetadata = "keyid";

    private readonly BlobContainerClient _container;
    private readonly PresignPolicy _policy;
    private readonly string _prefix;
    private readonly int _blockSize;

    public AzureBlobObjectStore(AzureBlobObjectStoreOptions options, string keyId = ObjectStorageOptions.InstallationDefaultKeyId, PresignPolicy? policy = null)
        : base(keyId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString, nameof(options.ConnectionString));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Container, nameof(options.Container));
        if (options.BlockSizeBytes < 1024 * 1024 || options.BlockSizeBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BlockSizeBytes must be at least 1 MiB.");
        }

        _container = new BlobContainerClient(options.ConnectionString, options.Container);
        _policy = policy ?? PresignPolicy.Default;
        _prefix = ValidateInstallationPrefix(options.InstallationPrefix);
        _blockSize = (int)options.BlockSizeBytes;
    }

    public ObjectDeliveryMode DeliveryMode => ObjectDeliveryMode.Presign;

    /// <summary>Creates the installation container (private access) when missing (migrator bootstrap step).</summary>
    public async Task EnsureContainerAsync(CancellationToken cancellationToken = default) =>
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);

    public override async Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        options ??= new PutObjectOptions();
        var expected = ResolveExpectedSha256(key, options);

        if (await HeadAsync(key, cancellationToken).ConfigureAwait(false) is not null)
        {
            var (existingSha, existingLength) = await HashStreamAsync(content, cancellationToken).ConfigureAwait(false);
            VerifyWritten(key, options, expected, existingSha, existingLength);
            return await ResolveExistingAsync(key, existingSha, existingLength, cancellationToken).ConfigureAwait(false);
        }

        var blob = _container.GetBlockBlobClient(Physical(_prefix, key.Value));
        var writer = Guid.NewGuid();
        var blockIds = new List<string>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            int filled;
            while ((filled = await FillAsync(content, buffer, _blockSize, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, filled);
                length += filled;
                var blockId = BlockId(writer, blockIds.Count);
                using var body = new MemoryStream(buffer, 0, filled, writable: false);
                await blob.StageBlockAsync(
                    blockId,
                    body,
                    new BlockBlobStageBlockOptions
                    {
                        TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
                    },
                    cancellationToken).ConfigureAwait(false);
                blockIds.Add(blockId);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var sha = Sha256Digest.FromBytes(hash.GetHashAndReset());
        VerifyWritten(key, options, expected, sha, length);

        try
        {
            await blob.CommitBlockListAsync(
                blockIds,
                new CommitBlockListOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = options.ContentType },
                    Metadata = new Dictionary<string, string> { [ShaMetadata] = sha.Hex, [KeyIdMetadata] = KeyId },
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return await ResolveExistingAsync(key, sha, length, cancellationToken).ConfigureAwait(false);
        }

        return new PutObjectResult(key, PutOutcome.Created, sha, length, KeyId, EncryptionScheme.ProviderSse);
    }

    public override async Task<Stream> OpenReadAsync(ObjectKey key, Application.Storage.ByteRange? range = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var blob = _container.GetBlobClient(Physical(_prefix, key.Value));
        var options = new BlobDownloadOptions();
        if (range is { } r)
        {
            options.Range = new HttpRange(r.Offset, r.Length);
        }

        try
        {
            var response = await blob.DownloadStreamingAsync(options, cancellationToken).ConfigureAwait(false);
            if (range is { } requested && requested.Offset >= TotalLength(response.Value.Details))
            {
                // Azure answers 416; some emulators (Azurite) return the whole, shorter blob instead.
                await response.Value.Content.DisposeAsync().ConfigureAwait(false);
                throw new ArgumentOutOfRangeException(nameof(range), "The range starts at or after the end of the object.");
            }

            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new ObjectNotFoundException(key, ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 416)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The range starts at or after the end of the object.");
        }
    }

    public override async Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        BlobProperties properties;
        try
        {
            properties = (await _container.GetBlobClient(Physical(_prefix, key.Value))
                .GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        properties.Metadata.TryGetValue(ShaMetadata, out var shaText);
        properties.Metadata.TryGetValue(KeyIdMetadata, out var keyId);
        Sha256Digest? sha = Sha256Digest.TryParse(shaText, out var parsed) ? parsed : null;
        return new ObjectInfo(
            key,
            properties.ContentLength,
            sha,
            string.IsNullOrEmpty(keyId) ? null : keyId,
            properties.ContentType ?? ContentDispositionHeader.OctetStream,
            properties.LastModified);
    }

    public override async IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        await foreach (var item in _container.GetBlobsAsync(
            new GetBlobsOptions { Prefix = Physical(_prefix, prefix.Value) }, cancellationToken).ConfigureAwait(false))
        {
            if (ToLogical(_prefix, item.Name) is { } key)
            {
                yield return new ObjectListing(key, item.Properties.ContentLength ?? 0);
            }
        }
    }

    protected override async Task<DeletePrefixResult> DeletePrefixCoreAsync(ObjectPrefix prefix, CancellationToken cancellationToken)
    {
        long count = 0, bytes = 0;
        await foreach (var item in _container.GetBlobsAsync(
            new GetBlobsOptions { Prefix = Physical(_prefix, prefix.Value) }, cancellationToken).ConfigureAwait(false))
        {
            var deleted = await _container.DeleteBlobIfExistsAsync(
                item.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (deleted.Value)
            {
                count++;
                bytes += item.Properties.ContentLength ?? 0;
            }
        }

        // Soft-deleted copies (when the account enables blob soft delete) are not reported yet; see E20-T02.
        return new DeletePrefixResult(count, bytes, []);
    }

    public Task<PresignedObjectUrl> PresignGetAsync(ObjectKey key, PresignGetOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        var ttl = _policy.ResolveGetTtl(key, options.Ttl);
        var blob = _container.GetBlobClient(Physical(_prefix, key.Value));
        var expires = DateTimeOffset.UtcNow.Add(ttl);
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, expires)
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
            Protocol = SasProtocolFor(blob.Uri),
            ContentDisposition = ContentDispositionHeader.Attachment(options.DownloadFileName),
            ContentType = ContentDispositionHeader.OctetStream,
            CacheControl = ContentDispositionHeader.NoStore,
        };
        return Task.FromResult(new PresignedObjectUrl(blob.GenerateSasUri(sas), "GET", expires));
    }

    public Task<PresignedObjectUrl> PresignPutAsync(ObjectKey key, PresignPutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ContentLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentType);
        var ttl = _policy.ResolvePutTtl(key, options.Ttl);
        var blob = _container.GetBlobClient(Physical(_prefix, key.Value));
        var expires = DateTimeOffset.UtcNow.Add(ttl);
        var sas = new BlobSasBuilder(BlobSasPermissions.Create, expires)
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
            Protocol = SasProtocolFor(blob.Uri),
        };
        return Task.FromResult(new PresignedObjectUrl(blob.GenerateSasUri(sas), "PUT", expires));
    }

    // "bytes 0-9/1000" carries the blob length after the slash; without a Content-Range the body is the whole blob.
    private static long TotalLength(BlobDownloadDetails details)
    {
        var contentRange = details.ContentRange;
        var slash = contentRange?.LastIndexOf('/') ?? -1;
        return slash >= 0 && long.TryParse(contentRange.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            ? total
            : details.ContentLength;
    }

    private static SasProtocol SasProtocolFor(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ? SasProtocol.Https : SasProtocol.HttpsAndHttp;

    // Writer-unique IDs: concurrent writers to one blob share its uncommitted block list.
    private static string BlockId(Guid writer, int index)
    {
        Span<byte> bytes = stackalloc byte[20];
        writer.TryWriteBytes(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes[16..], index);
        return Convert.ToBase64String(bytes);
    }
}
