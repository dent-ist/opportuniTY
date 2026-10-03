using System.Buffers.Binary;
using System.Text;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// Hand-encoded, fully deterministic blank page rasters (no imaging library, so bytes never change with a runtime
/// update). Each file carries its image key in a comment/description field so pages are distinguishable.
/// <list type="bullet">
/// <item>TIFF: bitonal CCITT Group 4, 2550 × 3300 at 300 DPI (US Letter); a white row is the single G4 code V0.</item>
/// <item>JPG: baseline grayscale, white, 850 × 1100 at 100 DPI (DC-only blocks with minimal Huffman tables).</item>
/// <item>PNG: 1-bit palette, white, 850 × 1100 at 100 DPI (fixed-Huffman deflate of zero runs).</item>
/// </list>
/// </summary>
public static class PageImages
{
    public const int TiffWidth = 2550;
    public const int TiffHeight = 3300;
    public const int TiffDpi = 300;
    public const int RasterWidth = 850;
    public const int RasterHeight = 1100;
    public const int RasterDpi = 100;

    /// <summary>Single- or multi-page G4 TIFF: one frame per description.</summary>
    public static byte[] Tiff(IReadOnlyList<string> pageDescriptions, int width = TiffWidth, int height = TiffHeight, int dpi = TiffDpi)
    {
        ArgumentNullException.ThrowIfNull(pageDescriptions);
        ArgumentOutOfRangeException.ThrowIfZero(pageDescriptions.Count);
        byte[] strip = G4BlankStrip(height);
        bool multi = pageDescriptions.Count > 1;
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("II"u8);
        w.Write((ushort)42);
        long nextPointer = ms.Position;
        w.Write(0u);
        for (int page = 0; page < pageDescriptions.Count; page++)
        {
            uint stripOffset = (uint)ms.Position;
            w.Write(strip);
            byte[] desc = Encoding.ASCII.GetBytes(pageDescriptions[page] + "\0");
            Align(w);
            uint descOffset = (uint)ms.Position;
            w.Write(desc);
            Align(w);
            uint resOffset = (uint)ms.Position;
            w.Write((uint)dpi);
            w.Write(1u);

            Align(w);
            uint ifdOffset = (uint)ms.Position;
            Patch(ms, nextPointer, ifdOffset);
            var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
            {
                (254, 4, 1, multi ? 2u : 0u),
                (256, 4, 1, (uint)width),
                (257, 4, 1, (uint)height),
                (258, 3, 1, 1),
                (259, 3, 1, 4),
                (262, 3, 1, 0),
                (270, 2, (uint)desc.Length, descOffset),
                (273, 4, 1, stripOffset),
                (277, 3, 1, 1),
                (278, 4, 1, (uint)height),
                (279, 4, 1, (uint)strip.Length),
                (282, 5, 1, resOffset),
                (283, 5, 1, resOffset),
                (293, 4, 1, 0),
                (296, 3, 1, 2),
            };
            if (multi)
            {
                entries.Add((297, 3, 2, (uint)page | ((uint)pageDescriptions.Count << 16)));
            }

            w.Write((ushort)entries.Count);
            foreach ((ushort tag, ushort type, uint count, uint value) in entries)
            {
                w.Write(tag);
                w.Write(type);
                w.Write(count);
                w.Write(value);
            }

            nextPointer = ms.Position;
            w.Write(0u);
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>White baseline grayscale JPEG.</summary>
    public static byte[] Jpeg(string comment, int width = RasterWidth, int height = RasterHeight, int dpi = RasterDpi)
    {
        ArgumentNullException.ThrowIfNull(comment);
        using var ms = new MemoryStream();
        void Marker(byte code, ReadOnlySpan<byte> payload)
        {
            ms.WriteByte(0xFF);
            ms.WriteByte(code);
            Span<byte> len = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)(payload.Length + 2));
            ms.Write(len);
            ms.Write(payload);
        }

        ms.Write([0xFF, 0xD8]);
        byte[] jfif = [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 1, (byte)(dpi >> 8), (byte)dpi, (byte)(dpi >> 8), (byte)dpi, 0, 0];
        Marker(0xE0, jfif);
        Marker(0xFE, Encoding.ASCII.GetBytes(comment));
        byte[] dqt = new byte[65];
        dqt.AsSpan(1).Fill(1);
        Marker(0xDB, dqt);
        Marker(0xC0, [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);

        // DC table 0: two 2-bit codes, 00 = category 0, 01 = category 10. AC table 0: one 1-bit code, 0 = EOB.
        byte[] dc = new byte[1 + 16 + 2];
        dc[0] = 0x00;
        dc[2] = 2;
        dc[17] = 0;
        dc[18] = 10;
        Marker(0xC4, dc);
        byte[] ac = new byte[1 + 16 + 1];
        ac[0] = 0x10;
        ac[1] = 1;
        ac[17] = 0x00;
        Marker(0xC4, ac);
        Marker(0xDA, [1, 1, 0x00, 0, 63, 0]);

        // White: level-shifted 127 → DC coefficient 8 × 127 = 1016 (category 10). Later blocks repeat it (diff 0).
        var bits = new BitWriterMsb(ms, stuffJpeg: true);
        int blocks = ((width + 7) / 8) * ((height + 7) / 8);
        bits.Write(0b01, 2);
        bits.Write(1016, 10);
        bits.Write(0, 1);
        for (int i = 1; i < blocks; i++)
        {
            bits.Write(0b000, 3);
        }

        bits.PadWithOnes();
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    /// <summary>White 1-bit palette PNG.</summary>
    public static byte[] Png(string text, int width = RasterWidth, int height = RasterHeight, int dpi = RasterDpi)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 1;
        ihdr[9] = 3;
        PngChunk(ms, "IHDR", ihdr);
        PngChunk(ms, "PLTE", [0xFF, 0xFF, 0xFF]);
        byte[] phys = new byte[9];
        uint ppm = (uint)Math.Round(dpi / 0.0254);
        BinaryPrimitives.WriteUInt32BigEndian(phys, ppm);
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), ppm);
        phys[8] = 1;
        PngChunk(ms, "pHYs", phys);
        PngChunk(ms, "tEXt", Encoding.Latin1.GetBytes("Comment\0" + text));
        long raw = (long)height * (1 + ((width + 7) / 8));
        PngChunk(ms, "IDAT", ZlibZeros(raw));
        PngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>A zlib stream (fixed-Huffman deflate) of <paramref name="count"/> zero bytes.</summary>
    public static byte[] ZlibZeros(long count)
    {
        using var ms = new MemoryStream();
        ms.Write([0x78, 0x01]);
        var bits = new BitWriterLsb(ms);
        bits.Write(1, 1);
        bits.Write(1, 2);
        long remaining = count;
        if (remaining > 0)
        {
            bits.WriteHuffman(0x30, 8);
            remaining--;
        }

        while (remaining > 0)
        {
            if (remaining < 3)
            {
                bits.WriteHuffman(0x30, 8);
                remaining--;
                continue;
            }

            int length = (int)Math.Min(258, remaining);
            if (remaining - length is 1 or 2)
            {
                length -= 3;
            }

            WriteLength(bits, length);
            bits.WriteHuffman(0, 5);
            remaining -= length;
        }

        bits.WriteHuffman(0, 7);
        bits.Flush();
        uint adler = ((uint)(count % 65521) << 16) | 1u;
        Span<byte> a = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(a, adler);
        ms.Write(a);
        return ms.ToArray();
    }

    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static void WriteLength(BitWriterLsb bits, int length)
    {
        int i = LengthBase.Length - 1;
        while (LengthBase[i] > length)
        {
            i--;
        }

        int symbol = 257 + i;
        if (symbol <= 279)
        {
            bits.WriteHuffman((uint)(symbol - 256), 7);
        }
        else
        {
            bits.WriteHuffman((uint)(0xC0 + symbol - 280), 8);
        }

        if (LengthExtra[i] > 0)
        {
            bits.Write((uint)(length - LengthBase[i]), LengthExtra[i]);
        }
    }

    private static void PngChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
        s.Write(buf);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    /// <summary>G4 strip of an all-white page: one V0 code ("1") per row, then EOFB (two EOL codes).</summary>
    private static byte[] G4BlankStrip(int rows)
    {
        using var ms = new MemoryStream();
        var bits = new BitWriterMsb(ms, stuffJpeg: false);
        for (int i = 0; i < rows; i++)
        {
            bits.Write(1, 1);
        }

        bits.Write(0b000000000001, 12);
        bits.Write(0b000000000001, 12);
        bits.PadWithZeros();
        return ms.ToArray();
    }

    private static void Align(BinaryWriter w)
    {
        if ((w.BaseStream.Position & 1) != 0)
        {
            w.Write((byte)0);
        }
    }

    private static void Patch(MemoryStream ms, long position, uint value)
    {
        long here = ms.Position;
        ms.Position = position;
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        ms.Write(b);
        ms.Position = here;
    }

    private sealed class BitWriterMsb(Stream output, bool stuffJpeg)
    {
        private int _acc;
        private int _count;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | (int)((value >> i) & 1);
                if (++_count == 8)
                {
                    Emit();
                }
            }
        }

        public void PadWithOnes()
        {
            while (_count != 0)
            {
                Write(1, 1);
            }
        }

        public void PadWithZeros()
        {
            while (_count != 0)
            {
                Write(0, 1);
            }
        }

        private void Emit()
        {
            output.WriteByte((byte)_acc);
            if (stuffJpeg && _acc == 0xFF)
            {
                output.WriteByte(0);
            }

            _acc = 0;
            _count = 0;
        }
    }

    private sealed class BitWriterLsb(Stream output)
    {
        private uint _acc;
        private int _count;

        /// <summary>Writes a value LSB first (deflate header fields and extra bits).</summary>
        public void Write(uint value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                Put((value >> i) & 1);
            }
        }

        /// <summary>Writes a Huffman code MSB first, as deflate requires.</summary>
        public void WriteHuffman(uint code, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                Put((code >> i) & 1);
            }
        }

        public void Flush()
        {
            if (_count > 0)
            {
                output.WriteByte((byte)_acc);
                _acc = 0;
                _count = 0;
            }
        }

        private void Put(uint bit)
        {
            _acc |= bit << _count;
            if (++_count == 8)
            {
                output.WriteByte((byte)_acc);
                _acc = 0;
                _count = 0;
            }
        }
    }
}

/// <summary>CRC-32 (IEEE 802.3), as used by PNG and ZIP.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
