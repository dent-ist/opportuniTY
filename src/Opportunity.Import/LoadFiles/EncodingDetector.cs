using System.Buffers;
using System.Text;

namespace Opportunity.Import.LoadFiles;

/// <summary>
/// Decides the encoding of a DAT or extracted-text file from its leading bytes: byte-order mark first (EF BB BF,
/// FF FE, FE FF); otherwise a UTF-16 zero-byte pattern; otherwise UTF-8 when the sample is valid UTF-8 (or valid
/// multi-byte sequences outnumber invalid ones, i.e. a UTF-8 file with a few mis-encoded rows), else Windows-1252.
/// An explicit override always wins; a byte-order mark matching the override is still skipped.
/// </summary>
public static class EncodingDetector
{
    public const int DefaultSampleBytes = 64 * 1024;

    /// <param name="sample">Leading bytes of the file.</param>
    /// <param name="isWholeFile">True when <paramref name="sample"/> is the complete file (a truncated trailing sequence is then invalid).</param>
    /// <param name="encodingOverride">Explicit encoding chosen by the user, if any.</param>
    public static DetectedEncoding Detect(ReadOnlySpan<byte> sample, bool isWholeFile = false, LoadFileEncodingKind? encodingOverride = null)
    {
        (LoadFileEncodingKind kind, int length)? bom = SniffBom(sample);
        if (encodingOverride is { } forced)
        {
            int skip = bom is { } b && b.kind == forced ? b.length : 0;
            return new DetectedEncoding(forced, EncodingSource.Override, skip);
        }

        if (bom is { } found)
        {
            return new DetectedEncoding(found.kind, EncodingSource.ByteOrderMark, found.length);
        }

        if (DetectUtf16(sample) is { } utf16)
        {
            return new DetectedEncoding(utf16, EncodingSource.Heuristic, 0);
        }

        (int valid, int invalid, bool ascii) = ScanUtf8(sample, isWholeFile);
        LoadFileEncodingKind result = invalid == 0 || valid > invalid ? LoadFileEncodingKind.Utf8 : LoadFileEncodingKind.Windows1252;
        return new DetectedEncoding(result, EncodingSource.Heuristic, 0, ascii, invalid, valid);
    }

    /// <summary>Reads up to <paramref name="sampleBytes"/> from the start of a seekable or fresh stream and detects its encoding.</summary>
    public static async Task<DetectedEncoding> DetectAsync(Stream stream, LoadFileEncodingKind? encodingOverride = null, int sampleBytes = DefaultSampleBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(sampleBytes);
        try
        {
            int read = 0;
            int n;
            while (read < sampleBytes && (n = await stream.ReadAsync(buffer.AsMemory(read, sampleBytes - read), cancellationToken).ConfigureAwait(false)) > 0)
            {
                read += n;
            }

            return Detect(buffer.AsSpan(0, read), read < sampleBytes, encodingOverride);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (LoadFileEncodingKind, int)? SniffBom(ReadOnlySpan<byte> s)
    {
        if (s.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (LoadFileEncodingKind.Utf8, 3);
        }

        if (s.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (LoadFileEncodingKind.Utf16LE, 2);
        }

        if (s.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (LoadFileEncodingKind.Utf16BE, 2);
        }

        return null;
    }

    private static LoadFileEncodingKind? DetectUtf16(ReadOnlySpan<byte> s)
    {
        int pairs = Math.Min(s.Length, 8192) / 2;
        if (pairs < 2)
        {
            return null;
        }

        int evenZero = 0;
        int oddZero = 0;
        for (int i = 0; i < pairs; i++)
        {
            if (s[2 * i] == 0)
            {
                evenZero++;
            }

            if (s[(2 * i) + 1] == 0)
            {
                oddZero++;
            }
        }

        // Latin text in UTF-16 has a zero high byte in nearly every unit; 8-bit text almost never contains NULs.
        if (oddZero * 10 >= pairs * 4 && evenZero * 20 < pairs)
        {
            return LoadFileEncodingKind.Utf16LE;
        }

        if (evenZero * 10 >= pairs * 4 && oddZero * 20 < pairs)
        {
            return LoadFileEncodingKind.Utf16BE;
        }

        return null;
    }

    private static (int Valid, int Invalid, bool Ascii) ScanUtf8(ReadOnlySpan<byte> s, bool isWholeFile)
    {
        int valid = 0;
        int invalid = 0;
        bool ascii = true;
        int i = 0;
        while (i < s.Length)
        {
            int next = s[i..].IndexOfAnyExceptInRange((byte)0, (byte)0x7F);
            if (next < 0)
            {
                break;
            }

            ascii = false;
            i += next;
            OperationStatus status = Rune.DecodeFromUtf8(s[i..], out _, out int consumed);
            if (status == OperationStatus.Done)
            {
                valid++;
            }
            else if (status == OperationStatus.NeedMoreData && !isWholeFile)
            {
                break;
            }
            else
            {
                invalid++;
            }

            i += Math.Max(1, consumed);
        }

        return (valid, invalid, ascii);
    }
}
