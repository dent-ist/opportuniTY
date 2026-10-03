using System.Text;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Randomness;

namespace Opportunity.DataGenerator.Corpus.Text;

/// <summary>Receives synthesised text in chunks; implementations must copy the span if they keep it.</summary>
public interface ITextSink
{
    void Write(ReadOnlySpan<char> chars);
}

/// <summary>
/// Streams the extracted text described by a <see cref="TextSpec"/>: Zipf-sampled vocabulary in sentences and
/// paragraphs, planted needle words at fixed body word positions, and quoted prior-message text. Output is exactly
/// <see cref="TextSpec.TotalBytes"/> UTF-8 bytes and is produced in O(buffer) memory regardless of size.
/// </summary>
public sealed class TextSynthesizer(Vocabulary vocabulary)
{
    private const int BufferChars = 16 * 1024;

    public void Write(TextSpec spec, ITextSink sink)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sink);
        if (spec.TotalBytes == 0)
        {
            return;
        }

        var output = new BudgetWriter(spec.TotalBytes, sink);
        if (spec.Prefix != null)
        {
            output.Append(spec.Prefix);
        }

        WriteBody(output, spec.BodySeed, spec.VariantSeed, spec.WordChangeRate, spec.BodyBytes, spec.Planted);
        if (spec.QuotedBytes > 0)
        {
            output.Append(ContentFactory.QuoteSeparator);
            WriteBody(output, spec.QuotedSeed, 0, 0, spec.QuotedBytes, []);
        }

        output.PadToEnd();
        output.Flush();
    }

    public string Render(TextSpec spec)
    {
        var sink = new StringBuilderSink();
        Write(spec, sink);
        return sink.Builder.ToString();
    }

    private void WriteBody(BudgetWriter output, ulong seed, ulong variantSeed, double changeRate, long bodyBytes, IReadOnlyList<PlantedWord> planted)
    {
        long end = Math.Min(output.Remaining, bodyBytes);
        long stop = output.Remaining - end;
        var rng = new Rng(seed);
        ulong changeThreshold = changeRate <= 0 ? 0 : (ulong)(changeRate * ulong.MaxValue);
        int nextPlanted = 0;
        int sentenceLeft = 0;
        int paragraphLeft = rng.NextInt(3, 8);
        for (int i = 0; ; i++)
        {
            bool sentenceStart = sentenceLeft == 0;
            if (sentenceStart)
            {
                sentenceLeft = rng.NextInt(5, 25);
            }

            string word = vocabulary.Sample(rng);
            if (variantSeed != 0)
            {
                ulong h = StableHash.Combine(variantSeed, (ulong)i);
                if (h < changeThreshold)
                {
                    word = vocabulary[(int)(StableHash.Mix(h) % (ulong)vocabulary.Count)];
                }
            }

            bool isPlanted = nextPlanted < planted.Count && planted[nextPlanted].Position == i;
            if (isPlanted)
            {
                word = planted[nextPlanted++].Word;
            }

            sentenceLeft--;
            string separator = " ";
            if (sentenceLeft == 0)
            {
                paragraphLeft--;
                separator = paragraphLeft == 0 ? ".\n\n" : ". ";
                if (paragraphLeft == 0)
                {
                    paragraphLeft = rng.NextInt(3, 8);
                }
            }

            // Vocabulary and needle words are ASCII, so chars == bytes.
            int bytes = word.Length + separator.Length;
            if (output.Remaining - bytes < stop)
            {
                break;
            }

            if (sentenceStart && !isPlanted)
            {
                output.AppendCapitalized(word);
            }
            else
            {
                output.AppendAscii(word);
            }

            output.AppendAscii(separator);
        }

        output.Pad(output.Remaining - stop);
    }

    private sealed class StringBuilderSink : ITextSink
    {
        public StringBuilder Builder { get; } = new();

        public void Write(ReadOnlySpan<char> chars) => Builder.Append(chars);
    }

    private sealed class BudgetWriter(long totalBytes, ITextSink sink)
    {
        private readonly char[] _buffer = new char[BufferChars];
        private int _length;

        public long Remaining { get; private set; } = totalBytes;

        public void Append(string value)
        {
            Span<char> chars = stackalloc char[2];
            foreach (Rune rune in value.EnumerateRunes())
            {
                int bytes = rune.Utf8SequenceLength;
                if (bytes > Remaining)
                {
                    return;
                }

                int n = rune.EncodeToUtf16(chars);
                Put(chars[..n]);
                Remaining -= bytes;
            }
        }

        public void AppendAscii(string value)
        {
            Put(value);
            Remaining -= value.Length;
        }

        public void AppendCapitalized(string word)
        {
            Span<char> first = [char.ToUpperInvariant(word[0])];
            Put(first);
            Put(word.AsSpan(1));
            Remaining -= word.Length;
        }

        public void Pad(long bytes)
        {
            for (long i = 0; i < bytes; i++)
            {
                Put([' ']);
            }

            Remaining -= bytes;
        }

        public void PadToEnd() => Pad(Remaining);

        public void Flush()
        {
            if (_length > 0)
            {
                sink.Write(_buffer.AsSpan(0, _length));
                _length = 0;
            }
        }

        private void Put(ReadOnlySpan<char> chars)
        {
            if (_length + chars.Length > _buffer.Length)
            {
                Flush();
            }

            chars.CopyTo(_buffer.AsSpan(_length));
            _length += chars.Length;
        }
    }
}
