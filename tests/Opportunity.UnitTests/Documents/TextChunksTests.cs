using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Content;

namespace Opportunity.UnitTests.Documents;

/// <summary>
/// E11-T01 chunked text: fixed byte-addressed chunks that never split a UTF-8 character, cover every byte exactly once
/// and concatenate to the whole text, reading only each chunk's own range.
/// </summary>
public sealed class TextChunksTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Chunks_of_multibyte_text_concatenate_to_the_original_whatever_the_alignment(int shift)
    {
        // 1-, 2-, 3- and 4-byte characters, shifted so that each kind straddles a chunk boundary in some case.
        var builder = new StringBuilder(new string('a', shift));
        var alphabet = new[] { "a", "é", "中", "😀", "\n" };
        for (var i = 0; builder.Length < (TextChunks.ChunkBytes * 2) + 5000; i++)
        {
            builder.Append(alphabet[(i * 7 + (i / 3)) % alphabet.Length]);
        }

        var text = builder.ToString();
        var bytes = Encoding.UTF8.GetBytes(text);

        var decoded = ReadAll(bytes);

        string.Concat(decoded.Select(c => c.Text)).Should().Be(text);
        decoded.Count.Should().Be(TextChunks.Count(bytes.Length));
        decoded[0].ByteStart.Should().Be(0);
        decoded[^1].ByteEnd.Should().Be(bytes.Length);
        for (var i = 1; i < decoded.Count; i++)
        {
            decoded[i].ByteStart.Should().Be(decoded[i - 1].ByteEnd, "chunk {0} starts where the previous one ended", i);
            decoded[i].ByteStart.Should().BeInRange((long)i * TextChunks.ChunkBytes, ((long)i * TextChunks.ChunkBytes) + TextChunks.MaxContinuationBytes);
        }
    }

    [Fact]
    public void A_leading_byte_order_mark_is_not_part_of_the_text()
    {
        var bytes = (byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Memo")];

        var chunk = ReadAll(bytes).Should().ContainSingle().Subject;

        chunk.Text.Should().Be("Memo");
        (chunk.ByteStart, chunk.ByteEnd).Should().Be((3L, 7L));
    }

    [Fact]
    public void Invalid_bytes_become_replacement_characters_and_coverage_stays_exact()
    {
        var bytes = new byte[TextChunks.ChunkBytes + 20];
        bytes.AsSpan().Fill((byte)'x');
        bytes.AsSpan(TextChunks.ChunkBytes - 2, 9).Fill(0x80); // a run of stray continuation bytes across the boundary

        var decoded = ReadAll(bytes);

        decoded.Should().HaveCount(2);
        decoded[1].ByteStart.Should().Be(decoded[0].ByteEnd);
        decoded[1].ByteEnd.Should().Be(bytes.Length);
        string.Concat(decoded.Select(c => c.Text)).Should().Contain("�");
    }

    [Fact]
    public void An_empty_text_has_one_empty_chunk_and_nothing_beyond_the_end_is_readable()
    {
        TextChunks.Count(0).Should().Be(1);
        TextChunks.TryGetReadRange(0, 0, out var range).Should().BeTrue();
        range.Should().BeNull();
        TextChunks.TryGetReadRange(0, 1, out _).Should().BeFalse();
        TextChunks.TryGetReadRange(10, -1, out _).Should().BeFalse();
        TextChunks.TryGetReadRange(TextChunks.ChunkBytes, 1, out _).Should().BeFalse();
        TextChunks.Count(TextChunks.ChunkBytes).Should().Be(1);
        TextChunks.Count(TextChunks.ChunkBytes + 1L).Should().Be(2);
        TextChunks.Decode([], 0).Should().Be(new DecodedChunk(string.Empty, 0, 0));
    }

    private static List<DecodedChunk> ReadAll(byte[] bytes)
    {
        var chunks = new List<DecodedChunk>();
        for (var n = 0; TextChunks.TryGetReadRange(bytes.Length, n, out var range); n++)
        {
            var r = range!.Value;
            r.Length.Should().BeLessThanOrEqualTo(TextChunks.ChunkBytes + TextChunks.MaxContinuationBytes, "a chunk reads only its own range");
            chunks.Add(TextChunks.Decode(bytes.AsSpan((int)r.Offset, (int)r.Length!.Value), r.Offset));
        }

        return chunks;
    }
}
