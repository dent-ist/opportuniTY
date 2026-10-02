using System.Security.Cryptography;

namespace Opportunity.Application.Storage;

/// <summary>
/// Forward-only stream that hashes what it reads and throws <see cref="ObjectIntegrityException"/> at end of stream when
/// the SHA-256 or length differs from the recorded value (ADR-011 §2.5 chain-of-custody re-verification).
/// </summary>
public sealed class VerifyingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly ObjectKey _key;
    private readonly Sha256Digest _expected;
    private readonly long? _expectedLength;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _read;
    private bool _verified;

    public VerifyingReadStream(Stream inner, ObjectKey key, Sha256Digest expected, long? expectedLength = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(key);
        _inner = inner;
        _key = key;
        _expected = expected;
        _expectedLength = expectedLength;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        return Track(buffer[..n]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Track(buffer.Span[..n]);
    }

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
            _inner.Dispose();
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        _hash.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Track(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
        {
            _hash.AppendData(data);
            _read += data.Length;
            return data.Length;
        }

        if (!_verified)
        {
            _verified = true;
            if (_expectedLength is { } length && length != _read)
            {
                throw new ObjectIntegrityException(_key, $"expected {length} bytes, read {_read}.");
            }

            var actual = Sha256Digest.FromBytes(_hash.GetHashAndReset());
            if (actual != _expected)
            {
                throw new ObjectIntegrityException(_key, "SHA-256 does not match the recorded value.");
            }
        }

        return 0;
    }
}

public static class ObjectStoreExtensions
{
    /// <summary>Opens the object through a <see cref="VerifyingReadStream"/> (export/production workers, scrub job).</summary>
    public static async Task<Stream> OpenReadVerifiedAsync(
        this IObjectStore store,
        ObjectKey key,
        Sha256Digest expectedSha256,
        long? expectedLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var inner = await store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false);
        return new VerifyingReadStream(inner, key, expectedSha256, expectedLength);
    }
}
