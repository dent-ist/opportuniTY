using System.Net.Http.Json;
using System.Text.Json;

using AwesomeAssertions;

using Opportunity.Application.Search.Indexing;
using Opportunity.Core.Highlighting;
using Opportunity.IntegrationTests.Containers;
using Opportunity.Search.Projection;
using Opportunity.Testing.OpenSearch;

namespace Opportunity.IntegrationTests.Search;

/// <summary>
/// E16-T12: the in-process tokenizer that places term hits agrees with OpenSearch's own search analyzer of the
/// projection (<c>opp_text_search</c> on <c>text</c>), token by token and offset by offset, so the viewer marks what
/// the search matched (case and accent folding included).
/// </summary>
[Collection(OpenSearchCollectionDefinition.Name)]
public sealed class HighlightAnalyzerContractTests(OpenSearchFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Don't e-mail john.smith@example.com about 3.14, 1,000 items_v2 (Résumé) ÆON straße.")]
    [InlineData("The PRICE-increase; naïve café x86_64 U.S.A. 2024-03-01 at 12:30 — O'Neill’s memo")]
    [InlineData("合同终止 カタカナ ひらがな Ελληνικά Кириллица עברית")]
    [InlineData("Termination\tof\nthe\r\nagreement... Terminated? TERMINATING! terminat_ion")]
    public async Task Tokens_and_offsets_match_the_search_analyzer(string text)
    {
        await using var harness = await IndexHarness.CreateAsync(fixture);
        var placement = await harness.Manager.PlaceAsync(Guid.CreateVersion7(), new WorkspacePlacementRequest(), Ct);
        using var response = await harness.Http.PostAsJsonAsync(
            $"{placement.WriteTargets[0].Index}/_analyze", new { analyzer = "opp_text_search", text }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue(body);
        using var json = JsonDocument.Parse(body);
        var expected = json.RootElement.GetProperty("tokens").EnumerateArray()
            .Select(t => (t.GetProperty("token").GetString()!, t.GetProperty("start_offset").GetInt64(), t.GetProperty("end_offset").GetInt64()))
            .ToList();

        var actual = HitTextAnalyzer.Tokenize(text).Select(t => (t.Folded, t.Start, t.End)).ToList();

        actual.Should().Equal(expected);
        placement.Generation.Should().Be(CandidateAProjectionBuilder.ProjectionGeneration);
    }
}
