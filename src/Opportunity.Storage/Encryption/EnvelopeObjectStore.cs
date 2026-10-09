using System.Security.Cryptography;

using Opportunity.Application.Keys;
using Opportunity.Application.Storage;

namespace Opportunity.Storage.Encryption;

/// <summary>
/// Client-side envelope encryption in front of any provider (ADR-011 §6, ADR-015 D10.3, <c>E05-T09</c>). Every new
/// workspace object (<c>ws/{id}/…</c>) is encrypted with the workspace's active data key from
/// <see cref="IWorkspaceDataKeyRing"/> in the <see cref="EnvelopeFormat"/>; reads decrypt with the data key version the
/// object's header names, so rotating or rewrapping keys never touches stored objects, and destroying the workspace's
/// keys makes every copy unreadable. Callers see plaintext semantics: SHA-256, lengths, byte ranges and write-once are
/// those of the plaintext; <see cref="PutObjectResult.KeyId"/> is the data key id and the scheme is
/// <see cref="EncryptionScheme.Envelope"/>.
/// </summary>
/// <remarks>
/// Installation objects (<c>sys/</c>) are left to the provider (SSE). Objects written before envelope encryption was
/// enabled have no envelope header and are read as they are. Listings report stored (ciphertext) sizes. Presigned
/// URLs would hand the browser ciphertext, so this store reports <see cref="ObjectDeliveryMode.Stream"/> and the
/// gateway streams every download through the API.
/// </remarks>
public sealed class EnvelopeObjectStore : IObjectStore, IObjectUrlSigner
{
    private readonly ObjectStoreBase _inner;
    private readonly IWorkspaceDataKeyRing _keys;
    private readonly int _chunkSizeLog2;

    public EnvelopeObjectStore(ObjectStoreBase inner, IWorkspaceDataKeyRing keys, int chunkSizeLog2 = EnvelopeFormat.DefaultChunkSizeLog2)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSizeLog2, EnvelopeFormat.MinChunkSizeLog2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSizeLog2, EnvelopeFormat.MaxChunkSizeLog2);
        _inner = inner;
        _keys = keys;
        _chunkSizeLog2 = chunkSizeLog2;
    }

    public ObjectDeliveryMode DeliveryMode => ObjectDeliveryMode.Stream;

    public async Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        options ??= new PutObjectOptions();
        if (key.WorkspaceId is not { } workspaceId)
        {
            return await _inner.PutAsync(key, content, options, cancellationToken).ConfigureAwait(false);
        }

        if (key.ContentSha256 is { } fromKey && options.ExpectedSha256 is { } fromOptions && fromKey != fromOptions)
        {
            throw new ArgumentException("ExpectedSha256 contradicts the content-addressed key.", nameof(options));
        }

        var expectedSha256 = options.ExpectedSha256 ?? key.ContentSha256;
        if (await _inner.HeadAsync(key, cancellationToken).ConfigureAwait(false) is not null)
        {
            var (sha, length) = await HashAsync(content, cancellationToken).ConfigureAwait(false);
            Verify(key, options, expectedSha256, sha, length);
            return await ResolveExistingAsync(key, sha, length, cancellationToken).ConfigureAwait(false);
        }

        var dataKey = await _keys.GetCurrentAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var header = EnvelopeFormat.CreateHeader(workspaceId, dataKey.Version, _chunkSizeLog2);
        var objectKey = EnvelopeFormat.DeriveObjectKey(dataKey.Material, EnvelopeFormat.Parse(key, header), key);
        var encrypting = new EncryptingReadStream(content, key, header, objectKey, expectedSha256, options.ExpectedLength);
        await using (encrypting.ConfigureAwait(false))
        {
            try
            {
                await _inner.PutCiphertextAsync(key, encrypting, new PutObjectOptions
                {
                    ContentType = options.ContentType,
                    ExpectedLength = options.ExpectedLength is { } l ? EnvelopeFormat.CiphertextLength(l, 1 << _chunkSizeLog2) : null,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (encrypting.IntegrityFailure is { } failure)
            {
                // Providers may wrap what the source stream threw; the plaintext check is the real cause.
                throw failure;
            }
            catch (ObjectAlreadyExistsException) when (encrypting.PlaintextSha256 is { } sha)
            {
                // Lost a race with a concurrent writer of the same key.
                return await ResolveExistingAsync(key, sha, encrypting.PlaintextLength, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(objectKey);
            }

            return new PutObjectResult(key, PutOutcome.Created, encrypting.PlaintextSha256!.Value, encrypting.PlaintextLength, dataKey.KeyId, EncryptionScheme.Envelope);
        }
    }

    public async Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.WorkspaceId is null)
        {
            return await _inner.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        }

        return range is { } r
            ? await OpenRangeAsync(key, r, cancellationToken).ConfigureAwait(false)
            : await OpenWholeAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var info = await _inner.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        if (info is null || key.WorkspaceId is null || info.Length < EnvelopeFormat.HeaderSize + EnvelopeFormat.TagSize)
        {
            return info;
        }

        var header = await ReadHeaderAsync(key, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return info;
        }

        return info with
        {
            Length = PlaintextLength(key, info.Length, header),
            Sha256 = null, // The provider hashed ciphertext; the registry holds the plaintext hash.
            KeyId = WorkspaceDataKey.FormatKeyId(header.KeyVersion),
            EncryptionScheme = EncryptionScheme.Envelope,
        };
    }

    public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
        _inner.ListPrefixAsync(prefix, cancellationToken);

    public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
        _inner.DeletePrefixAsync(prefix, cancellationToken);

    public Task<PresignedObjectUrl> PresignGetAsync(ObjectKey key, PresignGetOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Envelope-encrypted objects are streamed through the API, never presigned.");

    public Task<PresignedObjectUrl> PresignPutAsync(ObjectKey key, PresignPutOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Envelope-encrypted stores accept uploads through the API only.");

    private async Task<Stream> OpenWholeAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var source = await _inner.OpenReadAsync(key, null, cancellationToken).ConfigureAwait(false);
        try
        {
            var head = new byte[EnvelopeFormat.HeaderSize];
            var read = await FillAsync(source, head, cancellationToken).ConfigureAwait(false);
            if (read < EnvelopeFormat.HeaderSize || !EnvelopeFormat.HasMagic(head))
            {
                return new PrefixedStream(head.AsMemory(0, read), source);
            }

            var header = EnvelopeFormat.Parse(key, head);
            var objectKey = await ObjectKeyAsync(key, header, cancellationToken).ConfigureAwait(false);
            try
            {
                return new DecryptingReadStream(source, key, objectKey, header.ChunkSize, 0, null, 0, null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(objectKey);
            }
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Stream> OpenRangeAsync(ObjectKey key, ByteRange range, CancellationToken cancellationToken)
    {
        var info = await _inner.HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw new ObjectNotFoundException(key);
        var header = info.Length >= EnvelopeFormat.HeaderSize + EnvelopeFormat.TagSize
            ? await ReadHeaderAsync(key, cancellationToken).ConfigureAwait(false)
            : null;
        if (header is null)
        {
            return await _inner.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        }

        var plaintextLength = PlaintextLength(key, info.Length, header);
        if (range.Offset >= plaintextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "The range starts at or after the end of the object.");
        }

        var end = range.Length is { } l ? Math.Min(plaintextLength, range.Offset + l) : plaintextLength;
        var chunk = header.ChunkSize;
        var sealedChunk = (long)chunk + EnvelopeFormat.TagSize;
        var firstChunk = range.Offset / chunk;
        var lastReadChunk = (end - 1) / chunk;
        var lastChunk = EnvelopeFormat.ChunkCount(plaintextLength, chunk) - 1;
        var cipherOffset = EnvelopeFormat.HeaderSize + (firstChunk * sealedChunk);
        var cipherLength = Math.Min(info.Length - cipherOffset, (lastReadChunk - firstChunk + 1) * sealedChunk);

        var objectKey = await ObjectKeyAsync(key, header, cancellationToken).ConfigureAwait(false);
        try
        {
            var source = await _inner.OpenReadAsync(key, new ByteRange(cipherOffset, cipherLength), cancellationToken).ConfigureAwait(false);
            return new DecryptingReadStream(
                source, key, objectKey, chunk, firstChunk, lastChunk, (int)(range.Offset - (firstChunk * chunk)), end - range.Offset);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(objectKey);
        }
    }

    /// <summary>The object's envelope header, or null when it is a plaintext (pre-encryption) object.</summary>
    private async Task<EnvelopeHeader?> ReadHeaderAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var stream = await _inner.OpenReadAsync(key, new ByteRange(0, EnvelopeFormat.HeaderSize), cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var head = new byte[EnvelopeFormat.HeaderSize];
            var read = await FillAsync(stream, head, cancellationToken).ConfigureAwait(false);
            return read == EnvelopeFormat.HeaderSize && EnvelopeFormat.HasMagic(head) ? EnvelopeFormat.Parse(key, head) : null;
        }
    }

    private async Task<byte[]> ObjectKeyAsync(ObjectKey key, EnvelopeHeader header, CancellationToken cancellationToken)
    {
        var dataKey = await _keys.GetAsync(header.WorkspaceId, header.KeyVersion, cancellationToken).ConfigureAwait(false);
        return EnvelopeFormat.DeriveObjectKey(dataKey.Material, header, key);
    }

    /// <summary>Write-once against an existing object: identical plaintext is a no-op, anything else a conflict.</summary>
    private async Task<PutObjectResult> ResolveExistingAsync(ObjectKey key, Sha256Digest incoming, long incomingLength, CancellationToken cancellationToken)
    {
        var info = await HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw new ObjectAlreadyExistsException(key);
        if (info.Length != incomingLength)
        {
            throw new ObjectAlreadyExistsException(key);
        }

        var stream = await OpenReadAsync(key, null, cancellationToken).ConfigureAwait(false);
        Sha256Digest existing;
        await using (stream.ConfigureAwait(false))
        {
            existing = await Sha256Digest.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (existing != incoming)
        {
            throw new ObjectAlreadyExistsException(key);
        }

        return new PutObjectResult(key, PutOutcome.AlreadyExisted, incoming, incomingLength, info.KeyId ?? _inner.KeyId, info.EncryptionScheme);
    }

    private static long PlaintextLength(ObjectKey key, long storedLength, EnvelopeHeader header) =>
        EnvelopeFormat.TryPlaintextLength(storedLength, header.ChunkSize, out var length)
            ? length
            : throw new ObjectIntegrityException(key, "the encrypted object has an impossible length (truncated or extended).");

    private static void Verify(ObjectKey key, PutObjectOptions options, Sha256Digest? expected, Sha256Digest actual, long length)
    {
        if (options.ExpectedLength is { } expectedLength && expectedLength != length)
        {
            throw new ObjectIntegrityException(key, $"expected {expectedLength} bytes, received {length}.");
        }

        if (expected is { } e && e != actual)
        {
            throw new ObjectIntegrityException(key, "SHA-256 of the received bytes does not match the expected value.");
        }
    }

    private static async Task<(Sha256Digest Sha256, long Length)> HashAsync(Stream content, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int n;
        while ((n = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            length += n;
        }

        return (Sha256Digest.FromBytes(hash.GetHashAndReset()), length);
    }

    private static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        int n;
        while (filled < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false)) > 0)
        {
            filled += n;
        }

        return filled;
    }

    /// <summary>Replays bytes already read from <c>inner</c> (a plaintext object's first bytes), then the rest.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefix.Length > 0)
            {
                return TakePrefix(buffer);
            }

            return inner.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _prefix.Length > 0 ? ValueTask.FromResult(TakePrefix(buffer.Span)) : inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int TakePrefix(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }
    }
}
