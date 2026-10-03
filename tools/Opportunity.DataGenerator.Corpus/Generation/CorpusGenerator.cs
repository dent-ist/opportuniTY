using Opportunity.DataGenerator.Corpus.Model;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.DataGenerator.Corpus.Generation;

/// <summary>
/// Streams a corpus in load order. Chunks are planned sequentially (cheap), generated in parallel on up to
/// <c>threads</c> workers, and yielded strictly in order through a bounded window, so memory is O(threads × chunk)
/// regardless of corpus size and output is identical for any thread count.
/// </summary>
public sealed class CorpusGenerator
{
    private readonly int _threads;

    public CorpusGenerator(CorpusProfile profile, ulong seed, int threads = 1)
        : this(new GenerationContext(profile, seed), threads)
    {
    }

    public CorpusGenerator(GenerationContext context, int threads = 1)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
        Context = context;
        _threads = threads;
    }

    public GenerationContext Context { get; }

    /// <summary>Maximum chunks generated ahead of the consumer.</summary>
    public int Window => _threads == 1 ? 1 : _threads * 2;

    public IEnumerable<GeneratedChunk> GenerateChunks()
    {
        var planner = new CorpusPlanner(Context);
        var generator = new ChunkGenerator(Context);
        if (_threads == 1)
        {
            foreach (ChunkPlan plan in planner.Chunks())
            {
                yield return generator.Generate(plan);
            }

            yield break;
        }

        var pending = new Queue<Task<GeneratedChunk>>();
        using IEnumerator<ChunkPlan> plans = planner.Chunks().GetEnumerator();
        while (true)
        {
            while (pending.Count < Window && plans.MoveNext())
            {
                ChunkPlan plan = plans.Current;
                pending.Enqueue(Task.Run(() => generator.Generate(plan)));
            }

            if (pending.Count == 0)
            {
                yield break;
            }

            yield return pending.Dequeue().GetAwaiter().GetResult();
        }
    }

    public IEnumerable<GeneratedFamily> GenerateFamilies() => GenerateChunks().SelectMany(c => c.Families);

    public IEnumerable<GeneratedDocument> GenerateDocuments() => GenerateFamilies().SelectMany(f => f.Documents);
}
