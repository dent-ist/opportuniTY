using Opportunity.Application.Import;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Import;
using Opportunity.Import.LoadFiles;
using Opportunity.Import.Mapping;

namespace Opportunity.Import.Jobs;

/// <summary>
/// Reads an import's DAT from object storage with the same parser settings in the preparation pass and in every chunk,
/// so row numbers, values and issues agree. A chunk reads only the bytes before the first data row plus its own byte
/// range (<see cref="ImportChunkRange"/>), never the rows before it.
/// </summary>
public static class ImportSource
{
    /// <summary>Reader settings of the profile, with the encoding decisions of the preparation pass when known.</summary>
    public static DatReaderOptions ReaderOptions(
        ImportProfileDefinition profile, ImportPreparation? preparation, List<MappingIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var options = LoadFileSettingsResolver.ReaderOptions(profile.LoadFile, issues);
        return new DatReaderOptions
        {
            Profile = options.Profile,
            EncodingOverride = preparation is null ? options.EncodingOverride : Enum.Parse<LoadFileEncodingKind>(preparation.DatEncoding),
            RowEncodingFallback = preparation?.EncodingFallback,
            HasHeader = options.HasHeader,
            ConvertNewlineCharacter = options.ConvertNewlineCharacter,
            MaxRejectedRows = null,
            ReturnRejectedRecords = true,
        };
    }

    public static Task<Stream> OpenAsync(IObjectStore store, ImportBatchRecord batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(batch);
        return store.OpenReadAsync(ObjectKey.Parse(batch.SourceObjectKey), null, cancellationToken);
    }

    /// <summary>The bytes before the first data row followed by <paramref name="range"/>'s bytes.</summary>
    public static Stream OpenChunk(IObjectStore store, ImportBatchRecord batch, ImportChunkRange range)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(range);
        var key = ObjectKey.Parse(batch.SourceObjectKey);
        var prefix = batch.Preparation!.DataOffset;
        var segments = new List<Func<CancellationToken, Task<Stream>>>();
        if (prefix > 0)
        {
            segments.Add(ct => store.OpenReadAsync(key, new ByteRange(0, prefix), ct));
        }

        if (range.ByteTo > range.ByteFrom)
        {
            segments.Add(ct => store.OpenReadAsync(key, new ByteRange(range.ByteFrom, range.ByteTo - range.ByteFrom), ct));
        }

        return new ConcatenatedStream(segments);
    }

    /// <summary>A forward-only stream over segments opened one after the other.</summary>
    private sealed class ConcatenatedStream(IReadOnlyList<Func<CancellationToken, Task<Stream>>> segments) : Stream
    {
        private int _next;
        private Stream? _current;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is null)
                {
                    if (_next >= segments.Count)
                    {
                        return 0;
                    }

                    _current = await segments[_next++](cancellationToken).ConfigureAwait(false);
                }

                var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0 || buffer.IsEmpty)
                {
                    return read;
                }

                await _current.DisposeAsync().ConfigureAwait(false);
                _current = null;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

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

            _next = segments.Count;
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _current?.Dispose();
                _current = null;
                _next = segments.Count;
            }

            base.Dispose(disposing);
        }
    }
}
