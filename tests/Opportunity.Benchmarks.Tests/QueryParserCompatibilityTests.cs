using AwesomeAssertions;

using Opportunity.Benchmarks.Workloads;
using Opportunity.Core.QueryLanguage;

namespace Opportunity.Benchmarks.Tests;

/// <summary>
/// The benchmark query generator emits ADR-008 OQL (docs/benchmarks/query-taxonomy.md); the product parser (E07-T06)
/// must accept every query it produces, with the same structure and therefore the same gate class.
/// </summary>
public class QueryParserCompatibilityTests
{
    private static OqlTerm T(string text) => new(text);

    public static TheoryData<string> PrinterExamples => new(
        new OqlNode[]
        {
            new OqlAnd([T("contract"), T("termination")]),
            new OqlPhrase("trade secret"),
            new OqlProximity(T("apple"), T("iphone"), 10),
            new OqlField("custodian", T("John Smith")),
            new OqlField("date", new OqlRange("2025-01-01", "2025-12-31")),
            new OqlField("filename", new OqlWildcard("*.xlsx")),
            new OqlAnd([new OqlOr([T("a"), T("b")]), new OqlNot(T("c"))]),
            new OqlAnd([T("a"), T("b")], Explicit: false),
            new OqlAnd([T("a"), new OqlNot(T("b"))], Explicit: false),
            new OqlOr([new OqlAnd([T("a"), T("b")]), new OqlAnd([T("c"), T("d")])]),
            new OqlProximity(new OqlOr([T("apple"), T("pear")]), T("iphone"), 5),
            new OqlField("issues", new OqlOr([T("Pricing"), T("Export Control")])),
            new OqlAnd([new OqlWildcard("cris*"), T("weandplai")]),
            new OqlField("filename", new OqlWildcard("*word*")),
            new OqlField("controlnumber", new OqlRange("BENCH0000001", "BENCH0005000")),
            new OqlField("responsiveness", T("Not Responsive")),
            T("AND"),
            T("vex-tomarin"),
            T("o'bralvix"),
            new OqlPhrase("say \"hi\""),
        }.Select(OqlPrinter.Print));

    [Theory]
    [MemberData(nameof(PrinterExamples))]
    public void Benchmark_printer_output_parses_back_to_the_same_query(string oql)
    {
        var result = QueryParser.Parse(oql);

        result.Errors.Should().BeEmpty(oql);
        result.Warnings.Should().BeEmpty(oql);
        WithoutAnd(OqlPrinter.Print(ToOql(result.Ast!))).Should().Be(WithoutAnd(oql));
    }

    [Fact]
    public void Every_generated_benchmark_query_parses_with_the_same_structure_and_gate_class()
    {
        QuerySet set = QueryTestCorpus.QuerySet;
        IReadOnlySet<string> coding = set.CodingFixture.QueryNames;

        set.Queries.Should().NotBeEmpty();
        foreach (BenchmarkQuery query in set.Queries)
        {
            QueryParseResult result = QueryParser.Parse(query.Oql);

            result.Errors.Should().BeEmpty($"{query.Id} ({query.Class}): {query.Oql}");
            result.Warnings.Should().BeEmpty(query.Oql);
            OqlNode oql = ToOql(result.Ast!);
            WithoutAnd(OqlPrinter.Print(oql)).Should().Be(WithoutAnd(query.Oql), "the parsed tree is the generated tree");
            var options = new SearchOptions { Expand = query.Expand, Sort = query.Sort, Facets = query.Facets };
            QueryClassifier.Classify(oql, options, coding).Name().Should().Be(query.Gate, query.Oql);
            QueryAstComparer.IgnoringSpans.Equals(QueryParser.Parse(QueryPrinter.Print(result.Ast!)).Ast, result.Ast).Should().BeTrue(query.Oql);
        }

        set.Queries.Where(q => q.Class == "proximity").Should().OnlyContain(q => QueryParser.Parse(q.Oql).Ast is ProximityNode);
        set.Queries.Where(q => q.Class == "wildcard").Should().OnlyContain(q => QueryPrinter.Print(QueryParser.Parse(q.Oql).Ast!).Contains('*'));
    }

    /// <summary>The product AST does not keep whether an AND was implicit (R3), so texts are compared with AND as a space.</summary>
    private static string WithoutAnd(string oql) => oql.Replace(" AND ", " ", StringComparison.Ordinal);

    /// <summary>Product AST to the generator's AST (no spans; phrases and terms map one to one).</summary>
    private static OqlNode ToOql(QueryNode node) => node switch
    {
        TermNode t => new OqlTerm(t.Text),
        PhraseNode p => new OqlPhrase(p.Text),
        WildcardNode w => new OqlWildcard(w.Pattern),
        ExistsNode => new OqlWildcard("*"),
        RangeNode r => new OqlRange(r.Lower?.Value, r.Upper?.Value),
        FieldNode f => new OqlField(f.Name, ToOql(f.Child)),
        AndNode a => new OqlAnd([.. a.Children.Select(ToOql)]),
        OrNode o => new OqlOr([.. o.Children.Select(ToOql)]),
        NotNode n => new OqlNot(ToOql(n.Child)),
        ProximityNode x => new OqlProximity(ToOql(x.Left), ToOql(x.Right), x.Distance),
        _ => throw new ArgumentException(node.Kind.ToString(), nameof(node)),
    };
}
