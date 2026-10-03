using AwesomeAssertions;

using Opportunity.DataGenerator.Corpus.Generation;
using Opportunity.DataGenerator.Corpus.Model;

namespace Opportunity.DataGenerator.Tests;

[CollectionDefinition(nameof(MemorySensitive), DisableParallelization = true)]
public sealed class MemorySensitive;

/// <summary>Rough check that generation streams: live heap does not grow with the number of documents produced.</summary>
[Collection(nameof(MemorySensitive))]
public class StreamingMemoryTests
{
    [Fact]
    public void Live_heap_stays_bounded_while_streaming_a_large_corpus()
    {
        const long documents = 300_000;
        var generator = new CorpusGenerator(TestCorpus.Profile(documents), 99, threads: 4);
        long produced = 0;
        long earlyPeak = 0;
        long latePeak = 0;
        int chunks = 0;
        foreach (GeneratedChunk chunk in generator.GenerateChunks())
        {
            produced += chunk.Families.Sum(f => f.Documents.Count);
            if (++chunks % 25 != 0)
            {
                continue;
            }

            GC.Collect();
            long live = GC.GetTotalMemory(forceFullCollection: true);
            if (produced < documents / 4)
            {
                earlyPeak = Math.Max(earlyPeak, live);
            }
            else if (produced > documents * 3 / 4)
            {
                latePeak = Math.Max(latePeak, live);
            }
        }

        produced.Should().Be(documents);

        // Retaining 300K documents would need well over 1 GB; a streaming pipeline holds only a window of chunks.
        latePeak.Should().BeLessThan(400L * 1024 * 1024);
        latePeak.Should().BeLessThan((earlyPeak * 2) + (32L * 1024 * 1024));
    }
}
