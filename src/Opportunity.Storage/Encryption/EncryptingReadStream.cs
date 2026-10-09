using System.Security.Cryptography;

using Opportunity.Application.Storage;

namespace Opportunity.Storage.Encryption;

/// <summary>
/// Reads plaintext from <c>source</c> and yields the envelope ciphertext (<see cref="EnvelopeFormat"/>), holding two
/// chunks in memory. It hashes the plaintext as it goes and checks the caller's expected SHA-256 and length before it
/// yields the last chunk, so a provider never receives a complete object whose plaintext is wrong: the read throws
/// <see cref="ObjectIntegrityException"/> and the provider abandons the upload.
/// </summary>
internal sealed class EncryptingReadStream : Stream
{
    private readonly Stream _source;
    private readonly ObjectKey _key;
    private readonly AesGcm _aes;
    private readonly int _chunkSize;
    private readonly Sha256Digest? _expectedSha256;
    private readonly long? _expectedLength;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[] _current;
    private byte[] _next;
    private readonly byte[] _output;
    private int _currentLength;
    private int _outputOffset;
    private int _outputLength;
    private long _chunkIndex;
    private bool _primed;
    private bool _finished;

    public EncryptingReadStream(
        Stream source, ObjectKey key, byte[] header, byte[] objectKey, Sha256Digest? expectedSha256, long? expectedLength)
    {
        _source = source;
        _key = key;
        _aes = new AesGcm(objectKey, EnvelopeFormat.TagSize);
        _chunkSize = 1 << header[9];
        _expectedSha256 = expectedSha256;
        _expectedLength = expectedLength;
        _current = new byte[_chunkSize];
        _next = new byte[_chunkSize];
        _output = new byte[Math.Max(EnvelopeFormat.HeaderSize, _chunkSize + EnvelopeFormat.TagSize)];
        header.CopyTo(_output, 0);
        _outputLength = EnvelopeFormat.HeaderSize;
    }

    /// <summary>The plaintext SHA-256, once the whole source was read.</summary>
    public Sha256Digest? PlaintextSha256 { get; private set; }

    public long PlaintextLength { get; private set; }

    /// <summary>Set when the plaintext failed the caller's expectations (also thrown from the read).</summary>
    public ObjectIntegrityException? IntegrityFailure { get; private set; }

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
        if (_outputOffset == _outputLength && !_finished)
        {
            if (!_primed)
            {
                _currentLength = Fill(_current);
                _primed = true;
            }

            Seal(Fill(_next));
        }

        return Drain(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_outputOffset == _outputLength && !_finished)
        {
            if (!_primed)
            {
                _currentLength = await FillAsync(_current, cancellationToken).ConfigureAwait(false);
                _primed = true;
            }

            Seal(await FillAsync(_next, cancellationToken).ConfigureAwait(false));
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
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Drain(Span<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _outputLength - _outputOffset);
        _output.AsSpan(_outputOffset, n).CopyTo(buffer);
        _outputOffset += n;
        return n;
    }

    /// <summary>Seals the current chunk; <paramref name="nextLength"/> == 0 means it is the last one.</summary>
    private void Seal(int nextLength)
    {
        var last = nextLength == 0;
        var plaintext = _current.AsSpan(0, _currentLength);
        _hash.AppendData(plaintext);
        PlaintextLength += _currentLength;
        if (last)
        {
            PlaintextSha256 = Sha256Digest.FromBytes(_hash.GetHashAndReset());
            if (_expectedLength is { } length && length != PlaintextLength)
            {
                throw Fail($"expected {length} bytes, received {PlaintextLength}.");
            }

            if (_expectedSha256 is { } sha && sha != PlaintextSha256)
            {
                throw Fail("SHA-256 of the received bytes does not match the expected value.");
            }
        }

        Span<byte> nonce = stackalloc byte[EnvelopeFormat.NonceSize];
        EnvelopeFormat.Nonce(nonce, _chunkIndex, last);
        _aes.Encrypt(nonce, plaintext, _output.AsSpan(0, _currentLength), _output.AsSpan(_currentLength, EnvelopeFormat.TagSize));
        _outputOffset = 0;
        _outputLength = _currentLength + EnvelopeFormat.TagSize;
        _chunkIndex++;
        (_current, _next) = (_next, _current);
        _currentLength = nextLength;
        _finished = last;
    }

    private ObjectIntegrityException Fail(string detail) => IntegrityFailure = new ObjectIntegrityException(_key, detail);

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
