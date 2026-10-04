using System.Buffers.Binary;
using System.Text;

namespace Opportunity.Production.Exports;

/// <summary>
/// Writes a ZIP archive to a forward-only stream (an HTTP response) without buffering any entry: every entry is stored
/// (no compression; volumes are mostly already-compressed natives and images), followed by a data descriptor with its
/// CRC-32, and every size and offset uses the ZIP64 extensions, so entries and archives above 4 GiB and more than 65,535
/// entries are fine. Only asynchronous writes reach the output. Entry names are UTF-8 (general-purpose flag bit 11).
/// </summary>
public sealed class StreamingZipWriter(Stream output)
{
    private const ushort Version = 45;
    private const ushort Flags = 0x0808;
    private const ushort Zip64ExtraId = 0x0001;

    // 1980-01-01 00:00: entries carry no wall-clock time.
    private const ushort DosTime = 0;
    private const ushort DosDate = (0 << 9) | (1 << 5) | 1;

    private readonly Stream _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly List<(byte[] Name, uint Crc, long Size, long Offset)> _entries = [];
    private long _position;

    /// <summary>Copies <paramref name="content"/> to the archive as entry <paramref name="name"/> (use <c>/</c> separators).</summary>
    public async Task AddEntryAsync(string name, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var offset = _position;

        var header = new byte[30 + nameBytes.Length + 20];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 20);
        nameBytes.CopyTo(span[30..]);
        var extra = span[(30 + nameBytes.Length)..];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, Zip64ExtraId);
        BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
        BinaryPrimitives.WriteUInt64LittleEndian(extra[4..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(extra[12..], 0);
        await WriteAsync(header, cancellationToken).ConfigureAwait(false);

        var crc = Crc32.Initial;
        long size = 0;
        var buffer = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            crc = Crc32.Update(crc, buffer.AsSpan(0, read));
            size += read;
            await WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var final = Crc32.Finish(crc);
        var descriptor = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x08074b50);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), final);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), (ulong)size);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(16), (ulong)size);
        await WriteAsync(descriptor, cancellationToken).ConfigureAwait(false);
        _entries.Add((nameBytes, final, size, offset));
    }

    /// <summary>Writes the central directory and the ZIP64 end records. Call once, after the last entry.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        var directoryOffset = _position;
        foreach (var (name, crc, size, offset) in _entries)
        {
            var record = new byte[46 + name.Length + 28];
            var span = record.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], uint.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[30..], 28);
            BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[36..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[38..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], uint.MaxValue);
            name.CopyTo(span[46..]);
            var extra = span[(46 + name.Length)..];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, Zip64ExtraId);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 24);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[4..], (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[12..], (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[20..], (ulong)offset);
            await WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }

        var directorySize = _position - directoryOffset;
        var zip64End = _position;
        var end = new byte[56 + 20 + 22];
        var e = end.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(e[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(e[12..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(e[14..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(e[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(e[20..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(e[24..], (ulong)_entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(e[32..], (ulong)_entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(e[40..], (ulong)directorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(e[48..], (ulong)directoryOffset);
        var locator = e[56..];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[4..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)zip64End);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        var eocd = e[76..];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[20..], 0);
        await WriteAsync(end, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        _position += bytes.Length;
    }

    /// <summary>CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) as ZIP uses it.</summary>
    private static class Crc32
    {
        public const uint Initial = 0xFFFFFFFF;

        private static readonly uint[] Table = Build();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}
