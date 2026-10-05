using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Application.Search;
using Opportunity.Contracts.Search;
using Opportunity.Core.Documents;
using Opportunity.Search.Querying;

using static Opportunity.Search.Indexing.JsonBodies;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// E09-T03: one expansion definition (<see cref="RelationshipExpansion"/>) and its OpenSearch form: the steps and their
/// first-reason-wins labels, the named clauses of an expanded query, the count aggregations, chunked key lists, and the
/// "Date (Family)" sort that keeps families contiguous.
/// </summary>
public sealed class RelationshipExpansionTests
{
    private static readonly JsonObject User = Obj(("match", Obj(("text", "merger"))));

    [Theory]
    [InlineData(false, false, false, 0, "none")]
    [InlineData(true, false, false, 1, "family")]
    [InlineData(false, true, false, 2, "duplicates")]
    [InlineData(false, false, true, 4, "thread")]
    [InlineData(true, true, true, 7, "family+duplicates+thread")]
    public void Flags_round_trip_and_describe_the_expansion(bool family, bool duplicates, bool thread, short flags, string text)
    {
        var expansion = new RelationshipExpansion(family, duplicates, thread);
        expansion.Flags.Should().Be(flags);
        RelationshipExpansion.FromFlags(flags).Should().Be(expansion);
        expansion.ToString().Should().Be(text);
        expansion.IsNone.Should().Be(flags == 0);
    }

    [Fact]
    public void Steps_take_duplicates_and_threads_from_the_base_and_family_last_from_everything_added()
    {
        new RelationshipExpansion(true, true, true).Steps.Should().Equal(
            new RelationshipExpansionStep(RelationshipKind.Family, ExpansionSeed.Base),
            new RelationshipExpansionStep(RelationshipKind.Duplicate, ExpansionSeed.Base),
            new RelationshipExpansionStep(RelationshipKind.Thread, ExpansionSeed.Base),
            new RelationshipExpansionStep(RelationshipKind.Family, ExpansionSeed.DuplicateAndThreadAdditions));
        new RelationshipExpansion(true, false, false).Steps.Should().ContainSingle();
        new RelationshipExpansion(false, true, true).Steps.Select(s => s.Kind).Should().Equal(RelationshipKind.Duplicate, RelationshipKind.Thread);
        RelationshipExpansion.None.Steps.Should().BeEmpty();
    }

    [Fact]
    public void The_api_shape_maps_to_the_definition_and_back()
    {
        ((SearchExpand?)null).ToExpansion().Should().Be(RelationshipExpansion.None);
        new SearchExpand(Family: true).ToExpansion().Should().Be(new RelationshipExpansion(true, false, false));
        new SearchExpand(false, null, true).ToExpansion().Should().Be(new RelationshipExpansion(false, false, true));
        RelationshipExpansion.None.ToContract().Should().BeNull();
        new RelationshipExpansion(true, true, false).ToContract().Should().Be(new SearchExpand(true, true, false));
    }

    [Fact]
    public void The_expanded_clause_names_every_part_and_keeps_the_user_query_intact()
    {
        var keys = new RelationshipExpansionQuery.Keys(["f1", "f2"], ["d1"], [], ["f3"]);
        var combined = RelationshipExpansionQuery.Combined(User, keys);

        var should = combined["bool"]!["should"]!.AsArray();
        combined["bool"]!["minimum_should_match"]!.GetValue<int>().Should().Be(1);
        should.Should().HaveCount(4, "an empty key set (thread) adds no clause");
        should[0]!["bool"]!["_name"]!.GetValue<string>().Should().Be(RelationshipExpansionQuery.BaseName);
        JsonNode.DeepEquals(should[0]!["bool"]!["must"]![0], User).Should().BeTrue();
        should.Skip(1).Select(c => c!["constant_score"]!["_name"]!.GetValue<string>())
            .Should().Equal(RelationshipExpansionQuery.FamilyName, RelationshipExpansionQuery.DuplicateName, RelationshipExpansionQuery.FamilyOfExpandedName);
        should.Skip(1).Should().OnlyContain(c => c!["constant_score"]!["boost"]!.GetValue<int>() == 0, "expanded rows never outrank hits");
        should[1]!["constant_score"]!["filter"]!["terms"]!["familyId"]!.AsArray().Select(k => k!.GetValue<string>()).Should().Equal("f1", "f2");
        should[2]!["constant_score"]!["filter"]!["terms"]!["duplicateGroupId"].Should().NotBeNull();
    }

    [Fact]
    public void Count_aggregations_exclude_hits_and_earlier_reasons()
    {
        var keys = new RelationshipExpansionQuery.Keys(["f1"], ["d1"], ["t1"], ["f2"]);
        var aggs = RelationshipExpansionQuery.CountAggregations(User, keys);

        aggs.Select(a => a.Key).Should().BeEquivalentTo(
            RelationshipExpansionQuery.BaseCount, RelationshipExpansionQuery.FamilyCount, RelationshipExpansionQuery.DuplicateCount,
            RelationshipExpansionQuery.ThreadCount, RelationshipExpansionQuery.FamilyOfExpandedCount);
        MustNot(aggs, RelationshipExpansionQuery.FamilyCount).Should().Be(1, "family excludes only the hits");
        MustNot(aggs, RelationshipExpansionQuery.DuplicateCount).Should().Be(2, "duplicates exclude hits and family");
        MustNot(aggs, RelationshipExpansionQuery.ThreadCount).Should().Be(3);
        MustNot(aggs, RelationshipExpansionQuery.FamilyOfExpandedCount).Should().Be(4);

        var response = Obj(
            (RelationshipExpansionQuery.BaseCount, Obj(("doc_count", 10))),
            (RelationshipExpansionQuery.FamilyCount, Obj(("doc_count", 20))),
            (RelationshipExpansionQuery.DuplicateCount, Obj(("doc_count", 3))),
            (RelationshipExpansionQuery.ThreadCount, Obj(("doc_count", 4))),
            (RelationshipExpansionQuery.FamilyOfExpandedCount, Obj(("doc_count", 5))));
        RelationshipExpansionQuery.ReadCounts(response).Should().Be((10L, new SearchExpandedCounts(25, 3, 4, 42)));

        static int MustNot(JsonObject aggs, string name) => aggs[name]!["filter"]!["bool"]!["must_not"]!.AsArray().Count;
    }

    [Fact]
    public void A_row_says_why_it_is_there_with_family_winning()
    {
        RelationshipExpansionQuery.ExpandedBy(new JsonArray("x.base", "x.family")).Should().BeNull();
        RelationshipExpansionQuery.ExpandedBy(new JsonArray("x.duplicate", "x.family")).Should().Be(SearchExpandedBy.Family);
        RelationshipExpansionQuery.ExpandedBy(new JsonArray("x.thread", "x.duplicate")).Should().Be(SearchExpandedBy.Duplicate);
        RelationshipExpansionQuery.ExpandedBy(new JsonArray("x.thread")).Should().Be(SearchExpandedBy.Thread);
        RelationshipExpansionQuery.ExpandedBy(new JsonArray("x.thread", "x.familyOfExpanded")).Should().Be(SearchExpandedBy.Thread);
        RelationshipExpansionQuery.ExpandedBy(new JsonArray("x.familyOfExpanded")).Should().Be(SearchExpandedBy.Family);
        RelationshipExpansionQuery.ExpandedBy(Obj(("x.family", 0.0))).Should().Be(SearchExpandedBy.Family, "matched_queries with scores");
        RelationshipExpansionQuery.ExpandedBy(null).Should().BeNull();
    }

    [Fact]
    public void Large_key_sets_are_split_below_the_terms_limit()
    {
        var keys = Enumerable.Range(0, RelationshipExpansionQuery.MaxTermsPerClause * 2 + 5).Select(i => i.ToString("D8")).ToList();
        var filter = RelationshipExpansionQuery.Terms("familyId", keys);

        var chunks = filter["bool"]!["should"]!.AsArray();
        chunks.Should().HaveCount(3);
        chunks.Sum(c => c!["terms"]!["familyId"]!.AsArray().Count).Should().Be(keys.Count);
        chunks.Should().OnlyContain(c => c!["terms"]!["familyId"]!.AsArray().Count <= RelationshipExpansionQuery.MaxTermsPerClause);
    }

    [Fact]
    public void Family_date_sort_keeps_families_contiguous_and_reverses_exactly()
    {
        var key = SortKey.Resolve("FAMILYDATE", SearchSortDirection.Asc)!;
        Paths(SearchDsl.Sort([key], reverse: false)).Should().Equal(
            "familyDate:asc", "familyId:asc", "familySequence:asc", "documentId:asc");
        Paths(SearchDsl.Sort([key], reverse: true)).Should().Equal(
            "familyDate:desc", "familyId:desc", "familySequence:desc", "documentId:desc");
        Paths(SearchDsl.Sort([SortKey.Resolve("familyDate", SearchSortDirection.Desc)!], reverse: false)).Should().Equal(
            "familyDate:desc", "familyId:desc", "familySequence:asc", "documentId:asc");
        SortKey.FromJson(SortKey.ToJson([key])).Should().Equal(key);

        static IEnumerable<string> Paths(JsonArray sort) => sort.Select(s =>
        {
            var (path, order) = s!.AsObject().Single();
            return path + ":" + order!["order"]!.GetValue<string>();
        });
    }
}
