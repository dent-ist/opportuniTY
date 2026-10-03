using System.Text;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// Minimal ZIP writer with stored (uncompressed) entries and a fixed 1980-01-01 timestamp, so archives (zip,
/// docx, xlsx natives) are byte-stable across runtimes.
/// </summary>
public static class StoredZip
{
    private const ushort DosDate = (0 << 9) | (1 << 5) | 1; // 1980-01-01
    private const ushort DosTime = 0;

    public static byte[] Create(IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var central = new List<(byte[] Name, uint Crc, uint Size, uint Offset)>();
        foreach ((string name, byte[] data) in entries)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            uint crc = Crc32.Compute(data);
            uint offset = (uint)ms.Position;
            w.Write(0x04034B50u);
            w.Write((ushort)20);
            w.Write((ushort)0x0800);
            w.Write((ushort)0);
            w.Write(DosTime);
            w.Write(DosDate);
            w.Write(crc);
            w.Write((uint)data.Length);
            w.Write((uint)data.Length);
            w.Write((ushort)nameBytes.Length);
            w.Write((ushort)0);
            w.Write(nameBytes);
            w.Write(data);
            central.Add((nameBytes, crc, (uint)data.Length, offset));
        }

        uint centralOffset = (uint)ms.Position;
        foreach ((byte[] name, uint crc, uint size, uint offset) in central)
        {
            w.Write(0x02014B50u);
            w.Write((ushort)20);
            w.Write((ushort)20);
            w.Write((ushort)0x0800);
            w.Write((ushort)0);
            w.Write(DosTime);
            w.Write(DosDate);
            w.Write(crc);
            w.Write(size);
            w.Write(size);
            w.Write((ushort)name.Length);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write(0u);
            w.Write(offset);
            w.Write(name);
        }

        uint centralSize = (uint)ms.Position - centralOffset;
        w.Write(0x06054B50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)central.Count);
        w.Write((ushort)central.Count);
        w.Write(centralSize);
        w.Write(centralOffset);
        w.Write((ushort)0);
        w.Flush();
        return ms.ToArray();
    }
}
