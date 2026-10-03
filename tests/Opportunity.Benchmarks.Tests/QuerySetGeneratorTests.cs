using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Benchmarks.Workloads;

namespace Opportunity.Benchmarks.Tests;

public class QuerySetGeneratorTests
{
    private static readonly string[] CodingFilters = ["responsiveness:", "privilege:", "issues:"];

    private static QuerySet Set => QueryTestCorpus.QuerySet;

    private static QueryCorpus Corpus => QueryCorpus.Open(QueryTestCorpus.Manifest);

    [Fact]
    public void Same_seed_and_corpus_yield_a_byte_identical_query_file_for_any_thread_count()
    {
        string again = QueryTestCorpus.Generate(QueryTestCorpus.QuerySeed, threads: 1).ToJson();

        again.Should().Be(Set.ToJson());
    }

    [Fact]
    public void Another_seed_yields_another_query_sequence()
    {
        QuerySet other = QueryTestCorpus.Generate(QueryTestCorpus.QuerySeed + 1);

        other.Queries.Select(q => q.Oql).Should().NotEqual(Set.Queries.Select(q => q.Oql));
        other.Schedule.Should().NotEqual(Set.Schedule);
    }

    [Fact]
    public void Class_counts_follow_the_taxonomy_and_the_S29_mix()
    {
        IReadOnlyDictionary<string, int> expected = QueryTaxonomy.Allocate(QueryTestCorpus.Queries);

        Set.Queries.Should().HaveCount(QueryTestCorpus.Queries);
        Set.Queries.GroupBy(q => q.Class).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(expected);
        Set.Queries.GroupBy(q => q.Bucket).ToDictionary(g => g.Key, g => (double)g.Count() / Set.Queries.Count)
            .Should().BeEquivalentTo(new Dictionary<string, double> { ["simple"] = 0.6, ["boolean"] = 0.3, ["proximity-wildcard"] = 0.1 });
        foreach (BenchmarkQuery q in Set.Queries)
        {
            QueryClassDefinition c = QueryTaxonomy.Class(q.Class);
            q.Gate.Should().Be(c.Gate.Name());
            q.Bucket.Should().Be(c.MixBucket);
        }

        Set.Queries.Select(q => q.Id).Should().OnlyHaveUniqueItems();
        Set.TaxonomyVersion.Should().Be(QueryTaxonomy.Version);
    }

    [Fact]
    public void Queries_use_the_classes_defining_features()
    {
        IEnumerable<BenchmarkQuery> Of(string c) => Set.Queries.Where(q => q.Class == c);

        Of("content-coding").Should().OnlyContain(q => CodingFilters.Any(f => q.Oql.Contains(f, StringComparison.Ordinal)));
        Of("content-coding").Should().Contain(q => q.Oql.Contains("date:[", StringComparison.Ordinal), "date range + coding filter is a complex variant");
        Of("grid-coding").Should().OnlyContain(q => q.Sort.Any(s => s.Field == CodingFixture.ReviewPriorityField) || q.Facets.Count > 0);
        Of("expansion").Should().OnlyContain(q => q.Expand == "family" || q.Expand == "duplicates");
        Of("proximity").Should().OnlyContain(q => q.Oql.Contains(" W/", StringComparison.Ordinal));
        Of("wildcard").Should().OnlyContain(q => q.Oql.Contains('*', StringComparison.Ordinal));
        Of("wildcard").Where(q => !q.Oql.StartsWith("filename:", StringComparison.Ordinal))
            .Should().OnlyContain(q => q.Oql.Split(' ').Where(t => t.EndsWith('*')).All(t => t.Length >= 4), "R7: >= 3 literal characters before a text wildcard");
        Of("boolean").Should().OnlyContain(q => q.Oql.Contains(" OR ", StringComparison.Ordinal) || q.Oql.Contains("NOT ", StringComparison.Ordinal));
        Set.Queries.Where(q => q.Gate == "simple").Should().OnlyContain(q =>
            !q.Oql.Contains('*', StringComparison.Ordinal) && !q.Oql.Contains(" W/", StringComparison.Ordinal) && !q.Oql.Contains(" OR ", StringComparison.Ordinal) && q.Expand == null && q.Facets.Count == 0);
    }

    [Fact]
    public void Every_query_has_known_hits_and_the_band_of_its_hit_fraction()
    {
        foreach (BenchmarkQuery q in Set.Queries)
        {
            q.ExpectedHits.Should().BeGreaterThan(0, q.Oql);
            q.HitFraction.Should().BeApproximately((double)q.ExpectedHits / QueryTestCorpus.Documents, 1e-8);
            q.Selectivity.Should().Be(QueryTaxonomy.Band((double)q.ExpectedHits / QueryTestCorpus.Documents).Name(), q.Oql);
        }

        Set.Evaluation.TextMode.Should().Be("exact");
        Set.Queries.Select(q => q.Selectivity).Distinct().Should().HaveCountGreaterThanOrEqualTo(3);
        Set.Queries.Should().Contain(q => q.HitSource == "ground-truth");
    }

    [Fact]
    public void Schedule_replays_every_query_once_per_epoch()
    {
        int n = Set.Queries.Count;
        Set.Schedule.Count.Should().BeGreaterThanOrEqualTo(10_000);
        Set.Schedule.Count.Should().Be(n * (Set.Schedule.Count / n));
        for (int epoch = 0; epoch < Set.Schedule.Count / n; epoch++)
        {
            Set.Schedule.Skip(epoch * n).Take(n).Order().Should().Equal(Enumerable.Range(0, n));
        }
    }

    [Fact]
    public void Planted_needle_and_proximity_counts_match_the_ground_truth_file()
    {
        var truth = QueryTestCorpus.Lines("ground-truth.jsonl").ToList();
        long Docs(Func<JsonElement, bool> predicate) => truth.Where(predicate).Select(t => t.GetProperty("controlNumber").GetString()).Distinct().LongCount();
        bool Needle(JsonElement t, string term) => t.GetProperty("type").GetString() == "needle" && t.GetProperty("term").GetString() == term;
        bool Pair(JsonElement t, bool near) => t.GetProperty("type").GetString() == "proximity" && t.GetProperty("near").GetBoolean() == near;

        OqlTerm kelvatron = new("kelvatron"), brisomund = new("brisomund");
        (OqlNode, SearchOptions)[] queries =
        [
            (new OqlTerm("zyqvoltran"), SearchOptions.Default),
            (new OqlTerm("vex-tomarin"), SearchOptions.Default),
            (new OqlOr([new OqlTerm("zyqvoltran"), new OqlTerm("plimsorbex")]), SearchOptions.Default),
            (new OqlProximity(kelvatron, brisomund, 2), SearchOptions.Default),
            (new OqlProximity(kelvatron, brisomund, 5), SearchOptions.Default),
            (new OqlProximity(brisomund, kelvatron, 12), SearchOptions.Default),
            (new OqlTerm("zyqvoltran"), new SearchOptions { Expand = "family" }),
        ];
        IReadOnlyList<long> hits = QuerySetGenerator.CountHits(Corpus, queries, QueryTestCorpus.Options(1));

        long near = Docs(t => Pair(t, true)), far = Docs(t => Pair(t, false));
        near.Should().BeGreaterThan(0);
        far.Should().BeGreaterThan(0);
        hits[0].Should().Be(Docs(t => Needle(t, "zyqvoltran"))).And.BeGreaterThan(0);
        hits[1].Should().Be(Docs(t => Needle(t, "vex-tomarin"))).And.BeGreaterThan(0);
        hits[2].Should().Be(Docs(t => Needle(t, "zyqvoltran") || Needle(t, "plimsorbex")));
        hits[3].Should().Be(0, "planted near pairs are 3 words apart: W/2 must not match");
        hits[4].Should().Be(near, "W/5 matches the near pairs (distance 3) but not the far ones (12)");
        hits[5].Should().Be(near + far, "W/n is unordered and W/12 reaches the far pairs");

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(QueryTestCorpus.Manifest));
        long familyExpanded = manifest.RootElement.GetProperty("groundTruth").GetProperty("needles").EnumerateArray()
            .Single(n => n.GetProperty("term").GetString() == "zyqvoltran").GetProperty("familyExpandedDocs").GetInt64();
        hits[6].Should().Be(familyExpanded, "family expansion matches the manifest's family-expanded count");
    }

    [Fact]
    public void Text_and_metadata_counts_match_the_written_corpus()
    {
        var documents = QueryTestCorpus.Lines("documents.jsonl").ToList();
        var tokens = documents.Select(d => d.TryGetProperty("text", out JsonElement t) ? TextTokenizer.Tokens(t.GetString()!).ToHashSet(StringComparer.Ordinal) : []).ToList();
        static string? Field(JsonElement d, string name) => d.TryGetProperty("fields", out JsonElement f) && f.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var singleTerms = Set.Queries.Where(q => q.Class == "term" && q.HitSource == "exact" && !q.Oql.Contains(' ', StringComparison.Ordinal)).ToList();
        singleTerms.Should().NotBeEmpty();
        foreach (BenchmarkQuery q in singleTerms)
        {
            tokens.Count(t => t.Contains(q.Oql)).Should().Be((int)q.ExpectedHits, q.Oql);
        }

        foreach (BenchmarkQuery q in Set.Queries.Where(q => q.Oql.StartsWith("filename:*.", StringComparison.Ordinal)))
        {
            string suffix = q.Oql["filename:*".Length..];
            documents.Count(d => Field(d, "FileName")?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true).Should().Be((int)q.ExpectedHits, q.Oql);
        }

        // Duplicate expansion of a planted term, counted independently from duplicateGroupId.
        static string Group(JsonElement d) => (d.TryGetProperty("duplicateGroupId", out JsonElement g) ? g.GetString() : null) ?? d.GetProperty("controlNumber").GetString()!;
        var hit = documents.Where((d, i) => tokens[i].Contains("zyqvoltran")).Select(Group).ToHashSet(StringComparer.Ordinal);
        long expanded = documents.LongCount(d => hit.Contains(Group(d)));
        QuerySetGenerator.CountHits(Corpus, [(new OqlTerm("zyqvoltran"), new SearchOptions { Expand = "duplicates" })], QueryTestCorpus.Options(1))[0].Should().Be(expanded);

        // A sampled estimate scales the sample count to the corpus: within a factor of the exact count for broad terms.
        BenchmarkQuery broad = Set.Queries.First(q => q.Class == "term" && q.Selectivity == "broad");
        long estimate = QuerySetGenerator.CountHits(Corpus, [(new OqlTerm(broad.Oql.Split(' ')[0]), SearchOptions.Default)], QueryTestCorpus.Options(1) with { TextSampleDocuments = 800 })[0];
        estimate.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_corpus_manifest_from_another_generator_version_is_refused()
    {
        string copy = Path.Combine(SampleBundle.NewDirectory(), "corpus-manifest.json");
        File.WriteAllText(copy, File.ReadAllText(QueryTestCorpus.Manifest).Replace("\"version\": \"1.0.0\"", "\"version\": \"0.9.0\"", StringComparison.Ordinal));

        Action open = () => QueryCorpus.Open(copy);

        open.Should().Throw<InvalidOperationException>().WithMessage("*0.9.0*");
    }
}
