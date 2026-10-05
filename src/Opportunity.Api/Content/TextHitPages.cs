using Opportunity.Application.Content;
using Opportunity.Application.Storage;
using Opportunity.Contracts.Api;
using Opportunity.Core.Highlighting;

namespace Opportunity.Api.Content;

/// <summary>One page of term hits: the chunks it covers and the hits that start in them, in chunk-local offsets.</summary>
internal sealed record TextHitPage(int ChunkCount, int FromChunk, int ToChunk, IReadOnlyList<TermHitResource> Hits, long[] Counts);

/// <summary>
/// Computes term hits over a document's stored text one 256 KiB chunk at a time (E16-T12): never the whole text in
/// memory, never an OpenSearch highlighter (which stops at <c>max_analyzed_offset</c>). A page covers up to
/// <see cref="MaxChunksPerPage"/> chunks or about <see cref="MaxHitsPerPage"/> hits; the chunk before the page and the
/// chunk after it are scanned too, so phrases and <c>W/n</c> spans that cross a chunk boundary are found whole and a
/// hit belongs to the page of the chunk it starts in.
/// </summary>
internal static class TextHitPages
{
    public const int MaxChunksPerPage = 16;

    public const int MaxHitsPerPage = 5_000;

    public static async Task<TextHitPage> ScanAsync(
        IObjectStore store, ObjectKey key, long length, int from, IReadOnlyList<HitUnit> units, CancellationToken cancellationToken)
    {
        var count = TextChunks.Count(length);
        var scanner = new TermHitScanner(units);
        var starts = new Dictionary<int, long>();
        var found = new List<TermHit>();
        int? last = null;
        long windowEnd = 0;
        var completed = false;
        for (var chunk = Math.Max(0, from - 1); chunk < count; chunk++)
        {
            var text = await ReadChunkAsync(store, key, length, chunk, cancellationToken).ConfigureAwait(false);
            starts[chunk] = scanner.Length;
            completed = chunk == count - 1;
            found.AddRange(scanner.Append(text, completed));
            if (last is not null || chunk < from)
            {
                if (last is not null)
                {
                    break; // the look-ahead chunk
                }

                continue;
            }

            var windowStart = starts[from];
            if (completed || chunk - from + 1 >= MaxChunksPerPage || found.Count(h => h.Start >= windowStart) >= MaxHitsPerPage)
            {
                last = chunk;
                windowEnd = scanner.Length;
                if (completed)
                {
                    break;
                }
            }
        }

        if (!completed)
        {
            found.AddRange(scanner.Complete());
        }

        var to = last ?? count - 1;
        var begin = starts.GetValueOrDefault(from);
        var counts = new long[units.Count];
        var hits = new List<TermHitResource>();
        foreach (var hit in found.Where(h => h.Start >= begin && h.Start < windowEnd).OrderBy(h => h.Start).ThenBy(h => h.Unit))
        {
            var chunk = to;
            while (chunk > from && starts[chunk] > hit.Start)
            {
                chunk--;
            }

            counts[hit.Unit]++;
            hits.Add(new TermHitResource(hit.Unit, chunk, checked((int)(hit.Start - starts[chunk])), checked((int)(hit.End - starts[chunk]))));
        }

        return new TextHitPage(count, from, to + 1, hits, counts);
    }

    private static async Task<string> ReadChunkAsync(IObjectStore store, ObjectKey key, long length, int chunk, CancellationToken cancellationToken)
    {
        if (!TextChunks.TryGetReadRange(length, chunk, out var range) || range is not { } r)
        {
            return string.Empty;
        }

        var bytes = new byte[r.Length!.Value];
        var source = await store.OpenReadAsync(key, r, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        return TextChunks.Decode(bytes, r.Offset).Text;
    }
}
