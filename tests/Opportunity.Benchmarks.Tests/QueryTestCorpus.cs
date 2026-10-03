using System.Text.Json;

using Opportunity.Benchmarks.Workloads;
using Opportunity.DataGenerator.Corpus.Output;
using Opportunity.DataGenerator.Corpus.Profiles;

namespace Opportunity.Benchmarks.Tests;

/// <summary>
/// A small corpus written to disk the way the generator CLI writes it (documents.jsonl with inline text, ground truth,
/// manifest), with planted terms boosted so every ground-truth kind occurs, plus a query set generated from it.
/// </summary>
internal static class QueryTestCorpus
{
    public const ulong CorpusSeed = 20261003;
    public const ulong QuerySeed = 42;
    public const int Documents = 2_000;
    public const int Queries = 200;

    public static CorpusProfile Profile { get; } = new()
    {
        Name = "query-tests",
        DocumentCount = Documents,
        Text = new TextProfile { MedianBytes = 4_000, P99Bytes = 40_000, MaxBytes = 400_000 },
        Needles =
        [
            new() { Term = "zyqvoltran", DocRate = 0.02, MaxOccurrences = 3 },
            new() { Term = "plimsorbex", DocRate = 0.01, Scope = NeedleScope.AttachmentsOnly },
            new() { Term = "vex-tomarin", DocRate = 0.01 },
        ],
        ProximityPairs = [new() { First = "kelvatron", Second = "brisomund", NearDistance = 3, FarDistance = 12, NearDocRate = 0.01, FarDocRate = 0.01 }],
    };

    private static readonly Lazy<string> Directory = new(() =>
    {
        string dir = SampleBundle.NewDirectory();
        CorpusRunner.Run(Profile, CorpusSeed, dir, new CorpusRunOptions { InlineText = true, Threads = 4 });
        return dir;
    });

    private static readonly Lazy<QuerySet> Set = new(() => Generate(QuerySeed));

    public static string Path => Directory.Value;

    public static string Manifest => System.IO.Path.Combine(Path, CorpusRunner.ManifestFile);

    public static QuerySet QuerySet => Set.Value;

    /// <summary>The query set written to disk (for the stub and k6).</summary>
    public static string QueryFile
    {
        get
        {
            string file = System.IO.Path.Combine(Path, "queries.json");
            lock (Set)
            {
                if (!File.Exists(file))
                {
                    File.WriteAllText(file, QuerySet.ToJson());
                }
            }

            return file;
        }
    }

    public static QuerySetOptions Options(ulong seed) => new() { QuerySeed = seed, QueryCount = Queries, TextSampleDocuments = 10_000 };

    public static QuerySet Generate(ulong seed, int threads = 0) => QuerySetGenerator.Generate(QueryCorpus.Open(Manifest, threads), Options(seed));

    public static IEnumerable<JsonElement> Lines(string file)
    {
        foreach (string line in File.ReadLines(System.IO.Path.Combine(Path, file)))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }
}
