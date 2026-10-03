using System.Text;

namespace Opportunity.Import.LoadFiles;

/// <summary>Byte encodings accepted for DAT and extracted-text files (eDiscovery review §12).</summary>
public enum LoadFileEncodingKind
{
    /// <summary>UTF-8, with or without the <c>EF BB BF</c> byte-order mark.</summary>
    Utf8,

    /// <summary>UTF-16 little-endian ("Unicode" exports of common review/processing platforms), normally with the <c>FF FE</c> byte-order mark.</summary>
    Utf16LE,

    /// <summary>UTF-16 big-endian (<c>FE FF</c> byte-order mark).</summary>
    Utf16BE,

    /// <summary>Windows-1252 ("ANSI").</summary>
    Windows1252,
}

/// <summary>How the encoding of a file was decided.</summary>
public enum EncodingSource
{
    /// <summary>A byte-order mark at the start of the file.</summary>
    ByteOrderMark,

    /// <summary>Byte statistics of the leading sample (UTF-16 zero-byte pattern, UTF-8 validity).</summary>
    Heuristic,

    /// <summary>Explicitly chosen by the user (per DAT and per extracted-text set).</summary>
    Override,
}

/// <summary>The encoding a reader uses for a file and why.</summary>
/// <param name="Kind">Encoding used to decode the file.</param>
/// <param name="Source">How it was decided.</param>
/// <param name="PreambleLength">Bytes of byte-order mark skipped at the start of the file (0 when none).</param>
/// <param name="AsciiOnly">The sample held only 7-bit bytes, so UTF-8 and Windows-1252 decode it identically.</param>
/// <param name="InvalidUtf8Sequences">Invalid UTF-8 sequences seen in the sample (heuristic detection only).</param>
/// <param name="ValidUtf8MultiByteSequences">Valid multi-byte UTF-8 sequences seen in the sample (heuristic detection only).</param>
public sealed record DetectedEncoding(
    LoadFileEncodingKind Kind,
    EncodingSource Source,
    int PreambleLength,
    bool AsciiOnly = false,
    int InvalidUtf8Sequences = 0,
    int ValidUtf8MultiByteSequences = 0)
{
    public string Name => LoadFileEncodings.Name(Kind) + (PreambleLength > 0 ? " (BOM)" : "");

    /// <summary>True when decoding the file as <paramref name="declared"/> yields the same text as the detected encoding.</summary>
    public bool IsCompatibleWith(LoadFileEncodingKind declared) =>
        declared == Kind || (AsciiOnly && Kind is LoadFileEncodingKind.Utf8 or LoadFileEncodingKind.Windows1252
            && declared is LoadFileEncodingKind.Utf8 or LoadFileEncodingKind.Windows1252);
}

public static class LoadFileEncodings
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16LE = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16BE = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);

    public static string Name(LoadFileEncodingKind kind) => kind switch
    {
        LoadFileEncodingKind.Utf8 => "utf-8",
        LoadFileEncodingKind.Utf16LE => "utf-16le",
        LoadFileEncodingKind.Utf16BE => "utf-16be",
        _ => "windows-1252",
    };

    /// <summary>Parses <c>utf-8</c>, <c>utf-8-bom</c>, <c>utf-16le</c>/<c>unicode</c>, <c>utf-16be</c>, <c>windows-1252</c>/<c>ansi</c>/<c>cp1252</c>.</summary>
    public static bool TryParse(string? name, out LoadFileEncodingKind kind)
    {
        kind = default;
        switch (name?.Trim().ToUpperInvariant())
        {
            case "UTF-8" or "UTF8" or "UTF-8-BOM" or "UTF8BOM":
                kind = LoadFileEncodingKind.Utf8;
                return true;
            case "UTF-16LE" or "UTF16LE" or "UTF-16" or "UNICODE":
                kind = LoadFileEncodingKind.Utf16LE;
                return true;
            case "UTF-16BE" or "UTF16BE":
                kind = LoadFileEncodingKind.Utf16BE;
                return true;
            case "WINDOWS-1252" or "WINDOWS1252" or "CP1252" or "ANSI":
                kind = LoadFileEncodingKind.Windows1252;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The encoding without a preamble; decoders replace invalid input with U+FFFD.</summary>
    public static Encoding Get(LoadFileEncodingKind kind) => kind switch
    {
        LoadFileEncodingKind.Utf8 => Utf8,
        LoadFileEncodingKind.Utf16LE => Utf16LE,
        LoadFileEncodingKind.Utf16BE => Utf16BE,
        _ => Windows1252Encoding.Instance,
    };

    public static ReadOnlySpan<byte> Preamble(LoadFileEncodingKind kind) => kind switch
    {
        LoadFileEncodingKind.Utf8 => [0xEF, 0xBB, 0xBF],
        LoadFileEncodingKind.Utf16LE => [0xFF, 0xFE],
        LoadFileEncodingKind.Utf16BE => [0xFE, 0xFF],
        _ => [],
    };

    /// <summary>Bytes per code unit: 2 for UTF-16, otherwise 1.</summary>
    public static int UnitSize(LoadFileEncodingKind kind) => kind is LoadFileEncodingKind.Utf16LE or LoadFileEncodingKind.Utf16BE ? 2 : 1;

    /// <summary>True when <paramref name="c"/> can be written in <paramref name="kind"/> and read back unchanged.</summary>
    public static bool CanEncode(LoadFileEncodingKind kind, char c) =>
        kind != LoadFileEncodingKind.Windows1252 ? !char.IsSurrogate(c) : Windows1252Encoding.TryEncode(c, out _);
}
