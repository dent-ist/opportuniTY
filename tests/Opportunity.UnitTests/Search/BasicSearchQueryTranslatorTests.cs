using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;
using Opportunity.Search.Querying;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// The first-slice translator accepts the clauses the review grid's filter row compiles (#191): the catalogue query
/// names of the structural fields (<c>date</c>, <c>extension</c>) and leading/infix wildcards on <c>filename</c>
/// (ADR-008 R7, <c>fileName.wc</c>), while the R7 prefix rule still holds on full text.
/// </summary>
public sealed class BasicSearchQueryTranslatorTests
{
    private static readonly SearchTranslationContext Context =
        new(Guid.Parse("0199a8a0-1111-7000-8000-000000000001"), 1, QueryLimits.Default);

    private static async Task<SearchTranslation> TranslateAsync(string text)
    {
        var parsed = QueryParser.Parse(text);
        parsed.Ast.Should().NotBeNull(text);
        return await new BasicSearchQueryTranslator().TranslateAsync(parsed.Ast!, Context, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Catalogue_query_names_resolve_to_their_projection_fields()
    {
        var date = await TranslateAsync("date:[2024-01-01 TO *]");
        date.Success.Should().BeTrue();
        date.Query!["range"]!["documentDate"]!["gte"]!.GetValue<string>().Should().Be("2024-01-01T00:00:00.000Z");

        var extension = await TranslateAsync("extension:pd*");
        extension.Success.Should().BeTrue();
        extension.Query!["wildcard"]!["fileExtension"]!["value"]!.GetValue<string>().Should().Be("pd*");
    }

    [Fact]
    public async Task File_name_takes_leading_and_infix_wildcards_on_its_wildcard_subfield()
    {
        var contains = await TranslateAsync(@"filename:*quarterly\ terms*");
        contains.Success.Should().BeTrue(string.Join("; ", contains.Errors.Select(e => e.Message)));
        contains.Query!["wildcard"]![ProjectionFields.FileNameWildcard]!["value"]!.GetValue<string>().Should().Be("*quarterly terms*");

        var text = await TranslateAsync("text:*erger");
        text.Success.Should().BeFalse();
        text.Errors.Should().ContainSingle().Which.Code.Should().Be(SearchQueryErrorCodes.LeadingWildcard);
    }
}
