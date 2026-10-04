namespace Opportunity.Production.Exports;

/// <summary>A forward-only read stream: <paramref name="prefix"/> bytes, then the inner stream.</summary>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : ConcatenatedReadStream([() => Task.FromResult<Stream>(new MemoryStream(prefix, writable: false)), () => Task.FromResult(inner)]);

/// <summary>
/// A forward-only read stream over parts opened one at a time, in order (each is disposed once read): the DAT is its
/// header followed by every chunk's part, streamed into object storage without a local copy.
/// </summary>
internal class ConcatenatedReadStream(IReadOnlyList<Func<Task<Stream>>> parts) : Stream
{
    private int _next;
    private Stream? _current;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (_current is null)
            {
                if (_next >= parts.Count)
                {
                    return 0;
                }

                _current = await parts[_next++]().ConfigureAwait(false);
            }

            var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _position += read;
                return read;
            }

            await _current.DisposeAsync().ConfigureAwait(false);
            _current = null;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (_current is not null)
        {
            await _current.DisposeAsync().ConfigureAwait(false);
            _current = null;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
            _current = null;
        }

        base.Dispose(disposing);
    }
}
