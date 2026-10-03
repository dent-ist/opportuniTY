using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Opportunity.Application.Storage;

namespace Opportunity.Storage.S3;

/// <summary>
/// S3-compatible provider (AWS SDK for .NET, Apache-2.0). One bucket per installation; physical name
/// <c>{installationPrefix}/{logicalKey}</c>. Objects up to one part are sent as a single PUT carrying SHA-256 metadata
/// and <c>x-amz-checksum-sha256</c>; larger ones as multipart uploads that are aborted when the streamed hash or length
/// does not match. Write-once is enforced with <c>If-None-Match: *</c> plus HEAD-and-compare (ADR-011 §4.1).
/// </summary>
public sealed class S3ObjectStore : ObjectStoreBase, IObjectUrlSigner, IDisposable
{
    internal const string ShaMetadata = "sha256";
    internal const string KeyIdMetadata = "keyid";

    private readonly IAmazonS3 _client;
    private readonly IAmazonS3 _signingClient;
    private readonly bool _ownsClients;
    private readonly S3ObjectStoreOptions _options;
    private readonly PresignPolicy _policy;
    private readonly string _prefix;
    private readonly int _partSize;
    private readonly bool _signHttps;

    public S3ObjectStore(S3ObjectStoreOptions options, string keyId = ObjectStorageOptions.InstallationDefaultKeyId, PresignPolicy? policy = null)
        : this(options, CreateClient(options, options?.ServiceUrl), CreateClient(options, options?.PublicServiceUrl ?? options?.ServiceUrl), keyId, policy, ownsClients: true)
    {
    }

    private S3ObjectStore(S3ObjectStoreOptions options, IAmazonS3 client, IAmazonS3 signingClient, string keyId, PresignPolicy? policy, bool ownsClients)
        : base(keyId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Bucket, nameof(options.Bucket));
        if (options.PartSizeBytes < S3ObjectStoreOptions.MinPartSizeBytes || options.PartSizeBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PartSizeBytes must be at least 5 MiB (the S3 minimum part size).");
        }

        _options = options;
        _client = client;
        _signingClient = signingClient;
        _ownsClients = ownsClients;
        _policy = policy ?? PresignPolicy.Default;
        _prefix = ValidateInstallationPrefix(options.InstallationPrefix);
        _partSize = (int)options.PartSizeBytes;
        _signHttps = (options.PublicServiceUrl ?? options.ServiceUrl)?.Scheme != Uri.UriSchemeHttp;
    }

    public ObjectDeliveryMode DeliveryMode => ObjectDeliveryMode.Presign;

    /// <summary>Creates the installation bucket when missing (migrator bootstrap step).</summary>
    public async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    public override async Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        options ??= new PutObjectOptions();
        var expected = ResolveExpectedSha256(key, options);

        if (await HeadAsync(key, cancellationToken).ConfigureAwait(false) is not null)
        {
            var (sha, length) = await HashStreamAsync(content, cancellationToken).ConfigureAwait(false);
            VerifyWritten(key, options, expected, sha, length);
            return await ResolveExistingAsync(key, sha, length, cancellationToken).ConfigureAwait(false);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(_partSize);
        try
        {
            var first = await FillAsync(content, buffer, _partSize, cancellationToken).ConfigureAwait(false);
            return first < _partSize
                ? await PutSingleAsync(key, options, expected, buffer, first, cancellationToken).ConfigureAwait(false)
                : await PutMultipartAsync(key, content, options, expected, buffer, first, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override async Task<Stream> OpenReadAsync(ObjectKey key, Application.Storage.ByteRange? range = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var request = new GetObjectRequest { BucketName = _options.Bucket, Key = PhysicalKey(key) };
        if (range is { } r)
        {
            request.ByteRange = new Amazon.S3.Model.ByteRange(r.LastByte is { } last ? $"bytes={r.Offset}-{last}" : $"bytes={r.Offset}-");
        }

        try
        {
            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ObjectNotFoundException(key, ex);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The range starts at or after the end of the object.");
        }
    }

    public override async Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        GetObjectMetadataResponse response;
        try
        {
            response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = PhysicalKey(key) },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var shaText = response.Metadata[ShaMetadata];
        var keyId = response.Metadata[KeyIdMetadata];
        Sha256Digest? sha = Sha256Digest.TryParse(shaText, out var parsed) ? parsed : null;
        return new ObjectInfo(
            key,
            response.ContentLength,
            sha,
            string.IsNullOrEmpty(keyId) ? null : keyId,
            response.Headers.ContentType ?? ContentDispositionHeader.OctetStream,
            response.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) : null);
    }

    public override async IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var request = new ListObjectsV2Request { BucketName = _options.Bucket, Prefix = Physical(_prefix, prefix.Value) };
        ListObjectsV2Response page;
        do
        {
            page = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.S3Objects ?? [])
            {
                if (ToLogical(_prefix, item.Key) is { } key)
                {
                    yield return new ObjectListing(key, item.Size ?? 0);
                }
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);
    }

    protected override async Task<DeletePrefixResult> DeletePrefixCoreAsync(ObjectPrefix prefix, CancellationToken cancellationToken)
    {
        long count = 0, bytes = 0;
        var physicalPrefix = Physical(_prefix, prefix.Value);
        while (true)
        {
            // Always re-list from the start: resumable after a crash and unaffected by deletes during paging.
            var page = await _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _options.Bucket, Prefix = physicalPrefix, MaxKeys = 1000 },
                cancellationToken).ConfigureAwait(false);
            var objects = page.S3Objects ?? [];
            if (objects.Count == 0)
            {
                break;
            }

            var response = await _client.DeleteObjectsAsync(
                new DeleteObjectsRequest
                {
                    BucketName = _options.Bucket,
                    Quiet = true,
                    Objects = [.. objects.Select(o => new KeyVersion { Key = o.Key })],
                },
                cancellationToken).ConfigureAwait(false);
            if (response.DeleteErrors is { Count: > 0 } errors)
            {
                throw new IOException($"Deleting under '{prefix}' failed for {errors.Count} object(s): {errors[0].Code}.");
            }

            count += objects.Count;
            bytes += objects.Sum(o => o.Size ?? 0);
        }

        return new DeletePrefixResult(count, bytes, await VersioningResidualsAsync(prefix, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PresignedObjectUrl> PresignGetAsync(ObjectKey key, PresignGetOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        var ttl = _policy.ResolveGetTtl(key, options.Ttl);
        var expires = DateTime.UtcNow.Add(ttl);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = PhysicalKey(key),
            Verb = HttpVerb.GET,
            Expires = expires,
            Protocol = _signHttps ? Protocol.HTTPS : Protocol.HTTP,
        };
        request.ResponseHeaderOverrides.ContentDisposition = ContentDispositionHeader.Attachment(options.DownloadFileName);
        request.ResponseHeaderOverrides.ContentType = ContentDispositionHeader.OctetStream;
        request.ResponseHeaderOverrides.CacheControl = ContentDispositionHeader.NoStore;
        var url = await _signingClient.GetPreSignedURLAsync(request).ConfigureAwait(false);
        return new PresignedObjectUrl(new Uri(url), "GET", new DateTimeOffset(expires, TimeSpan.Zero));
    }

    public async Task<PresignedObjectUrl> PresignPutAsync(ObjectKey key, PresignPutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ContentLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentType);
        var ttl = _policy.ResolvePutTtl(key, options.Ttl);
        var expires = DateTime.UtcNow.Add(ttl);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = PhysicalKey(key),
            Verb = HttpVerb.PUT,
            Expires = expires,
            Protocol = _signHttps ? Protocol.HTTPS : Protocol.HTTP,
            ContentType = options.ContentType,
        };
        var url = await _signingClient.GetPreSignedURLAsync(request).ConfigureAwait(false);
        return new PresignedObjectUrl(new Uri(url), "PUT", new DateTimeOffset(expires, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (_ownsClients)
        {
            _client.Dispose();
            if (!ReferenceEquals(_client, _signingClient))
            {
                _signingClient.Dispose();
            }
        }
    }

    private async Task<PutObjectResult> PutSingleAsync(ObjectKey key, PutObjectOptions options, Sha256Digest? expected, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var sha = Sha256Digest.Compute(buffer.AsSpan(0, length));
        VerifyWritten(key, options, expected, sha, length);

        using var body = new MemoryStream(buffer, 0, length, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = PhysicalKey(key),
            InputStream = body,
            AutoCloseStream = false,
            ContentType = options.ContentType,
        };
        request.Metadata[ShaMetadata] = sha.Hex;
        request.Metadata[KeyIdMetadata] = KeyId;
        ApplyEncryption(request);
        if (_options.SendChecksums)
        {
            request.ChecksumSHA256 = sha.ToBase64();
        }

        if (_options.UseConditionalWrites)
        {
            request.IfNoneMatch = "*";
        }

        try
        {
            await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (IsWriteConflict(ex))
        {
            return await ResolveExistingAsync(key, sha, length, cancellationToken).ConfigureAwait(false);
        }

        return new PutObjectResult(key, PutOutcome.Created, sha, length, KeyId, EncryptionScheme.ProviderSse);
    }

    private async Task<PutObjectResult> PutMultipartAsync(
        ObjectKey key, Stream content, PutObjectOptions options, Sha256Digest? expected, byte[] buffer, int filled, CancellationToken cancellationToken)
    {
        var physical = PhysicalKey(key);
        var create = new InitiateMultipartUploadRequest { BucketName = _options.Bucket, Key = physical, ContentType = options.ContentType };
        create.Metadata[KeyIdMetadata] = KeyId;
        if (expected is { } known)
        {
            create.Metadata[ShaMetadata] = known.Hex;
        }

        ApplyEncryption(create);
        var upload = await _client.InitiateMultipartUploadAsync(create, cancellationToken).ConfigureAwait(false);
        var completed = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var parts = new List<PartETag>();
            long length = 0;
            while (filled > 0)
            {
                hash.AppendData(buffer, 0, filled);
                length += filled;
                using var body = new MemoryStream(buffer, 0, filled, writable: false);
                var part = await _client.UploadPartAsync(
                    new UploadPartRequest
                    {
                        BucketName = _options.Bucket,
                        Key = physical,
                        UploadId = upload.UploadId,
                        PartNumber = parts.Count + 1,
                        InputStream = body,
                        PartSize = filled,
                    },
                    cancellationToken).ConfigureAwait(false);
                parts.Add(new PartETag(parts.Count + 1, part.ETag));
                filled = await FillAsync(content, buffer, _partSize, cancellationToken).ConfigureAwait(false);
            }

            var sha = Sha256Digest.FromBytes(hash.GetHashAndReset());
            VerifyWritten(key, options, expected, sha, length);

            var complete = new CompleteMultipartUploadRequest
            {
                BucketName = _options.Bucket,
                Key = physical,
                UploadId = upload.UploadId,
                PartETags = parts,
            };
            if (_options.UseConditionalWrites)
            {
                complete.IfNoneMatch = "*";
            }

            try
            {
                await _client.CompleteMultipartUploadAsync(complete, cancellationToken).ConfigureAwait(false);
                completed = true;
            }
            catch (AmazonS3Exception ex) when (IsWriteConflict(ex))
            {
                return await ResolveExistingAsync(key, sha, length, cancellationToken).ConfigureAwait(false);
            }

            return new PutObjectResult(key, PutOutcome.Created, sha, length, KeyId, EncryptionScheme.ProviderSse);
        }
        finally
        {
            if (!completed)
            {
                await AbortQuietlyAsync(physical, upload.UploadId).ConfigureAwait(false);
            }
        }
    }

    private async Task AbortQuietlyAsync(string physical, string uploadId)
    {
        try
        {
            await _client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest { BucketName = _options.Bucket, Key = physical, UploadId = uploadId },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (AmazonServiceException)
        {
            // Incomplete uploads are also expired by the bucket lifecycle rule (E19-T02).
        }
    }

    private async Task<IReadOnlyList<string>> VersioningResidualsAsync(ObjectPrefix prefix, CancellationToken cancellationToken)
    {
        try
        {
            var versioning = await _client.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = _options.Bucket }, cancellationToken).ConfigureAwait(false);
            var status = versioning.VersioningConfig?.Status;
            return status is null || status == VersionStatus.Off
                ? []
                : [$"{prefix}: bucket versioning is {status}; non-current versions are not removed by this provider."];
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.Forbidden)
        {
            return [$"{prefix}: bucket versioning status could not be read ({(int)ex.StatusCode})."];
        }
    }

    private void ApplyEncryption(PutObjectRequest request)
    {
        if (!string.IsNullOrEmpty(_options.ServerSideEncryption))
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(_options.ServerSideEncryption);
        }
    }

    private void ApplyEncryption(InitiateMultipartUploadRequest request)
    {
        if (!string.IsNullOrEmpty(_options.ServerSideEncryption))
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(_options.ServerSideEncryption);
        }
    }

    private static bool IsWriteConflict(AmazonS3Exception ex) =>
        ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;

    private string PhysicalKey(ObjectKey key) => Physical(_prefix, key.Value);

    private static AmazonS3Client CreateClient(S3ObjectStoreOptions? options, Uri? serviceUrl)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceUrl, nameof(options.ServiceUrl));
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl.ToString(),
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            UseHttp = serviceUrl.Scheme == Uri.UriSchemeHttp,
            // Checksums are sent explicitly (SHA-256); keep the SDK from adding CRC trailers some S3 servers reject.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }
}
