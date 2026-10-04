using System.Text;

using Opportunity.Application.Storage;

namespace Opportunity.Application.Content;

/// <summary>
/// Fixed-size chunking of a document's stored UTF-8 text for the viewer (E11-T01; UI finding 8: a ~10 MB text cannot
/// be loaded or highlighted in one piece). Chunk <c>n</c> owns every character whose first byte lies in
/// <c>[n × ChunkBytes, (n + 1) × ChunkBytes)</c>, so chunks are stable, never split a character, and concatenate to the
/// whole text. Reading a chunk needs only its own byte range plus up to three bytes that finish its last character.
/// </summary>
public static class TextChunks
{
    /// <summary>Bytes per chunk. Part of the API contract: changing it renumbers every chunk.</summary>
    public const int ChunkBytes = 256 * 1024;

    /// <summary>A UTF-8 character has at most three continuation bytes.</summary>
    public const int MaxContinuationBytes = 3;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Number of chunks of a text object of <paramref name="length"/> bytes (an empty text has one, empty, chunk).</summary>
    public static int Count(long length) => length <= 0 ? 1 : checked((int)((length + ChunkBytes - 1) / ChunkBytes));

    /// <summary>The byte range to read for <paramref name="chunk"/>, or null when the chunk lies beyond the text.</summary>
    /// <returns>Null range with true for the single chunk of an empty object (read nothing).</returns>
    public static bool TryGetReadRange(long length, int chunk, out ByteRange? range)
    {
        range = null;
        if (chunk < 0)
        {
            return false;
        }

        if (length <= 0)
        {
            return chunk == 0;
        }

        var start = (long)chunk * ChunkBytes;
        if (start >= length)
        {
            return false;
        }

        range = new ByteRange(start, Math.Min(ChunkBytes + MaxContinuationBytes, length - start));
        return true;
    }

    /// <summary>
    /// Decodes a chunk from the bytes read at <paramref name="offset"/> (its <see cref="TryGetReadRange"/> range):
    /// skips the continuation bytes that belong to the previous chunk's last character, stops after the last character
    /// that starts inside this chunk, and drops a leading byte-order mark. Invalid sequences become U+FFFD.
    /// </summary>
    public static DecodedChunk Decode(ReadOnlySpan<byte> bytes, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var start = 0;
        if (offset > 0)
        {
            while (start < MaxContinuationBytes && start < bytes.Length && IsContinuation(bytes[start]))
            {
                start++;
            }
        }
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            start = 3;
        }

        var end = Math.Min(ChunkBytes, bytes.Length);
        while (end < bytes.Length && IsContinuation(bytes[end]))
        {
            end++;
        }

        start = Math.Min(start, end);
        var text = Utf8.GetString(bytes[start..end]);
        return new DecodedChunk(text, offset + start, offset + end);
    }

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
}

/// <summary>A decoded chunk and the bytes of the stored text it covers (<c>[ByteStart, ByteEnd)</c>).</summary>
public readonly record struct DecodedChunk(string Text, long ByteStart, long ByteEnd);
