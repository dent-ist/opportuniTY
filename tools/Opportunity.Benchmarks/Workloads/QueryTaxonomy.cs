namespace Opportunity.Benchmarks.Workloads;

/// <summary>The two classes the §26 degradation gates are evaluated on (gates.yaml <c>queryClasses[name=simple|complex]</c>).</summary>
public enum GateClass
{
    Simple,
    Complex,
}

/// <summary>Selectivity band: share of the corpus a query hits (after family/duplicate expansion).</summary>
public enum SelectivityBand
{
    /// <summary>Fewer than 0.01% of documents (planted needles).</summary>
    Needle,

    /// <summary>0.01% to below 1%.</summary>
    Narrow,

    /// <summary>1% to below 10%.</summary>
    Medium,

    /// <summary>10% to below 50%.</summary>
    Broad,

    /// <summary>50% or more.</summary>
    VeryBroad,
}

/// <summary>One taxonomy bucket: the unit results are reported by (p50/p95/p99 per bucket, E17-T04).</summary>
public sealed record QueryClassDefinition(string Id, GateClass Gate, string MixBucket, double Share, string Description);

/// <summary>
/// Versioned query taxonomy (docs/benchmarks/query-taxonomy.md is the normative description). A change to a class,
/// share, rule or band boundary bumps <see cref="Version"/>; results from different taxonomy versions are not compared.
/// </summary>
public static class QueryTaxonomy
{
    public const string Version = "1.0.0";

    /// <summary>§29 mix buckets.</summary>
    public const string SimpleBucket = "simple";
    public const string BooleanBucket = "boolean";
    public const string ProximityWildcardBucket = "proximity-wildcard";

    /// <summary>Grid page size and highlighting for every benchmark search (Simple definition, E17-T04).</summary>
    public const int PageSize = 50;
    public const bool Highlight = true;

    /// <summary>Indexed-text cap the evaluator applies before matching (ADR-007 R9).</summary>
    public const int IndexedTextCapChars = 10_000_000;

    public static IReadOnlyList<QueryClassDefinition> Classes { get; } =
    [
        new("term", GateClass.Simple, SimpleBucket, 0.20, "1-2 bare terms, implicit or explicit AND"),
        new("phrase", GateClass.Simple, SimpleBucket, 0.10, "one 2-3 word phrase"),
        new("field-filter", GateClass.Simple, SimpleBucket, 0.20, "1-2 terms or one phrase + 1-2 keyword/choice metadata filters"),
        new("date-range", GateClass.Simple, SimpleBucket, 0.10, "1-2 terms + a document-date range (+ at most one more metadata filter)"),
        new("boolean", GateClass.Complex, BooleanBucket, 0.10, ">= 3 Boolean clauses with nesting (AND/OR/NOT, groups)"),
        new("content-coding", GateClass.Complex, BooleanBucket, 0.10, "content query + coding-field filter, incl. date range + coding filter"),
        new("expansion", GateClass.Complex, BooleanBucket, 0.05, "family or duplicate expansion of a needle/metadata query"),
        new("grid-coding", GateClass.Complex, BooleanBucket, 0.05, "grid sort and facets on coding columns over a browse/simple query"),
        new("proximity", GateClass.Complex, ProximityWildcardBucket, 0.05, "W/n between terms, planted pairs or OR-groups"),
        new("wildcard", GateClass.Complex, ProximityWildcardBucket, 0.05, "trailing text wildcard (>= 3 literal chars) or leading/infix filename wildcard"),
    ];

    /// <summary>§29: 60% simple / 30% Boolean / 10% proximity-wildcard.</summary>
    public static IReadOnlyDictionary<string, double> Mix { get; } = Classes
        .GroupBy(c => c.MixBucket, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => Math.Round(g.Sum(c => c.Share), 6), StringComparer.Ordinal);

    public static QueryClassDefinition Class(string id) =>
        Classes.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentException($"Unknown query class '{id}'.", nameof(id));

    public static SelectivityBand Band(double hitFraction) => hitFraction switch
    {
        < 0.0001 => SelectivityBand.Needle,
        < 0.01 => SelectivityBand.Narrow,
        < 0.10 => SelectivityBand.Medium,
        < 0.50 => SelectivityBand.Broad,
        _ => SelectivityBand.VeryBroad,
    };

    public static string Name(this GateClass gate) => gate == GateClass.Simple ? "simple" : "complex";

    public static string Name(this SelectivityBand band) => band switch
    {
        SelectivityBand.Needle => "needle",
        SelectivityBand.Narrow => "narrow",
        SelectivityBand.Medium => "medium",
        SelectivityBand.Broad => "broad",
        _ => "very-broad",
    };

    /// <summary>Splits <paramref name="total"/> queries over the classes by share (largest remainder, ties by class order).</summary>
    public static IReadOnlyDictionary<string, int> Allocate(int total)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(total, Classes.Count);
        var exact = Classes.Select((c, i) => (c.Id, Index: i, Value: total * c.Share)).ToList();
        var counts = exact.ToDictionary(e => e.Id, e => (int)Math.Floor(e.Value), StringComparer.Ordinal);
        int remaining = total - counts.Values.Sum();
        foreach (var e in exact.OrderByDescending(e => e.Value - Math.Floor(e.Value)).ThenBy(e => e.Index).Take(remaining))
        {
            counts[e.Id]++;
        }

        return counts;
    }

    public static string Describe(SelectivityBand band) => band switch
    {
        SelectivityBand.Needle => "< 0.01%",
        SelectivityBand.Narrow => "0.01% - < 1%",
        SelectivityBand.Medium => "1% - < 10%",
        SelectivityBand.Broad => "10% - < 50%",
        _ => ">= 50%",
    };
}

/// <summary>
/// Reviewer session model (closed model, E17-T04): search, open up to 25 documents, read (log-normal think time,
/// median 20 s, p90 60 s), code each, pause 5 s before the next search. The k6 scripts implement the same constants.
/// </summary>
public static class ReviewerSessionModel
{
    public const int DefaultReviewers = 100;
    public const double ThinkTimeMedianSeconds = 20;
    public const double ThinkTimeP90Seconds = 60;
    public const double ThinkTimeMinSeconds = 2;
    public const double ThinkTimeMaxSeconds = 300;
    public const int MinDocumentsPerSearch = 5;
    public const int MaxDocumentsPerSearch = 25;
    public const double PauseBetweenSearchesSeconds = 5;

    /// <summary>z-score of the 90th percentile of the standard normal distribution.</summary>
    public const double Z90 = 1.2815515655446004;

    public static double Mu => Math.Log(ThinkTimeMedianSeconds);

    public static double Sigma => Math.Log(ThinkTimeP90Seconds / ThinkTimeMedianSeconds) / Z90;
}
