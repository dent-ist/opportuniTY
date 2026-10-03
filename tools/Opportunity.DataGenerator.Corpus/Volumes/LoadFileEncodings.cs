using System.Buffers;
using System.Text;

namespace Opportunity.DataGenerator.Corpus.Volumes;

/// <summary>
/// Encoders for load files. Windows-1252 is implemented here (no code-page provider) so output does not depend
/// on the runtime's globalization mode. Callers never split a surrogate pair across calls.
/// </summary>
public static class LoadFileEncodings
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16 = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);

    // Windows-1252 0x80–0x9F; '\0' marks the five undefined bytes.
    private static readonly char[] HighTable =
    [
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
    ];

    private static readonly Dictionary<char, byte> HighReverse = HighTable
        .Select((c, i) => (c, b: (byte)(0x80 + i)))
        .Where(x => x.c != '\0')
        .ToDictionary(x => x.c, x => x.b);

    public static string Name(LoadFileEncoding encoding) => encoding switch
    {
        LoadFileEncoding.Utf8 => "utf-8",
        LoadFileEncoding.Utf8Bom => "utf-8-bom",
        LoadFileEncoding.Utf16Le => "utf-16le",
        _ => "windows-1252",
    };

    public static bool TryParse(string name, out LoadFileEncoding encoding)
    {
        foreach (LoadFileEncoding e in Enum.GetValues<LoadFileEncoding>())
        {
            if (string.Equals(Name(e), name, StringComparison.OrdinalIgnoreCase) || string.Equals(e.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                encoding = e;
                return true;
            }
        }

        encoding = default;
        return false;
    }

    public static ReadOnlySpan<byte> Preamble(LoadFileEncoding encoding) => encoding switch
    {
        LoadFileEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF],
        LoadFileEncoding.Utf16Le => [0xFF, 0xFE],
        _ => [],
    };

    public static void Write(LoadFileEncoding encoding, ReadOnlySpan<char> chars, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        int max = encoding switch
        {
            LoadFileEncoding.Windows1252 => chars.Length,
            LoadFileEncoding.Utf16Le => chars.Length * 2,
            _ => Utf8.GetMaxByteCount(chars.Length),
        };
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, max));
        try
        {
            int n = Encode(encoding, chars, buffer);
            output.Write(buffer, 0, n);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static byte[] GetBytes(LoadFileEncoding encoding, string value, bool withPreamble = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        if (withPreamble)
        {
            stream.Write(Preamble(encoding));
        }

        Write(encoding, value, stream);
        return stream.ToArray();
    }

    /// <summary>Decodes bytes, skipping the encoding's preamble when present (used by tests and tooling).</summary>
    public static string GetString(LoadFileEncoding encoding, ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> preamble = Preamble(encoding);
        if (preamble.Length > 0 && bytes.StartsWith(preamble))
        {
            bytes = bytes[preamble.Length..];
        }

        switch (encoding)
        {
            case LoadFileEncoding.Utf16Le:
                return Utf16.GetString(bytes);
            case LoadFileEncoding.Windows1252:
                var sb = new StringBuilder(bytes.Length);
                foreach (byte b in bytes)
                {
                    sb.Append(b is >= 0x80 and < 0xA0 ? (HighTable[b - 0x80] is var c and not '\0' ? c : (char)b) : (char)b);
                }

                return sb.ToString();
            default:
                return Utf8.GetString(bytes);
        }
    }

    private static int Encode(LoadFileEncoding encoding, ReadOnlySpan<char> chars, Span<byte> output)
    {
        switch (encoding)
        {
            case LoadFileEncoding.Utf16Le:
                return Utf16.GetBytes(chars, output);
            case LoadFileEncoding.Windows1252:
                int n = 0;
                for (int i = 0; i < chars.Length; i++)
                {
                    char c = chars[i];
                    if (c < 0x80 || c is >= ' ' and <= 'ÿ')
                    {
                        output[n++] = (byte)c;
                    }
                    else if (HighReverse.TryGetValue(c, out byte b))
                    {
                        output[n++] = b;
                    }
                    else
                    {
                        if (char.IsHighSurrogate(c) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                        {
                            i++;
                        }

                        output[n++] = (byte)'?';
                    }
                }

                return n;
            default:
                return Utf8.GetBytes(chars, output);
        }
    }
}
