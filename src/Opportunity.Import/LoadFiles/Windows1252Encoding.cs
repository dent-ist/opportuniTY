using System.Text;

namespace Opportunity.Import.LoadFiles;

/// <summary>
/// Windows-1252 implemented in code: the hosts run with invariant globalization and no code-page provider. Every byte
/// decodes (the five undefined bytes 0x81, 0x8D, 0x8F, 0x90, 0x9D map to the C1 control with the same value, as
/// Windows does); characters outside the code page encode as <c>?</c>.
/// </summary>
public sealed class Windows1252Encoding : Encoding
{
    public static readonly Windows1252Encoding Instance = new();

    // 0x80–0x9F.
    private static readonly char[] HighTable =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    private static readonly Dictionary<char, byte> HighReverse = HighTable
        .Select((c, i) => (c, b: (byte)(0x80 + i)))
        .ToDictionary(x => x.c, x => x.b);

    public override string WebName => "windows-1252";

    public override string EncodingName => "Western European (Windows)";

    public override bool IsSingleByte => true;

    public static bool TryEncode(char c, out byte value)
    {
        if (c < 0x80 || c is >= ' ' and <= 'ÿ')
        {
            value = (byte)c;
            return true;
        }

        return HighReverse.TryGetValue(c, out value);
    }

    public static char Decode(byte b) => b is >= 0x80 and < 0xA0 ? HighTable[b - 0x80] : (char)b;

    public override int GetByteCount(char[] chars, int index, int count) => count;

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
        GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));

    public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        if (bytes.Length < chars.Length)
        {
            throw new ArgumentException("Output buffer too small.", nameof(bytes));
        }

        for (int i = 0; i < chars.Length; i++)
        {
            bytes[i] = TryEncode(chars[i], out byte b) ? b : (byte)'?';
        }

        return chars.Length;
    }

    public override int GetCharCount(byte[] bytes, int index, int count) => count;

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
        GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex));

    public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        if (chars.Length < bytes.Length)
        {
            throw new ArgumentException("Output buffer too small.", nameof(chars));
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i] = Decode(bytes[i]);
        }

        return bytes.Length;
    }

    public override int GetMaxByteCount(int charCount) => charCount;

    public override int GetMaxCharCount(int byteCount) => byteCount;
}
