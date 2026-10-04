using AwesomeAssertions;

using Opportunity.Core.QueryLanguage;
using Opportunity.Search.Querying;

namespace Opportunity.UnitTests.Search;

/// <summary>#193: without an explicit sort, relevance only when the query has a keyword, else Control Number ascending.</summary>
public sealed class DefaultSortTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("filetype:Email", false)]
    [InlineData("filetype:Email AND NOT contract", false)]
    [InlineData("date:[2018-01-01 TO 2018-12-31]", false)]
    [InlineData("contract", true)]
    [InlineData("\"supply contract\"", true)]
    [InlineData("contr*", true)]
    [InlineData("filetype:Email AND contract", true)]
    [InlineData("filetype:Email OR (contract W/3 termination)", true)]
    public void Relevance_needs_a_keyword(string query, bool relevance)
    {
        var ast = QueryParser.Parse(query).Ast!;
        var sort = SortKey.DefaultFor(ast);

        sort.Should().Be(relevance ? SortKey.Default : SortKey.ControlNumber);
        if (!relevance)
        {
            sort.Path.Should().Be(ProjectionFields.ControlNumberSort);
            sort.Descending.Should().BeFalse();
        }
    }
}
