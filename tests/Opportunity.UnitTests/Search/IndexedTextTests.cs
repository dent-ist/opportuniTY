using System.Text;

using AwesomeAssertions;

using Opportunity.Application.Search.Projection;
using Opportunity.Application.Storage;
using Opportunity.Search.Projection;

namespace Opportunity.UnitTests.Search;

/// <summary>ADR-007 R9/R10 (Q-29): the indexed-text cap, its cut rule and the bounded read.</summary>
public sealed class IndexedTextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Text_within_the_cap_is_kept_whole()
    {
        IndexedText.Cap("exactly ten", 11).Should().Be(new ProjectionText("exactly ten", false));
        IndexedText.Cap(string.Empty, 5).Should().Be(new ProjectionText(string.Empty, false));
    }

    [Fact]
    public void The_cut_is_at_the_last_whitespace_within_the_final_window()
    {
        var text = new string('a', 1_500) + " " + new string('b', 600) + "\t" + new string('c', 2_000);
        var capped = IndexedText.Cap(text, 3_000);

        capped.Truncated.Should().BeTrue();
        capped.Text.Should().Be(text[..2_101], "the tab at 2,101 is the last whitespace before the cap");
    }

    [Fact]
    public void Without_whitespace_in_the_window_the_cut_is_at_the_cap()
    {
        var text = "word " + new string('x', 5_000);
        IndexedText.Cap(text, 2_000).Text.Should().HaveLength(2_000);
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // 🙂 is two UTF-16 code units; with no whitespace the cap would fall between them.
        var text = new string('x', 1_999) + "🙂" + new string('y', 10);
        var capped = IndexedText.Cap(text, 2_000);

        capped.Text.Should().Be(new string('x', 1_999));
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(capped.Text)).Should().Be(capped.Text);
    }

    [Fact]
    public async Task Reading_stops_one_character_after_the_cap()
    {
        using var reader = new CountingReader(new string('z', 100_000));
        var capped = await IndexedText.ReadAsync(reader, 5_000, Ct);

        capped.Text.Should().HaveLength(5_000);
        capped.Truncated.Should().BeTrue();
        reader.Consumed.Should().Be(5_001);
    }

    [Fact]
    public async Task The_object_store_loader_range_reads_only_what_the_cap_needs()
    {
        // Multi-byte text: 3-byte characters are the worst case for the byte range.
        var text = string.Concat(Enumerable.Repeat("€uro ", 3_000));
        var store = new RecordingStore(Encoding.UTF8.GetBytes(text));
        var loader = new ObjectStoreProjectionTextLoader(store, new ProjectionOptions { IndexedTextCap = 1_000 });

        var loaded = await loader.LoadAsync(Source(ProjectionSamples.TextKey), Ct);

        store.Range.Should().Be(new ByteRange(0, 3_003));
        loaded!.Truncated.Should().BeTrue();
        loaded.Text.Should().Be(text[..999], "the cut falls on the space at index 999");
        (await loader.LoadAsync(Source(null), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task An_empty_text_object_loads_as_empty_text()
    {
        var loader = new ObjectStoreProjectionTextLoader(new RecordingStore([]), new ProjectionOptions());
        var loaded = await loader.LoadAsync(Source(ProjectionSamples.TextKey), Ct);
        loaded.Should().Be(new ProjectionText(string.Empty, false));
    }

    private static ProjectionSource Source(string? key) => new()
    {
        WorkspaceId = ProjectionSamples.WorkspaceId,
        DocumentId = ProjectionSamples.DocumentId,
        State = ProjectionSourceState.Live,
        DocumentVersion = 1,
        TextObjectKey = key,
    };

    private sealed class CountingReader(string text) : StringReader(text)
    {
        public int Consumed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var read = Read(buffer.Span);
            Consumed += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class RecordingStore(byte[] content) : IObjectStore
    {
        public ByteRange? Range { get; private set; }

        public Task<Stream> OpenReadAsync(ObjectKey key, ByteRange? range = null, CancellationToken cancellationToken = default)
        {
            Range = range;
            if (range is { } r && r.Offset >= content.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(range), "The range starts at or after the end of the object.");
            }

            var length = (int)Math.Min(content.Length - (range?.Offset ?? 0), range?.Length ?? long.MaxValue);
            return Task.FromResult<Stream>(new MemoryStream(content, (int)(range?.Offset ?? 0), length));
        }

        public Task<PutObjectResult> PutAsync(ObjectKey key, Stream content, PutObjectOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectInfo?> HeadAsync(ObjectKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<ObjectListing> ListPrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeletePrefixResult> DeletePrefixAsync(ObjectPrefix prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
