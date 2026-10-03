using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;

namespace Opportunity.UnitTests.QueryLanguage;

/// <summary>ADR-008 §5 limits: violations are positioned errors with a clear code.</summary>
public class QueryLimitsTests
{
    [Fact]
    public void Defaults_are_the_ADR_008_values()
    {
        var limits = QueryLimits.Default;

        limits.MaxLength.Should().Be(10_000);
        limits.MaxNodes.Should().Be(1_024);
        limits.MaxDepth.Should().Be(32);
        limits.MaxProximityDistance.Should().Be(1_000);
        limits.MaxWildcardExpansion.Should().Be(4_096);
        limits.MinWildcardPrefix.Should().Be(3);
    }

    [Fact]
    public void Query_length_is_limited_and_the_span_marks_the_excess()
    {
        QueryParser.Parse(new string('a', 10_000)).Success.Should().BeTrue();

        var error = QueryParser.Parse(new string('a', 10_005)).Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(QueryErrorCodes.QueryTooLong);
        error.Span.Should().Be(new SourceSpan(10_000, 10_005));
    }

    [Fact]
    public void Parenthesis_nesting_is_limited_before_recursion_can_overflow()
    {
        static string Nested(int depth) => new string('(', depth) + "a" + new string(')', depth);

        QueryParser.Parse(Nested(32)).Success.Should().BeTrue();
        var error = QueryParser.Parse(Nested(33)).Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(QueryErrorCodes.QueryTooDeep);
        error.Span.Should().Be(new SourceSpan(32, 33));

        QueryParser.Parse(Nested(4_000)).Errors.Should().ContainSingle().Which.Code.Should().Be(QueryErrorCodes.QueryTooDeep);
    }

    [Fact]
    public void Not_chains_are_limited()
    {
        static string Nots(int count) => string.Concat(Enumerable.Repeat("NOT ", count)) + "a";

        QueryParser.Parse(Nots(31)).Success.Should().BeTrue("31 NOTs and the term are 32 AST levels");
        QueryParser.Parse(Nots(2_000)).Errors.Should().ContainSingle().Which.Code.Should().Be(QueryErrorCodes.QueryTooDeep);
    }

    [Fact]
    public void Ast_depth_is_limited()
    {
        var limits = new QueryLimits { MaxDepth = 3 };

        QueryParser.Parse("a OR (b AND c)", limits).Success.Should().BeTrue();
        var error = QueryParser.Parse("a OR (b AND (c OR d))", limits).Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(QueryErrorCodes.QueryTooDeep);
        error.Span.Should().Be(new SourceSpan(13, 14), "the first node below the limit is 'c'");
    }

    [Fact]
    public void Node_count_is_limited()
    {
        var limits = new QueryLimits { MaxNodes = 5 };

        QueryParser.Parse("a OR b OR c OR d", limits).Success.Should().BeTrue();
        var error = QueryParser.Parse("a OR b OR c OR d OR e", limits).Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(QueryErrorCodes.TooManyNodes);
        error.Span.Should().Be(new SourceSpan(20, 21));
    }

    [Fact]
    public void Term_count_is_limited()
    {
        static string Terms(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => $"t{i}"));

        QueryParser.Parse(Terms(512)).Success.Should().BeTrue();
        var query = Terms(513);
        var error = QueryParser.Parse(query).Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(QueryErrorCodes.TooManyTerms);
        query[error.Span.Start..error.Span.End].Should().Be("t512");
    }

    [Fact]
    public void Wildcard_term_count_is_limited()
    {
        static string Wildcards(int count) => string.Join(" OR ", Enumerable.Range(0, count).Select(i => $"abc{i}*"));

        QueryParser.Parse(Wildcards(32)).Success.Should().BeTrue();
        var query = Wildcards(33);
        var error = QueryParser.Parse(query).Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(QueryErrorCodes.TooManyWildcards);
        query[error.Span.Start..error.Span.End].Should().Be("abc32*");
    }

    [Fact]
    public void Proximity_distance_limit_is_configurable()
    {
        var limits = new QueryLimits { MaxProximityDistance = 10 };

        QueryParser.Parse("a W/10 b", limits).Success.Should().BeTrue();
        QueryParser.Parse("a W/11 b", limits).Errors.Should().ContainSingle().Which.Code.Should().Be(QueryErrorCodes.ProximityDistanceOutOfRange);
    }
}
