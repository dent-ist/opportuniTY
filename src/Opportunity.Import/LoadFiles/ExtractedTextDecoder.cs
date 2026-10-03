using System.Buffers;
using System.Text;

namespace Opportunity.Import.LoadFiles;

/// <summary>Outcome of decoding one extracted-text file.</summary>
/// <param name="InvalidSequences">Byte sequences invalid in the encoding, each replaced by U+FFFD.</param>
public sealed record ExtractedTextResult(DetectedEncoding Encoding, long Bytes, long Chars, long InvalidSequences)
{
    /// <summary>Sets the document's <c>TextEncodingWarning</c>: the text was imported with replacement characters, not failed.</summary>
    public bool TextEncodingWarning => InvalidSequences > 0;
}

/// <summary>
/// Streams an extracted-text (TXT) file to a <see cref="TextWriter"/>, with its own encoding detection or override,
/// independent of the DAT (eDiscovery review §12). Invalid bytes never fail the file.
/// </summary>
public static class ExtractedTextDecoder
{
    public static async Task<ExtractedTextResult> DecodeAsync(Stream input, TextWriter output, LoadFileEncodingKind? encodingOverride = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        const int size = EncodingDetector.DefaultSampleBytes;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(size);
        char[] chars = ArrayPool<char>.Shared.Rent(size + 4);
        try
        {
            int filled = 0;
            int n;
            while (filled < size && (n = await input.ReadAsync(bytes.AsMemory(filled, size - filled), cancellationToken).ConfigureAwait(false)) > 0)
            {
                filled += n;
            }

            DetectedEncoding detected = EncodingDetector.Detect(bytes.AsSpan(0, filled), filled < size, encodingOverride);
            var fallback = new CountingDecoderFallback();
            var encoding = (Encoding)LoadFileEncodings.Get(detected.Kind).Clone();
            encoding.DecoderFallback = fallback;
            Decoder decoder = encoding.GetDecoder();

            long totalBytes = filled;
            long totalChars = 0;
            int offset = detected.PreambleLength;
            while (true)
            {
                bool final = filled < size;
                int count = filled - offset;
                int written = decoder.GetChars(bytes, offset, count, chars, 0, flush: final);
                totalChars += written;
                await output.WriteAsync(chars.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                if (final)
                {
                    break;
                }

                filled = 0;
                offset = 0;
                while (filled < size && (n = await input.ReadAsync(bytes.AsMemory(filled, size - filled), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    filled += n;
                }

                totalBytes += filled;
            }

            return new ExtractedTextResult(detected, totalBytes, totalChars, fallback.Count);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private sealed class CountingDecoderFallback : DecoderFallback
    {
        public long Count { get; set; }

        public override int MaxCharCount => 1;

        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer(this);

        private sealed class Buffer(CountingDecoderFallback owner) : DecoderFallbackBuffer
        {
            private int _remaining;

            public override int Remaining => _remaining;

            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                owner.Count++;
                _remaining = 1;
                return true;
            }

            public override char GetNextChar()
            {
                if (_remaining == 0)
                {
                    return '\0';
                }

                _remaining--;
                return '�';
            }

            public override bool MovePrevious()
            {
                if (_remaining == 0)
                {
                    _remaining = 1;
                    return true;
                }

                return false;
            }
        }
    }
}
