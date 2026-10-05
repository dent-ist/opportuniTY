using System.Text.Json.Nodes;

using AwesomeAssertions;

using Opportunity.Contracts.Search;
using Opportunity.Core.Fields;
using Opportunity.Search.Querying;

namespace Opportunity.UnitTests.Search;

/// <summary>
/// E16-T09: the grid's columns over the projection. Workspace fields sort by query name when their capabilities say
/// sortable (text on its keyword companion, choices and users never), every sort ends with Control Number and the
/// document ID so equal values page deterministically, and hits carry the values of the fields a search asked for.
/// </summary>
public sealed class SearchColumnsTests
{
    private static readonly SearchFieldResolver Resolver = SearchFieldResolver.Create(PlannerFixture.Catalog, 2);
    private static readonly IReadOnlyDictionary<int, string> Names = FieldQueryNames.Assign(PlannerFixture.Catalog.Fields);

    [Fact]
    public void Sortable_workspace_fields_resolve_to_sortable_paths_and_choice_user_and_unknown_fields_do_not()
    {
        SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Custodian], descending: false)!.Path.Should().Be("metadata.kw.s001");
        SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Notes], descending: true)!.Path.Should().Be("metadata.txt.s001.kw");
        SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Amount], descending: false)!.Path.Should().Be("metadata.dec.s001");
        SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Hot], descending: false)!.Path.Should().Be("coding.bool.s001");
        SearchColumns.SortKeyFor(Resolver, "begbates", descending: false)!.Path.Should().Be("begBatesSort");
        SearchColumns.SortKeyFor(Resolver, "datesent", descending: false)!.Path.Should().Be("dateSent");

        SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Responsiveness], descending: false).Should().BeNull("choices are not sortable (ADR-007 R8)");
        SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Reviewer], descending: false).Should().BeNull("users are not sortable");
        SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Unsearchable], descending: false).Should().BeNull();
        SearchColumns.SortKeyFor(Resolver, "nosuchfield", descending: false).Should().BeNull();
        SearchColumns.SortKeyFor(Resolver, "text", descending: false).Should().BeNull();
        SearchColumns.SortKeyFor(Resolver, "workspaceId", descending: false).Should().BeNull();
    }

    [Fact]
    public void Every_sort_ends_with_control_number_then_document_id_and_a_control_number_sort_is_not_repeated()
    {
        var custodian = SearchColumns.SortKeyFor(Resolver, Names[PlannerFixture.Custodian], descending: true)!;
        SearchDsl.Sort([custodian, SortKey.Resolve("documentDate", SearchSortDirection.Asc)!], reverse: false).ToJsonString().Should().Be(
            """[{"metadata.kw.s001":{"order":"desc","missing":"_last"}},{"documentDate":{"order":"asc","missing":"_last"}},""" +
            """{"controlNumberSort":{"order":"asc","missing":"_last"}},{"documentId":{"order":"asc","missing":"_last"}}]""");

        SearchDsl.Sort([SortKey.Resolve("controlNumber", SearchSortDirection.Desc)!], reverse: false).ToJsonString().Should().Be(
            """[{"controlNumberSort":{"order":"desc","missing":"_last"}},{"documentId":{"order":"asc","missing":"_last"}}]""");

        // A sort stored with its resolved path pages exactly like the first page, even for workspace fields.
        IReadOnlyList<SortKey> keys = [custodian, SortKey.Default];
        SortKey.FromJson(SortKey.ToJson(keys)).Should().Equal(keys);
        SortKey.FromJson("""[{"field":"fileName","direction":"asc"}]""").Single().Path.Should().Be("fileName.kw", "older sessions stored no path");
    }

    [Fact]
    public void Hits_carry_the_requested_values_as_strings_bounded_and_leave_out_fields_without_values()
    {
        List<ResultField> fields =
        [
            SearchColumns.FieldFor(Resolver, Names[PlannerFixture.Custodian])!,
            SearchColumns.FieldFor(Resolver, Names[PlannerFixture.Issues])!,
            SearchColumns.FieldFor(Resolver, Names[PlannerFixture.Hot])!,
            SearchColumns.FieldFor(Resolver, Names[PlannerFixture.Amount])!,
            SearchColumns.FieldFor(Resolver, Names[PlannerFixture.OverflowTag])!,
            SearchColumns.FieldFor(Resolver, "datesent")!,
            SearchColumns.FieldFor(Resolver, Names[PlannerFixture.Notes])!,
        ];
        SearchColumns.FieldFor(Resolver, "nosuchfield").Should().BeNull();
        SearchColumns.FieldFor(Resolver, Names[PlannerFixture.Unsearchable]).Should().BeNull();
        SearchColumns.FieldFor(Resolver, "text").Should().BeNull("the extracted text is never a grid value (ADR-015 D8.5)");

        var source = JsonNode.Parse(
            $$"""
            {
              "controlNumber": "ABC0001",
              "dateSent": "2024-03-01T10:00:00Z",
              "metadata": { "kw": { "s001": "Smith, Jane" }, "dec": { "s001": 12.5 }, "txt": { "s001": "{{new string('x', 5000)}}" } },
              "metadataOverflow": { "{{PlannerFixture.Catalog.Find(PlannerFixture.OverflowTag)!.Key}}": "tag-1" },
              "coding": { "ch": { "s002": ["1", "2"] }, "bool": { "s001": true } }
            }
            """)!.AsObject();

        var values = SearchColumns.Values(source, fields)!;

        values[Names[PlannerFixture.Custodian]].Should().Equal("Smith, Jane");
        values[Names[PlannerFixture.Issues]].Should().Equal("1", "2");
        values[Names[PlannerFixture.Hot]].Should().Equal("true");
        values[Names[PlannerFixture.Amount]].Should().Equal("12.5");
        values[Names[PlannerFixture.OverflowTag]].Should().Equal("tag-1");
        values["datesent"].Should().Equal("2024-03-01T10:00:00Z");
        values[Names[PlannerFixture.Notes]].Single().Length.Should().Be(SearchResultFields.MaxValueLength);
        SearchColumns.Values(JsonNode.Parse("""{"controlNumber":"X"}""")!.AsObject(), fields).Should().BeEmpty();
        SearchColumns.Values(source, []).Should().BeNull();
        SearchColumns.FromJson(SearchColumns.ToJson(fields)).Should().Equal(fields);
    }
}
