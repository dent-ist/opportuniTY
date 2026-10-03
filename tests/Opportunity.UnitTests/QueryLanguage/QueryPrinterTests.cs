using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.QueryLanguage;

public class QueryPrinterTests
{
    [Theory]
    [InlineData("contract termination", "contract AND termination")]
    [InlineData("  a   OR b  ", "a OR b")]
    [InlineData("a OR (b AND c)", "a OR b AND c")]
    [InlineData("(a OR b) c", "(a OR b) AND c")]
    [InlineData("a AND (b AND c)", "a AND (b AND c)")]
    [InlineData("a OR (b OR c)", "a OR (b OR c)")]
    [InlineData("NOT (a b)", "NOT (a AND b)")]
    [InlineData("NOT(a)", "NOT a")]
    [InlineData("((a))", "a")]
    [InlineData("apple w/05 iphone", "apple W/5 iphone")]
    [InlineData("(apple OR pear) W/5 iphone", "(apple OR pear) W/5 iphone")]
    [InlineData("a (b W/2 c)", "a AND b W/2 c")]
    [InlineData("custodian:(smith)", "custodian:smith")]
    [InlineData("custodian:(smith OR jones)", "custodian:(smith OR jones)")]
    [InlineData("date:[ 2025-01-01 TO 2025-12-31 ]", "date:[2025-01-01 TO 2025-12-31]")]
    [InlineData("date:{* TO 2025}", "date:[* TO 2025}")]
    [InlineData("x:[\"a b\" TO \"c\"]", "x:[\"a b\" TO c]")]
    [InlineData("x:[\\* TO \\TO]", "x:[\"\\*\" TO \"TO\"]")]
    [InlineData("sent:[\"2025-03-01T09:00:00+01:00\" TO *]", "sent:[\"2025-03-01T09:00:00+01:00\" TO *]")]
    [InlineData("AT\\&T", "AT&T")]
    [InlineData("\\(draft\\)", "\\(draft\\)")]
    [InlineData("\"AND\" \\AND", "\"AND\" AND \\AND")]
    [InlineData("W\\/5", "\\W/5")]
    [InlineData("\\-draft e-mail", "\\-draft AND e-mail")]
    [InlineData("a\\ b", "a\\ b")]
    [InlineData("5\\* a\\*b*", "5\\* AND a\\*b*")]
    [InlineData("\"say \\\"hi\\\"\"", "\"say \\\"hi\\\"\"")]
    [InlineData("\"a\\b\"", "\"a\\\\b\"")]
    [InlineData("filename:*.xlsx privilege:*", "filename:*.xlsx AND privilege:*")]
    [InlineData("", "")]
    public void Prints_canonical_text(string query, string canonical)
    {
        var ast = QueryParser.Parse(query).Ast!;

        QueryPrinter.Print(ast).Should().Be(canonical);
        QueryAstComparer.IgnoringSpans.Equals(QueryParser.Parse(canonical).Ast, ast).Should().BeTrue();
    }

    [Fact]
    public void Reserved_extension_flags_print_their_future_syntax()
    {
        var ordered = new ProximityNode(new TermNode("a", default), new TermNode("b", default), 3, default, Ordered: true);

        QueryPrinter.Print(ordered).Should().Be("a PRE/3 b");
        QueryPrinter.Print(new TermNode("appl", default, RootExpand: true)).Should().Be("appl!");
        QueryParser.Parse("a PRE/3 b").Errors[0].Code.Should().Be(QueryErrorCodes.UnsupportedSyntax);
    }

    [Fact]
    public void Canonical_json_has_ast_version_kinds_and_spans()
    {
        var ast = QueryParser.Parse("custodian:\"John Smith\" AND date:[2025 TO *}").Ast!;

        QueryAstJson.Serialize(ast).Should().Be(
            """{"astVersion":1,"root":{"kind":"and","span":{"start":0,"end":43},"children":[""" +
            """{"kind":"field","span":{"start":0,"end":22},"name":"custodian","child":{"kind":"phrase","span":{"start":10,"end":22},"text":"John Smith"}},""" +
            """{"kind":"field","span":{"start":27,"end":43},"name":"date","child":{"kind":"range","span":{"start":32,"end":43},"lower":{"value":"2025","inclusive":true},"upper":null}}]}}""");
    }

    [Fact]
    public void Canonical_json_without_spans_is_equal_for_equal_trees()
    {
        var a = QueryParser.Parse("a   W/3 (b OR c)").Ast!;
        var b = QueryParser.Parse("a W/3 (b OR c)").Ast!;

        QueryAstJson.Serialize(a, includeSpans: false).Should().Be(QueryAstJson.Serialize(b, includeSpans: false));
        QueryAstJson.Serialize(a).Should().NotBe(QueryAstJson.Serialize(b));
        QueryAstComparer.WithSpans.Equals(a, b).Should().BeFalse();
        QueryAstComparer.IgnoringSpans.Equals(a, b).Should().BeTrue();
    }
}
