using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Authorization;
using Opportunity.Contracts.Search;
using Opportunity.Core.QueryLanguage;
using Opportunity.Search.Querying;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// E07-T05 / ADR-006 R7, ADR-015 D8.1: whatever the user's query, the DSL the search service sends has the authenticated
/// workspace term first in its top-level <c>bool.filter</c>, the user clause only in <c>bool.must</c>, and nothing a
/// query can OR or NOT away. Every request variant (first page, search_after, reversed, page jump, highlight, facets)
/// uses the same filtered query.
/// </summary>
public sealed class SearchDslTests
{
    private static readonly Guid Workspace = Guid.Parse("0199a8a0-1111-7000-8000-000000000001");
    private static readonly Guid Other = Guid.Parse("0199a8a0-2222-7000-8000-000000000002");
    private static readonly string Ws = Workspace.ToString("D");
    private static readonly SearchTranslationContext Context = new(Workspace, 2, QueryLimits.Default);
    private static readonly SourceSpan Span = new(0, 1);

    private static readonly string[] Words =
    [
        "contract", "apple", "iphone", "OR", "NOT", "and", "or", "*", "?", Other.ToString("D"), "workspaceId", "workspaceId:" + Other,
        "\"}}]},{\"term\":{\"workspaceId\":\"" + Other + "\"}}", "securityTags:class:Privileged", "match_all", "{", "}", "\\",
        "documentId:" + Other, "ünïcödé", "a\"b", "'", "W/3",
    ];

    private static readonly string[] FieldNames =
    [
        "workspaceId", "WORKSPACEID", "WorkspaceId", "securityTags", "documentId", "projectionVersion", "text", "fileName",
        "controlNumber", "fileType", "documentDate", "fileSize", "textTruncated", "custodian", "metadata.kw.s001", "_id", "_routing",
    ];

    [Fact]
    public async Task Ten_thousand_random_asts_all_keep_the_workspace_term_in_the_top_level_filter()
    {
        var translator = PlannerFixture.Planner();
        var random = new Random(20261003);
        var translated = 0;
        var refused = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var ast = Node(random, 0);
            var translation = await translator.TranslateAsync(ast, Context, TestContext.Current.CancellationToken);
            if (!translation.Success)
            {
                translation.Errors.Should().NotBeEmpty("a query that is not planned says why ({0})", ast);
                refused++;
                continue;
            }

            Fields(ast).Should().NotContain(f => ProjectionFields.NotAddressable.Contains(f),
                "workspaceId, securityTags, documentId and projectionVersion are never addressable ({0})", ast);
            var visibility = RandomVisibility(random);
            var query = SearchDsl.Query(Ws, visibility, translation.Query!);
            AssertIsolated(query, translation.Query!, visibility);
            AssertIsolated((JsonObject)SearchDsl.Body(RandomBody(random, query))["query"]!, translation.Query!, visibility);
            translated++;
        }

        translated.Should().BeGreaterThan(2_000, "the generator must exercise accepted queries, not only errors");
        refused.Should().BeGreaterThan(500, "and injection attempts must be refused");
    }

    [Fact]
    public async Task Random_query_text_with_workspace_injection_never_reaches_the_filter()
    {
        var translator = PlannerFixture.Planner();
        var random = new Random(4711);
        for (var i = 0; i < 2_000; i++)
        {
            var text = string.Join(' ', Enumerable.Range(0, random.Next(1, 8)).Select(_ => Words[random.Next(Words.Length)]));
            var parsed = QueryParser.Parse(text);
            if (parsed.Ast is null)
            {
                continue;
            }

            var translation = await translator.TranslateAsync(parsed.Ast, Context, TestContext.Current.CancellationToken);
            if (translation.Success)
            {
                var query = SearchDsl.Query(Ws, new VisibilityFilter([], [], false), translation.Query!);
                AssertIsolated(query, translation.Query!, new VisibilityFilter([], [], false));
            }
            else if (Fields(parsed.Ast).Any(ProjectionFields.NotAddressable.Contains))
            {
                translation.Errors.Should().Contain(e => e.Code == SearchQueryErrorCodes.UnknownField, text);
            }
        }
    }

    [Fact]
    public void Even_a_hostile_user_clause_stays_inside_must()
    {
        var hostile = (JsonObject)JsonNode.Parse(
            $$$"""
            {"bool":{"should":[{"term":{"workspaceId":"{{{Other}}}"}},{"match_all":{}}],"filter":[{"term":{"workspaceId":"{{{Other}}}"}}],
             "minimum_should_match":0}}
            """)!;

        var query = SearchDsl.Query(Ws, new VisibilityFilter(["Privileged"], [Other], false), hostile);

        AssertIsolated(query, hostile, new VisibilityFilter(["Privileged"], [Other], false));
        query["bool"]!["filter"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public void Denied_classes_and_walls_are_excluded_in_the_outer_filter()
    {
        var wall = Guid.Parse("0199a8a0-3333-7000-8000-000000000003");
        var query = SearchDsl.Query(Ws, new VisibilityFilter(["Privileged", "Aeo"], [wall], false), new JsonObject { ["match_all"] = new JsonObject() });

        var exclusion = query["bool"]!["filter"]![1]!["bool"]!["must_not"]![0]!["terms"]!["securityTags"]!.AsArray()
            .Select(n => n!.GetValue<string>());
        exclusion.Should().Equal("class:Aeo", "class:Privileged", "wall:" + wall);
        SearchDsl.Query(Ws, new VisibilityFilter([], [], true), new JsonObject { ["match_all"] = new JsonObject() })["bool"]!["filter"]!
            .AsArray().Should().ContainSingle("break-glass lifts classes and walls, never the workspace");
    }

    [Fact]
    public void Reversed_sort_is_the_exact_mirror_including_missing_values_and_the_tie_breaker()
    {
        IReadOnlyList<SortKey> keys = [SortKey.Resolve("documentDate", SearchSortDirection.Desc)!, SortKey.Resolve("relevance", SearchSortDirection.Desc)!];

        SearchDsl.Sort(keys, reverse: false).ToJsonString().Should().Be(
            """[{"documentDate":{"order":"desc","missing":"_last"}},{"_score":{"order":"desc"}},{"documentId":{"order":"asc","missing":"_last"}}]""");
        SearchDsl.Sort(keys, reverse: true).ToJsonString().Should().Be(
            """[{"documentDate":{"order":"asc","missing":"_first"}},{"_score":{"order":"asc"}},{"documentId":{"order":"desc","missing":"_first"}}]""");
        SortKey.Resolve("controlnumber", SearchSortDirection.Asc)!.Path.Should().Be("controlNumberSort");
        SortKey.Resolve("workspaceId", SearchSortDirection.Asc).Should().BeNull();
        SortKey.FromJson(SortKey.ToJson(keys)).Should().Equal(keys);
    }

    [Fact]
    public void Bodies_return_grid_fields_only_and_bounded_snippets()
    {
        var body = SearchDsl.Body(new SearchBodySpec
        {
            Query = SearchDsl.Query(Ws, new VisibilityFilter([], [], false), new JsonObject { ["match_all"] = new JsonObject() }),
            PointInTimeId = "pit",
            KeepAlive = TimeSpan.FromMinutes(5),
            Sort = [SortKey.Default],
            Size = 51,
            TrackTotalHitsUpTo = 10_000,
            Highlight = true,
        });

        body["_source"]!["includes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().NotContain("text");
        body["highlight"]!["fields"]!["text"]!["number_of_fragments"]!.GetValue<int>().Should().Be(3);
        body["track_total_hits"]!.GetValue<int>().Should().Be(10_000);
        body["pit"]!["keep_alive"]!.GetValue<string>().Should().Be("300s");
        body.ContainsKey("post_filter").Should().BeFalse();
    }

    [Fact]
    public void Snippets_are_plain_text_with_highlight_offsets()
    {
        var snippet = SearchService.Snippet("the apple and iPhone <b>launch</b>");

        snippet.Text.Should().Be("the apple and iPhone <b>launch</b>");
        snippet.Highlights.Select(h => snippet.Text[h.Start..h.End]).Should().Equal("apple", "iPhone");
        SearchService.Snippet("broken  tail").Highlights.Should().BeEmpty();
    }

    private static void AssertIsolated(JsonObject query, JsonObject userClause, VisibilityFilter visibility)
    {
        query.Select(p => p.Key).Should().Equal("bool");
        var outer = query["bool"]!.AsObject();
        outer.Select(p => p.Key).Should().BeEquivalentTo(["filter", "must"], "nothing but filter and must at the top level: no should, no must_not");
        var filter = outer["filter"]!.AsArray();
        filter[0]!.ToJsonString().Should().Be("""{"term":{"workspaceId":{"value":""" + "\"" + Ws + "\"}}}");
        var hasExclusions = visibility.DeniedClasses.Count + visibility.WallIds.Count > 0;
        filter.Should().HaveCount(hasExclusions ? 2 : 1, "only server state is in the filter");
        if (hasExclusions)
        {
            filter[1]!["bool"]!.AsObject().Select(p => p.Key).Should().Equal("must_not");
        }

        var must = outer["must"]!.AsArray();
        must.Should().ContainSingle();
        JsonNode.DeepEquals(must[0], userClause).Should().BeTrue();
    }

    private static VisibilityFilter RandomVisibility(Random random) => random.Next(3) switch
    {
        0 => new VisibilityFilter([], [], false),
        1 => new VisibilityFilter(["Privileged"], [], false),
        _ => new VisibilityFilter(["Privileged", "Confidential"], [Other], false),
    };

    private static SearchBodySpec RandomBody(Random random, JsonObject query) => new()
    {
        Query = query,
        PointInTimeId = "pit-" + random.Next(),
        KeepAlive = TimeSpan.FromMinutes(5),
        Sort = [SortKey.Default],
        Reverse = random.Next(2) == 0,
        SearchAfter = random.Next(2) == 0 ? new JsonArray(1.5, Other.ToString("D")) : null,
        From = random.Next(2) == 0 ? 50 : 0,
        Size = 51,
        TrackTotalHitsUpTo = random.Next(2) == 0 ? null : 10_000,
        Highlight = random.Next(2) == 0,
        Facets = random.Next(2) == 0 ? ["fileType"] : [],
    };

    private static IEnumerable<string> Fields(QueryNode node) => node switch
    {
        FieldNode f => [f.Name, .. Fields(f.Child)],
        AndNode a => a.Children.SelectMany(Fields),
        OrNode o => o.Children.SelectMany(Fields),
        NotNode n => Fields(n.Child),
        ProximityNode p => Fields(p.Left).Concat(Fields(p.Right)),
        _ => [],
    };

    private static QueryNode Node(Random random, int depth)
    {
        var choice = random.Next(depth >= 4 ? 5 : 11);
        return choice switch
        {
            0 => new TermNode(Words[random.Next(Words.Length)], Span),
            1 => new PhraseNode(Words[random.Next(Words.Length)] + " " + Words[random.Next(Words.Length)], Span),
            2 => new WildcardNode(random.Next(2) == 0 ? "con*" : "*tract", Span),
            3 => new MatchAllNode(Span),
            4 => new FieldNode(FieldNames[random.Next(FieldNames.Length)], Leaf(random), Span),
            5 => new AndNode([.. Enumerable.Range(0, random.Next(2, 4)).Select(_ => Node(random, depth + 1))], Span),
            6 => new OrNode([.. Enumerable.Range(0, random.Next(2, 4)).Select(_ => Node(random, depth + 1))], Span),
            7 => new NotNode(Node(random, depth + 1), Span),
            8 => new ProximityNode(new TermNode("apple", Span), random.Next(2) == 0 ? new PhraseNode("iphone pro", Span)
                : new OrNode([new TermNode("iphone", Span), new WildcardNode("ipa*", Span)], Span), random.Next(1, 20), Span),
            9 => new FieldNode(FieldNames[random.Next(FieldNames.Length)],
                new OrNode([Leaf(random), new NotNode(Leaf(random), Span)], Span), Span),
            _ => new OrNode([new FieldNode("workspaceId", new TermNode(Other.ToString("D"), Span), Span), Node(random, depth + 1)], Span),
        };
    }

    private static QueryNode Leaf(Random random) => random.Next(6) switch
    {
        0 => new TermNode(Words[random.Next(Words.Length)], Span),
        1 => new PhraseNode("a b", Span),
        2 => new WildcardNode("abc*", Span),
        3 => new RangeNode(new RangeBound("2020-01-01", true), new RangeBound("2021-01-01", false), Span),
        4 => new ExistsNode(Span),
        _ => new TermNode(Other.ToString("D"), Span),
    };
}
