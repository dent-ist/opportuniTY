using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Workloads;

/// <summary>
/// The versioned query file the k6 workloads replay (<c>queries.json</c>). Generated deterministically from a corpus
/// manifest and a query seed by <see cref="QuerySetGenerator"/>; bundled with every result as a workload script.
/// </summary>
public sealed record QuerySet
{
    public const string FormatName = "opportunity-query-set";
    public const int CurrentFormatVersion = 1;

    public string Format { get; init; } = FormatName;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string TaxonomyVersion { get; init; } = QueryTaxonomy.Version;

    public QuerySetTool Generator { get; init; } = new(HarnessInfo.Name, HarnessInfo.Version);

    public required ulong QuerySeed { get; init; }

    public required QuerySetCorpus Corpus { get; init; }

    public required QueryEvaluation Evaluation { get; init; }

    public QueryRequestDefaults Request { get; init; } = new();

    public required CodingFixture CodingFixture { get; init; }

    public IReadOnlyDictionary<string, double> Mix { get; init; } = QueryTaxonomy.Mix;

    public required IReadOnlyList<QuerySetClass> Classes { get; init; }

    public required IReadOnlyList<BenchmarkQuery> Queries { get; init; }

    /// <summary>Replay order: indexes into <see cref="Queries"/>, a seeded permutation per epoch. Iteration i of a workload issues <c>Queries[Schedule[i % Schedule.Count]]</c>.</summary>
    public required IReadOnlyList<int> Schedule { get; init; }

    public static QuerySet Load(string path) => BenchJson.Deserialize<QuerySet>(File.ReadAllText(path));

    public string ToJson() => BenchJson.Serialize(this);
}

public sealed record QuerySetTool(string Name, string Version);

public sealed record QueryEvaluation
{
    /// <summary>"exact" when every document's text was evaluated, otherwise "sampled".</summary>
    public required string TextMode { get; init; }

    public required long TextSampleDocuments { get; init; }

    public int IndexedTextCapChars { get; init; } = QueryTaxonomy.IndexedTextCapChars;

    /// <summary>Zone for date-only bounds (ADR-008 R8); the benchmark user's display zone must be the same.</summary>
    public string Zone { get; init; } = "UTC";

    public string Tokenizer { get; init; } = "uax29-approx-lowercase-v1";

    /// <summary>Sampled text queries need at least this many hits in the sample to be kept.</summary>
    public int MinSampleHits { get; init; }
}

public sealed record QueryRequestDefaults
{
    public int PageSize { get; init; } = QueryTaxonomy.PageSize;

    public bool Highlight { get; init; } = QueryTaxonomy.Highlight;
}

public sealed record QuerySetClass(string Id, string Gate, string Bucket, double Share, int Count, string Description);

public sealed record BenchmarkQuery
{
    public required string Id { get; init; }

    public required string Class { get; init; }

    public required string Gate { get; init; }

    public required string Bucket { get; init; }

    /// <summary>Canonical ADR-008 text sent to the search API.</summary>
    public required string Oql { get; init; }

    /// <summary>null, "family" or "duplicates".</summary>
    public string? Expand { get; init; }

    public IReadOnlyList<SortKey> Sort { get; init; } = [];

    public IReadOnlyList<string> Facets { get; init; } = [];

    /// <summary>Documents the query returns (after expansion) on the freshly loaded corpus + coding fixture.</summary>
    public required long ExpectedHits { get; init; }

    public required double HitFraction { get; init; }

    public required string Selectivity { get; init; }

    /// <summary>"exact" (counted over every document), "ground-truth" (planted terms) or "sampled" (estimated from the text sample).</summary>
    public required string HitSource { get; init; }

    public long? SampleHits { get; init; }
}
