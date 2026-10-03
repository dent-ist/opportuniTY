using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.QueryLanguage;

/// <summary>One theory row per production and disambiguation rule of the ADR-008 §1 grammar.</summary>
public class QueryGrammarTests
{
    [Theory]
    // query = ws [or_expr] ws; blank = match all (R5)
    [InlineData("", "(all)")]
    [InlineData("   \t\n", "(all)")]
    [InlineData("  contract  ", "(term contract)")]
    // or_expr / and_expr, left-associative and flat (R2)
    [InlineData("a OR b", "(or (term a) (term b))")]
    [InlineData("a OR b OR c", "(or (term a) (term b) (term c))")]
    [InlineData("a AND b AND c", "(and (term a) (term b) (term c))")]
    [InlineData("a OR b AND c", "(or (term a) (and (term b) (term c)))")]
    [InlineData("a AND b OR c AND d", "(or (and (term a) (term b)) (and (term c) (term d)))")]
    // implicit AND (R3), mixable with explicit AND
    [InlineData("contract termination", "(and (term contract) (term termination))")]
    [InlineData("a b AND c", "(and (term a) (term b) (term c))")]
    [InlineData("a b OR c", "(or (and (term a) (term b)) (term c))")]
    // unary NOT; purely negative queries allowed (R5)
    [InlineData("NOT privileged", "(not (term privileged))")]
    [InlineData("NOT NOT a", "(not (not (term a)))")]
    [InlineData("a NOT b", "(and (term a) (not (term b)))")]
    [InlineData("NOT a b", "(and (not (term a)) (term b))")]
    [InlineData("NOT a OR b", "(or (not (term a)) (term b))")]
    // prox_expr: W/n binds tighter than AND, looser than NOT; case-insensitive W (R1)
    [InlineData("apple W/10 iphone", "(w/10 (term apple) (term iphone))")]
    [InlineData("apple w/5 iphone", "(w/5 (term apple) (term iphone))")]
    [InlineData("a b W/3 c", "(and (term a) (w/3 (term b) (term c)))")]
    [InlineData("a W/3 b OR c", "(or (w/3 (term a) (term b)) (term c))")]
    [InlineData("apple W/007 iphone", "(w/7 (term apple) (term iphone))")]
    [InlineData("a W/1000 b", "(w/1000 (term a) (term b))")]
    [InlineData("(apple OR pear) W/5 iphone", "(w/5 (or (term apple) (term pear)) (term iphone))")]
    [InlineData("\"trade secret\" W/3 misappropriat*", "(w/3 (phrase trade secret) (wildcard misappropriat*))")]
    [InlineData("subject:(merger W/3 acquisition)", "(field subject (w/3 (term merger) (term acquisition)))")]
    // group
    [InlineData("(a OR b) AND (c OR d)", "(and (or (term a) (term b)) (or (term c) (term d)))")]
    [InlineData("((a))", "(term a)")]
    [InlineData("a AND (b AND c)", "(and (term a) (and (term b) (term c)))")]
    [InlineData("NOT(a)", "(not (term a))")]
    [InlineData("a(b)", "(and (term a) (term b))")]
    // field_expr: term, phrase, group, range, exists
    [InlineData("custodian:smith", "(field custodian (term smith))")]
    [InlineData("custodian:\"John Smith\"", "(field custodian (phrase John Smith))")]
    [InlineData("custodian:(smith OR jones)", "(field custodian (or (term smith) (term jones)))")]
    [InlineData("privilege:*", "(field privilege (exists))")]
    [InlineData("filename:*.xlsx", "(field filename (wildcard *.xlsx))")]
    [InlineData("filename:*budget*", "(field filename (wildcard *budget*))")]
    [InlineData("meta.sub_field-2:x", "(field meta.sub_field-2 (term x))")]
    [InlineData("Custodian:x", "(field Custodian (term x))")]
    [InlineData("NOT custodian:smith", "(not (field custodian (term smith)))")]
    [InlineData("custodian:smith jones", "(and (field custodian (term smith)) (term jones))")]
    // range: inclusive/exclusive, mixable, open bounds, phrase bounds
    [InlineData("date:[2025-01-01 TO 2025-12-31]", "(field date (range [2025-01-01 2025-12-31]))")]
    [InlineData("amount:{100 TO 200.5}", "(field amount (range {100 200.5}))")]
    [InlineData("controlnumber:[ABC0001 TO ABC0100}", "(field controlnumber (range [ABC0001 ABC0100}))")]
    [InlineData("date:[2025-01-01 TO *]", "(field date (range [2025-01-01 *))")]
    [InlineData("date:[* TO 2025]", "(field date (range * 2025]))")]
    [InlineData("date:[ 2025-03 TO 2025 ]", "(field date (range [2025-03 2025]))")]
    [InlineData("sent:[\"2025-03-01T09:00:00+01:00\" TO *]", "(field sent (range [2025-03-01T09:00:00+01:00 *))")]
    [InlineData("x:[a\\* TO b]", "(field x (range [a* b]))")]
    // phrase and escapes (R4)
    [InlineData("\"trade secret\"", "(phrase trade secret)")]
    [InlineData("\"say \\\"hi\\\"\"", "(phrase say \"hi\")")]
    [InlineData("\"C:\\\\temp\"", "(phrase C:\\temp)")]
    [InlineData("\"a\\b\"", "(phrase a\\b)")]
    [InlineData("\"5\\* rating\"", "(phrase 5* rating)")]
    [InlineData("AT\\&T", "(term AT&T)")]
    [InlineData("10\\:00", "(term 10:00)")]
    [InlineData("\\(draft\\)", "(term (draft))")]
    [InlineData("\\-draft", "(term -draft)")]
    [InlineData("e-mail", "(term e-mail)")]
    [InlineData("o'brien", "(term o'brien)")]
    [InlineData("a\\ b", "(term a b)")]
    [InlineData("W\\/5", "(term W/5)")]
    [InlineData("\\AND", "(term AND)")]
    [InlineData("\"AND\"", "(phrase AND)")]
    // wildcards (R7): * and ?, escaped wildcard characters are literal
    [InlineData("contr*", "(wildcard contr*)")]
    [InlineData("wom?n", "(wildcard wom?n)")]
    [InlineData("5\\*", "(term 5*)")]
    [InlineData("a\\*b*", "(wildcard a\\*b*)")]
    [InlineData("a\\\\b*", "(wildcard a\\\\b*)")]
    [InlineData("*", "(wildcard *)")]
    // R1: lower-case and/or/not are terms
    [InlineData("contract and termination", "(and (term contract) (term and) (term termination))")]
    [InlineData("this or that", "(and (term this) (term or) (term that))")]
    // non-ASCII
    [InlineData("Müller straße", "(and (term Müller) (term straße))")]
    public void Parses_to_the_expected_tree(string query, string expected)
    {
        var result = QueryParser.Parse(query);

        result.Errors.Should().BeEmpty(query);
        Sx.Of(result.Ast!).Should().Be(expected);
    }

    [Theory]
    [InlineData("contract and termination", "and", 9, 12, "AND")]
    [InlineData("this Or that", "Or", 5, 7, "OR")]
    [InlineData("not privileged", "not", 0, 3, "NOT")]
    public void Lower_case_operators_are_terms_with_a_positioned_warning_R1(string query, string word, int start, int end, string suggestion)
    {
        var result = QueryParser.Parse(query);

        result.Success.Should().BeTrue();
        var warning = result.Warnings.Should().ContainSingle().Subject;
        warning.Code.Should().Be(QueryWarningCodes.LowercaseOperator);
        warning.Span.Should().Be(new SourceSpan(start, end));
        query[start..end].Should().Be(word);
        warning.Message.Should().Contain(suggestion);
    }

    [Fact]
    public void Escaped_lower_case_operator_has_no_warning()
    {
        QueryParser.Parse("\\and").Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Spans_are_utf16_offsets_into_the_original_text()
    {
        var ast = QueryParser.Parse("x (apple OR pear) W/5 \"John Smith\"").Ast.Should().BeOfType<AndNode>().Subject;

        ast.Span.Should().Be(new SourceSpan(0, 34));
        var proximity = ast.Children[1].Should().BeOfType<ProximityNode>().Subject;
        proximity.Span.Should().Be(new SourceSpan(2, 34));
        var or = proximity.Left.Should().BeOfType<OrNode>().Subject;
        or.Span.Should().Be(new SourceSpan(2, 17), "a group's span includes its parentheses");
        or.Children[1].Span.Should().Be(new SourceSpan(12, 16));
        proximity.Right.Span.Should().Be(new SourceSpan(22, 34));
    }

    [Fact]
    public void Field_span_covers_name_colon_and_value()
    {
        var field = QueryParser.Parse("  date:[2025 TO 2026}").Ast.Should().BeOfType<FieldNode>().Subject;

        field.Span.Should().Be(new SourceSpan(2, 21));
        field.Child.Span.Should().Be(new SourceSpan(7, 21));
    }

    [Fact]
    public void Wildcard_literal_prefix_counts_escaped_characters_once()
    {
        new WildcardNode("contr*", default).LiteralPrefixLength.Should().Be(5);
        new WildcardNode("*.xlsx", default).LiteralPrefixLength.Should().Be(0);
        new WildcardNode("a\\*b?", default).LiteralPrefixLength.Should().Be(3);
    }

    [Fact]
    public void Proximity_is_unordered_and_keeps_the_distance()
    {
        var node = QueryParser.Parse("apple W/10 iphone").Ast.Should().BeOfType<ProximityNode>().Subject;

        node.Distance.Should().Be(10);
        node.Ordered.Should().BeFalse("W/n is unordered (ADR-008 §2); PRE/n is the reserved ordered extension");
    }

    [Fact]
    public void Raw_OpenSearch_DSL_is_not_a_query()
    {
        QueryParser.Parse("{\"query\":{\"match_all\":{}}}").Success.Should().BeFalse();
        QueryParser.Parse("{\"bool\":{\"must_not\":[{\"term\":{\"workspaceId\":\"w1\"}}]}}").Success.Should().BeFalse();
    }
}
