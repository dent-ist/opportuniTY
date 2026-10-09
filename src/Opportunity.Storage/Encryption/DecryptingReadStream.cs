using System.Security.Cryptography;

using Opportunity.Application.Storage;

namespace Opportunity.Storage.Encryption;

/// <summary>
/// Decrypts envelope chunks from <c>source</c> (positioned at the start of chunk <c>firstChunk</c>) and yields
/// plaintext, dropping <c>skip</c> bytes of the first chunk and stopping after <c>limit</c> bytes. Whether a chunk is
/// the last one comes from the object's length when known (range reads), otherwise from reading one chunk ahead. Any
/// chunk that does not authenticate, a short chunk and a missing last chunk throw <see cref="ObjectIntegrityException"/>.
/// </summary>
internal sealed class DecryptingReadStream : Stream
{
    private readonly Stream _source;
    private readonly ObjectKey _key;
    private readonly AesGcm _aes;
    private readonly int _sealedSize;
    private readonly long? _lastChunk;
    private byte[] _current;
    private byte[] _next;
    private readonly byte[] _plaintext;
    private int _currentLength;
    private int _nextLength;
    private bool _haveNext;
    private int _offset;
    private int _length;
    private long _chunkIndex;
    private int _skip;
    private long? _remaining;
    private bool _finished;

    public DecryptingReadStream(Stream source, ObjectKey key, byte[] objectKey, int chunkSize, long firstChunk, long? lastChunk, int skip, long? limit)
    {
        _source = source;
        _key = key;
        _aes = new AesGcm(objectKey, EnvelopeFormat.TagSize);
        _sealedSize = chunkSize + EnvelopeFormat.TagSize;
        _chunkIndex = firstChunk;
        _lastChunk = lastChunk;
        _skip = skip;
        _remaining = limit;
        _current = new byte[_sealedSize];
        _next = new byte[_sealedSize];
        _plaintext = new byte[chunkSize];
    }

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
        while (_offset == _length && !Done)
        {
            if (_haveNext)
            {
                Swap();
            }
            else
            {
                _currentLength = Fill(_current);
            }

            bool last;
            if (_lastChunk is { } lastChunk)
            {
                last = _chunkIndex == lastChunk;
            }
            else
            {
                _nextLength = Fill(_next);
                _haveNext = true;
                last = _nextLength == 0;
            }

            Open(last);
        }

        return Drain(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset == _length && !Done)
        {
            if (_haveNext)
            {
                Swap();
            }
            else
            {
                _currentLength = await FillAsync(_current, cancellationToken).ConfigureAwait(false);
            }

            bool last;
            if (_lastChunk is { } lastChunk)
            {
                last = _chunkIndex == lastChunk;
            }
            else
            {
                _nextLength = await FillAsync(_next, cancellationToken).ConfigureAwait(false);
                _haveNext = true;
                last = _nextLength == 0;
            }

            Open(last);
        }

        return Drain(buffer.Span);
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
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plaintext);
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _aes.Dispose();
        CryptographicOperations.ZeroMemory(_plaintext);
        await _source.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private bool Done => _finished || _remaining == 0;

    private void Swap()
    {
        (_current, _next) = (_next, _current);
        _currentLength = _nextLength;
        _haveNext = false;
    }

    private void Open(bool last)
    {
        if (_currentLength < EnvelopeFormat.TagSize || (!last && _currentLength != _sealedSize))
        {
            throw new ObjectIntegrityException(_key, "the encrypted object is truncated.");
        }

        var plaintextLength = _currentLength - EnvelopeFormat.TagSize;
        Span<byte> nonce = stackalloc byte[EnvelopeFormat.NonceSize];
        EnvelopeFormat.Nonce(nonce, _chunkIndex, last);
        try
        {
            _aes.Decrypt(
                nonce,
                _current.AsSpan(0, plaintextLength),
                _current.AsSpan(plaintextLength, EnvelopeFormat.TagSize),
                _plaintext.AsSpan(0, plaintextLength));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new ObjectIntegrityException(_key, "an encrypted chunk failed authentication (tampered, truncated or wrong key).");
        }

        _offset = Math.Min(_skip, plaintextLength);
        _skip = 0;
        _length = plaintextLength;
        if (_remaining is { } remaining && _length - _offset > remaining)
        {
            _length = _offset + (int)remaining;
        }

        _chunkIndex++;
        _finished = last;
    }

    private int Drain(Span<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _length - _offset);
        _plaintext.AsSpan(_offset, n).CopyTo(buffer);
        _offset += n;
        if (_remaining is { } remaining)
        {
            _remaining = remaining - n;
        }

        return n;
    }

    private int Fill(byte[] buffer)
    {
        var filled = 0;
        int n;
        while (filled < buffer.Length && (n = _source.Read(buffer, filled, buffer.Length - filled)) > 0)
        {
            filled += n;
        }

        return filled;
    }

    private async ValueTask<int> FillAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        int n;
        while (filled < buffer.Length
            && (n = await _source.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken).ConfigureAwait(false)) > 0)
        {
            filled += n;
        }

        return filled;
    }
}
