using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.QueryLanguage;

/// <summary>Malformed queries return one positioned error (code, exact span, expected tokens) for the query bar.</summary>
public class QueryErrorTests
{
    [Theory]
    // structure
    [InlineData("contract AND", QueryErrorCodes.SyntaxError, 12, 12)]
    [InlineData("contract AND OR x", QueryErrorCodes.SyntaxError, 13, 15)]
    [InlineData("OR contract", QueryErrorCodes.SyntaxError, 0, 2)]
    [InlineData("NOT", QueryErrorCodes.SyntaxError, 3, 3)]
    [InlineData("a W/3", QueryErrorCodes.SyntaxError, 5, 5)]
    [InlineData("W/3 a", QueryErrorCodes.SyntaxError, 0, 3)]
    [InlineData("a TO b", QueryErrorCodes.SyntaxError, 2, 4)]
    [InlineData("(a OR b", QueryErrorCodes.UnbalancedParenthesis, 0, 1)]
    [InlineData("a OR (b AND (c)", QueryErrorCodes.UnbalancedParenthesis, 5, 6)]
    [InlineData("a OR b)", QueryErrorCodes.UnbalancedParenthesis, 6, 7)]
    [InlineData(")", QueryErrorCodes.UnbalancedParenthesis, 0, 1)]
    [InlineData("()", QueryErrorCodes.SyntaxError, 1, 2)]
    [InlineData("(a ]", QueryErrorCodes.SyntaxError, 3, 4)]
    // phrases and escapes (R4)
    [InlineData("x \"trade secret", QueryErrorCodes.UnterminatedPhrase, 2, 15)]
    [InlineData("\"abc\\\"", QueryErrorCodes.UnterminatedPhrase, 0, 6)]
    [InlineData("\"\"", QueryErrorCodes.EmptyPhrase, 0, 2)]
    [InlineData("a \"  \"", QueryErrorCodes.EmptyPhrase, 2, 6)]
    [InlineData("abc\\", QueryErrorCodes.DanglingEscape, 3, 4)]
    [InlineData("a -draft", QueryErrorCodes.ReservedPrefix, 2, 3)]
    [InlineData("+draft", QueryErrorCodes.ReservedPrefix, 0, 1)]
    [InlineData("10:00", QueryErrorCodes.SyntaxError, 2, 3)]
    [InlineData("a :b", QueryErrorCodes.SyntaxError, 2, 3)]
    [InlineData("a\\b:c", QueryErrorCodes.SyntaxError, 3, 4)]
    // unsupported syntax (R4, §7)
    [InlineData("apple PRE/5 iphone", QueryErrorCodes.UnsupportedSyntax, 6, 11)]
    [InlineData("apple pre/5 iphone", QueryErrorCodes.UnsupportedSyntax, 6, 11)]
    [InlineData("apple W/s iphone", QueryErrorCodes.UnsupportedSyntax, 6, 9)]
    [InlineData("apple w/P iphone", QueryErrorCodes.UnsupportedSyntax, 6, 9)]
    [InlineData("appl!", QueryErrorCodes.UnsupportedSyntax, 4, 5)]
    [InlineData("apple~2", QueryErrorCodes.UnsupportedSyntax, 5, 6)]
    [InlineData("apple^2", QueryErrorCodes.UnsupportedSyntax, 5, 6)]
    [InlineData("\"meet* now\"", QueryErrorCodes.UnsupportedSyntax, 5, 6)]
    [InlineData("\"is it?\"", QueryErrorCodes.UnsupportedSyntax, 6, 7)]
    // proximity (R2, R6)
    [InlineData("a W/3 b W/5 c", QueryErrorCodes.UnsupportedSyntax, 8, 11)]
    [InlineData("(a AND b) W/3 c", QueryErrorCodes.UnsupportedSyntax, 0, 9)]
    [InlineData("(a b) W/3 c", QueryErrorCodes.UnsupportedSyntax, 0, 5)]
    [InlineData("NOT a W/3 b", QueryErrorCodes.UnsupportedSyntax, 0, 5)]
    [InlineData("a W/3 NOT b", QueryErrorCodes.UnsupportedSyntax, 6, 11)]
    [InlineData("(a W/2 b) W/3 c", QueryErrorCodes.UnsupportedSyntax, 0, 9)]
    [InlineData("(a OR (b AND c)) W/3 d", QueryErrorCodes.UnsupportedSyntax, 6, 15)]
    [InlineData("custodian:smith W/3 jones", QueryErrorCodes.InvalidProximityOperand, 0, 15)]
    [InlineData("a W/0 b", QueryErrorCodes.ProximityDistanceOutOfRange, 2, 5)]
    [InlineData("a W/1001 b", QueryErrorCodes.ProximityDistanceOutOfRange, 2, 8)]
    [InlineData("a W/99999999999999 b", QueryErrorCodes.ProximityDistanceOutOfRange, 2, 18)]
    // fields and ranges
    [InlineData("custodian: smith", QueryErrorCodes.SyntaxError, 10, 11)]
    [InlineData("custodian:", QueryErrorCodes.SyntaxError, 10, 10)]
    [InlineData("custodian:)", QueryErrorCodes.SyntaxError, 10, 11)]
    [InlineData("a:b:c", QueryErrorCodes.NestedField, 2, 4)]
    [InlineData("custodian:(smith OR date:2025)", QueryErrorCodes.NestedField, 20, 25)]
    [InlineData("[a TO b]", QueryErrorCodes.InvalidRange, 0, 1)]
    [InlineData("date:[2020 to 2021]", QueryErrorCodes.InvalidRange, 11, 13)]
    [InlineData("date:[2020 2021]", QueryErrorCodes.InvalidRange, 11, 15)]
    [InlineData("date:[2020* TO 2021]", QueryErrorCodes.InvalidRange, 6, 11)]
    [InlineData("date:[2020 TO 2021", QueryErrorCodes.InvalidRange, 5, 18)]
    [InlineData("date:[2020 TO 2021)", QueryErrorCodes.InvalidRange, 18, 19)]
    [InlineData("date:[TO 2021]", QueryErrorCodes.InvalidRange, 6, 8)]
    // raw DSL is never accepted
    [InlineData("{\"query\":{\"match_all\":{}}}", QueryErrorCodes.InvalidRange, 0, 1)]
    public void Malformed_query_returns_a_positioned_error(string query, string code, int start, int end)
    {
        var result = QueryParser.Parse(query);

        result.Success.Should().BeFalse(query);
        result.Ast.Should().BeNull();
        var error = result.Errors.Should().ContainSingle().Subject;
        (error.Code, error.Span).Should().Be((code, new SourceSpan(start, end)), $"{query}: {error.Message}");
        error.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("contract AND", new[] { "term", "phrase", "field:", "(", "NOT" })]
    [InlineData("(a OR b", new[] { ")" })]
    [InlineData("\"abc", new[] { "\"" })]
    [InlineData("custodian:", new[] { "term", "phrase", "(", "[", "{", "*" })]
    [InlineData("date:[2020 to 2021]", new[] { "TO" })]
    [InlineData("date:[2020 TO 2021", new[] { "]", "}" })]
    [InlineData("(a ]", new[] { "AND", "OR", "W/n", "term", "phrase", "field:", "(", "NOT", ")" })]
    public void Errors_list_the_expected_tokens(string query, string[] expected)
    {
        QueryParser.Parse(query).Errors[0].Expected.Should().Equal(expected);
    }

    [Fact]
    public void The_first_error_in_text_order_is_reported()
    {
        var result = QueryParser.Parse("a AND ) \"unterminated");

        result.Errors.Should().ContainSingle().Which.Span.Should().Be(new SourceSpan(6, 7));
    }

    [Fact]
    public void Error_messages_suggest_the_fix()
    {
        Message("-draft").Should().Contain("NOT");
        Message("a W/3 b W/5 c").Should().Contain("AND");
        Message("custodian:smith W/3 jones").Should().Contain("custodian:(a W/n b)");
        Message("date:[2020 to 2021]").Should().Contain("upper case");
        Message("10:00").Should().Contain("\\:");
        Message("contract AND OR x").Should().Contain("\"OR\"");
    }

    private static string Message(string query) => QueryParser.Parse(query).Errors[0].Message;
}
