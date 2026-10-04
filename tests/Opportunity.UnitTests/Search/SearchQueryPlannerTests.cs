using AwesomeAssertions;

using Opportunity.Application.Search;
using Opportunity.Core.Fields;
using Opportunity.Core.QueryLanguage;
using Opportunity.Search.Indexing;
using Opportunity.Search.Querying;

namespace Opportunity.UnitTests.Search;

/// <summary>E07-T07 planner rules the golden files do not show on their own: limits, classes, analysis and binding.</summary>
public sealed class SearchQueryPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SearchTranslation> PlanAsync(string text, QueryLimits? limits = null, SearchFieldOptions? options = null)
    {
        var parsed = QueryParser.Parse(text);
        parsed.Errors.Should().BeEmpty(text);
        return await PlannerFixture.Planner().TranslateAsync(
            parsed.Ast!, new SearchTranslationContext(PlannerFixture.Workspace, 2, limits ?? QueryLimits.Default, null, options), Ct);
    }

    [Fact]
    public async Task Clause_limit_counts_after_field_expansion_and_answers_with_a_positioned_error()
    {
        var query = "custodian:(" + string.Join(" OR ", Enumerable.Range(1, 6).Select(i => "c" + i)) + ")";
        var limits = QueryLimits.Default with { MaxClauses = 10 };

        (await PlanAsync(query, limits)).Success.Should().BeTrue("6 clauses without expansion");
        var expanded = await PlanAsync(query, limits, new SearchFieldOptions(CustodianIncludesAllCustodians: true));

        var error = expanded.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SearchQueryErrorCodes.TooManyClauses);
        error.Span.Should().Be(new SourceSpan(0, query.Length));
        error.Message.Should().Contain("12 clauses").And.Contain("at most 10");
    }

    [Fact]
    public async Task Every_error_of_a_query_is_reported_in_text_order()
    {
        var translation = await PlanAsync("nosuch:a AND *lead AND hot:maybe");

        translation.Query.Should().BeNull();
        translation.Errors.Select(e => e.Code).Should().Equal(
            SearchQueryErrorCodes.UnknownField, SearchQueryErrorCodes.LeadingWildcard, SearchQueryErrorCodes.InvalidFieldValue);
    }

    [Fact]
    public async Task Wildcards_inside_proximity_are_reported_as_bounded_expansions()
    {
        var translation = await PlanAsync("contr* W/5 (terminat* OR cancel)");

        translation.BoundedExpansions.Should().Equal(new SourceSpan(0, 6), new SourceSpan(12, 21));
        (await PlanAsync("contr* AND cancel")).BoundedExpansions.Should().BeEmpty("outside W/n constant_score never truncates or fails");
    }

    [Fact]
    public async Task Gate_classes_follow_the_benchmark_taxonomy()
    {
        (await PlanAsync("")).QueryClass.Should().Be(SearchTranslation.Simple);
        (await PlanAsync("contract")).QueryClass.Should().Be(SearchTranslation.Simple);
        (await PlanAsync("\"trade secret\" custodian:smith date:[2025 TO 2026]")).QueryClass.Should().Be(SearchTranslation.Simple);
        (await PlanAsync("a b c")).QueryClass.Should().Be(SearchTranslation.Complex);
        (await PlanAsync("a OR b")).QueryClass.Should().Be(SearchTranslation.Complex);
        (await PlanAsync("contract hot:true")).QueryClass.Should().Be(SearchTranslation.Complex, "coding filter");
        (await PlanAsync("apple W/10 iphone")).QueryClass.Should().Be(SearchTranslation.Complex);
        (await PlanAsync("contr*")).QueryClass.Should().Be(SearchTranslation.Complex);
    }

    [Fact]
    public async Task Coding_fields_of_a_generation_1_index_are_not_searchable_until_reindexed()
    {
        var parsed = QueryParser.Parse("hot:true").Ast!;

        var translation = await PlannerFixture.Planner().TranslateAsync(parsed, new SearchTranslationContext(PlannerFixture.Workspace, 1, QueryLimits.Default), Ct);

        translation.Errors.Should().ContainSingle().Which.Code.Should().Be(SearchQueryErrorCodes.UnsupportedForField);
    }

    [Fact]
    public async Task A_workspace_without_a_seeded_catalogue_still_searches_the_structural_fields()
    {
        var planner = PlannerFixture.Planner(new FieldCatalog([], []));
        var parsed = QueryParser.Parse("filename:*.xlsx AND controlnumber:ABC1 AND custodian:x").Ast!;

        var translation = await planner.TranslateAsync(parsed, new SearchTranslationContext(PlannerFixture.Workspace, 2, QueryLimits.Default), Ct);

        translation.Errors.Should().ContainSingle().Which.Code.Should().Be(SearchQueryErrorCodes.UnknownField);
    }

    [Fact]
    public void Query_analysis_reproduces_the_search_analyzers_of_the_mapping()
    {
        var mapping = ProjectionMappings.Embedded.Load(2);

        OpenSearchTextAnalyzer.AnalyzerFor(mapping, "text").ToJsonString().Should().Be(
            """{"tokenizer":{"type":"standard","max_token_length":255},"filter":["lowercase","asciifolding"]}""");
        OpenSearchTextAnalyzer.AnalyzerFor(mapping, "metadata.txt.s001").ToJsonString().Should().Be(
            OpenSearchTextAnalyzer.AnalyzerFor(mapping, "text").ToJsonString(), "Text slots search with opp_text_search too");
        OpenSearchTextAnalyzer.AnalyzerFor(mapping, "metadata.idt.s001")["tokenizer"]!["type"]!.GetValue<string>().Should().Be("pattern");
        OpenSearchTextAnalyzer.AnalyzerFor(mapping, "fileName", AnalysisMode.Normalize).ToJsonString().Should().Be(
            """{"tokenizer":"keyword","filter":["lowercase","asciifolding"]}""");
        FluentActions.Invoking(() => OpenSearchTextAnalyzer.AnalyzerFor(mapping, "controlNumber")).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("contract", false, true)]
    [InlineData("3rd", false, true)]
    [InlineData("e-mail", false, false)]
    [InlineData("café", false, false)]
    [InlineData("東京", false, false)]
    [InlineData("contr*", true, true)]
    [InlineData("caf*", true, true)]
    [InlineData("café*", true, false)]
    public void Only_text_every_analyzer_keeps_as_one_lower_cased_token_skips_the_round_trip(string text, bool normalize, bool plain) =>
        OpenSearchTextAnalyzer.IsPlain(text, normalize ? AnalysisMode.Normalize : AnalysisMode.Tokens).Should().Be(plain);

    [Fact]
    public async Task The_validate_use_case_reports_binding_errors_with_positions()
    {
        var validator = new QueryValidator(QueryLimits.Default);
        var binder = new PlannerBinder();

        var unknown = await validator.ValidateAsync("contract AND custodain:smith", PlannerFixture.Workspace, binder, Ct);
        var ok = await validator.ValidateAsync("contract AND custodian:smith", PlannerFixture.Workspace, binder, Ct);
        var parseOnly = await validator.ValidateAsync("custodain:smith", PlannerFixture.Workspace, null, Ct);

        unknown.Valid.Should().BeFalse();
        var error = unknown.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SearchQueryErrorCodes.UnknownField);
        error.Span.Should().Be(new Contracts.Search.TextSpan(13, 22));
        error.Expected.Should().Equal("custodian");
        ok.Valid.Should().BeTrue();
        ok.Normalized.Should().Be("contract AND custodian:smith");
        parseOnly.Valid.Should().BeTrue("without a binder validation is parse-only");
    }

    private sealed class PlannerBinder : IQueryBinder
    {
        public async Task<IReadOnlyList<QueryDiagnostic>> BindAsync(Guid workspaceId, QueryNode ast, CancellationToken cancellationToken = default) =>
            (await PlannerFixture.Planner().TranslateAsync(ast, new SearchTranslationContext(workspaceId, 2, QueryLimits.Default), cancellationToken)).Errors;
    }
}
