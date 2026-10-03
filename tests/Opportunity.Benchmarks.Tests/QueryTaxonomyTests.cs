using AwesomeAssertions;

using Opportunity.Benchmarks.Workloads;

namespace Opportunity.Benchmarks.Tests;

public class QueryTaxonomyTests
{
    private static readonly IReadOnlySet<string> Coding = new CodingFixture { Seed = 1 }.QueryNames;

    private static GateClass Classify(OqlNode query, SearchOptions? options = null) => QueryClassifier.Classify(query, options ?? SearchOptions.Default, Coding);

    private static OqlTerm T(string text) => new(text);

    [Fact]
    public void Mix_is_60_simple_30_boolean_10_proximity_wildcard_S29()
    {
        QueryTaxonomy.Mix.Should().BeEquivalentTo(new Dictionary<string, double> { ["simple"] = 0.6, ["boolean"] = 0.3, ["proximity-wildcard"] = 0.1 });
        QueryTaxonomy.Classes.Where(c => c.MixBucket == QueryTaxonomy.SimpleBucket).Should().OnlyContain(c => c.Gate == GateClass.Simple);
        QueryTaxonomy.Classes.Where(c => c.MixBucket != QueryTaxonomy.SimpleBucket).Should().OnlyContain(c => c.Gate == GateClass.Complex);
    }

    [Fact]
    public void Content_plus_coding_filter_and_grid_coding_have_mandatory_shares()
    {
        QueryTaxonomy.Class("content-coding").Share.Should().BeGreaterThanOrEqualTo(0.10);
        QueryTaxonomy.Class("grid-coding").Share.Should().BeGreaterThanOrEqualTo(0.05);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    [InlineData(1_000)]
    [InlineData(1_337)]
    public void Allocation_is_exact_and_follows_the_shares(int total)
    {
        IReadOnlyDictionary<string, int> counts = QueryTaxonomy.Allocate(total);

        counts.Values.Sum().Should().Be(total);
        foreach (QueryClassDefinition c in QueryTaxonomy.Classes)
        {
            counts[c.Id].Should().BeInRange((int)Math.Floor(total * c.Share), (int)Math.Ceiling(total * c.Share));
        }
    }

    [Fact]
    public void Baseline_S9_examples_classify_as_the_taxonomy_defines()
    {
        Classify(new OqlAnd([T("contract"), T("termination")])).Should().Be(GateClass.Simple);
        Classify(new OqlPhrase("trade secret")).Should().Be(GateClass.Simple);
        Classify(new OqlField("custodian", T("John Smith"))).Should().Be(GateClass.Simple);
        Classify(new OqlAnd([T("contract"), new OqlField("date", new OqlRange("2025-01-01", "2025-12-31"))])).Should().Be(GateClass.Simple);
        Classify(new OqlProximity(T("apple"), T("iphone"), 10)).Should().Be(GateClass.Complex);
        Classify(new OqlField("filename", new OqlWildcard("*.xlsx"))).Should().Be(GateClass.Complex);
    }

    [Fact]
    public void Simple_is_at_most_two_terms_or_one_phrase_with_at_most_two_metadata_filters()
    {
        OqlField custodian = new("custodian", T("A B")), extension = new("extension", T("pdf")), language = new("language", T("en"));

        Classify(new OqlAnd([T("a"), T("b"), custodian, extension])).Should().Be(GateClass.Simple);
        Classify(new OqlAnd([new OqlAnd([T("a"), T("b")], Explicit: false), custodian])).Should().Be(GateClass.Simple, "nested AND is one conjunction");
        Classify(new OqlAnd([T("a"), T("b"), T("c")])).Should().Be(GateClass.Complex, "three terms");
        Classify(new OqlAnd([new OqlPhrase("a b"), T("c")])).Should().Be(GateClass.Complex, "phrase plus term");
        Classify(new OqlAnd([T("a"), custodian, extension, language])).Should().Be(GateClass.Complex, "three filters");
        Classify(new OqlOr([T("a"), T("b")])).Should().Be(GateClass.Complex, "OR");
        Classify(new OqlAnd([T("a"), new OqlNot(T("b"))])).Should().Be(GateClass.Complex, "NOT");
        Classify(new OqlWildcard("contr*")).Should().Be(GateClass.Complex, "wildcard");
        Classify(new OqlAnd([T("a"), new OqlField("responsiveness", T("Responsive"))])).Should().Be(GateClass.Complex, "coding filter");
        Classify(T("a"), new SearchOptions { Expand = "family" }).Should().Be(GateClass.Complex, "family expansion");
        Classify(T("a"), new SearchOptions { Facets = ["responsiveness"] }).Should().Be(GateClass.Complex, "facets");
        Classify(T("a"), new SearchOptions { Sort = [new SortKey("review_priority", "asc")] }).Should().Be(GateClass.Complex, "grid sort");
    }

    [Fact]
    public void Printer_emits_canonical_ADR_008_text_with_minimal_parentheses()
    {
        OqlPrinter.Print(new OqlAnd([new OqlOr([T("a"), T("b")]), new OqlNot(T("c"))])).Should().Be("(a OR b) AND NOT c");
        OqlPrinter.Print(new OqlAnd([T("a"), T("b")], Explicit: false)).Should().Be("a b");
        OqlPrinter.Print(new OqlOr([new OqlAnd([T("a"), T("b")]), new OqlAnd([T("c"), T("d")])])).Should().Be("a AND b OR c AND d");
        OqlPrinter.Print(new OqlProximity(new OqlOr([T("apple"), T("pear")]), T("iphone"), 5)).Should().Be("(apple OR pear) W/5 iphone");
        OqlPrinter.Print(new OqlField("custodian", T("John Smith"))).Should().Be("custodian:\"John Smith\"");
        OqlPrinter.Print(new OqlField("issues", new OqlOr([T("Pricing"), T("Export Control")]))).Should().Be("issues:(Pricing OR \"Export Control\")");
        OqlPrinter.Print(new OqlField("date", new OqlRange("2025-01-01", "2025-12-31"))).Should().Be("date:[2025-01-01 TO 2025-12-31]");
        OqlPrinter.Print(new OqlField("filename", new OqlWildcard("*.xlsx"))).Should().Be("filename:*.xlsx");
        OqlPrinter.Print(T("AND")).Should().Be("\"AND\"", "upper-case operator words are quoted (R1)");
        OqlPrinter.Print(new OqlPhrase("say \"hi\"")).Should().Be("\"say \\\"hi\\\"\"");
    }

    [Fact]
    public void Think_time_is_log_normal_with_median_20s_and_p90_60s()
    {
        Math.Exp(ReviewerSessionModel.Mu).Should().BeApproximately(20, 1e-9);
        Math.Exp(ReviewerSessionModel.Mu + (ReviewerSessionModel.Z90 * ReviewerSessionModel.Sigma)).Should().BeApproximately(60, 1e-9);
        ReviewerSessionModel.Sigma.Should().BeApproximately(0.8572, 1e-4);
    }

    [Theory]
    [InlineData(0.00009, SelectivityBand.Needle)]
    [InlineData(0.0001, SelectivityBand.Narrow)]
    [InlineData(0.0099, SelectivityBand.Narrow)]
    [InlineData(0.01, SelectivityBand.Medium)]
    [InlineData(0.1, SelectivityBand.Broad)]
    [InlineData(0.5, SelectivityBand.VeryBroad)]
    public void Selectivity_bands_have_fixed_boundaries(double fraction, SelectivityBand band) => QueryTaxonomy.Band(fraction).Should().Be(band);

    [Fact]
    public void Tokenizer_approximates_the_standard_tokenizer()
    {
        TextTokenizer.Tokens("Re: The vex-tomarin O'Bralvix report, v1.5 john.smith@corp.example!").Should().Equal(
            "re", "the", "vex", "tomarin", "o'bralvix", "report", "v1.5", "john.smith", "corp.example");
    }
}
