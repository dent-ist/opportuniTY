using System.Reflection;

using AwesomeAssertions;

using NetArchTest.Rules;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// E07-T01 / ADR-006 R1, ADR-019 R6: application code never references physical index names. Names, placements and
/// the OpenSearch REST client live in <c>Opportunity.Search.Indexing</c>; every other project addresses a workspace
/// through <c>IWorkspaceSearchPlacement</c> (Application) and never sees an index, alias or routing value.
/// </summary>
public class SearchIndexIsolationTests
{
    private const string IndexingNamespace = "Opportunity.Search.Indexing";
    private const string QueryingNamespace = "Opportunity.Search.Querying";

    public static TheoryData<string> NonSearchAssemblies => new()
    {
        "Opportunity.Core",
        "Opportunity.Contracts",
        "Opportunity.Application",
        "Opportunity.Data",
        "Opportunity.Storage",
        "Opportunity.Messaging",
        "Opportunity.Security",
        "Opportunity.Jobs",
        "Opportunity.Import",
        "Opportunity.Rendering",
        "Opportunity.Production",
        "Opportunity.Hosting",
        "Opportunity.Api",
        "Opportunity.Migrator",
    };

    [Theory]
    [MemberData(nameof(NonSearchAssemblies))]
    public void Only_the_search_module_uses_index_management_internals(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(assemblyName)).ShouldNot().HaveDependencyOn(IndexingNamespace).GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0} must address workspaces, not indexes (ADR-006 R1); offending: {1}",
            assemblyName,
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// E07-T05: the OpenSearch client, the query DSL and the search service implementation are referenced only from
    /// <c>Opportunity.Search</c>; everything else searches through <c>ISearchService</c> (Application).
    /// </summary>
    [Theory]
    [MemberData(nameof(NonSearchAssemblies))]
    public void Only_the_search_module_builds_or_sends_search_requests(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(QueryingNamespace, "OpenSearch.Client", "OpenSearch.Net", "Elastic.Clients", "Elasticsearch.Net")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0} must search through ISearchService (ADR-006 R7, ADR-015 D8.1); offending: {1}",
            assemblyName,
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_search_service_its_dsl_and_the_rest_client_are_not_public()
    {
        var search = typeof(Search.AssemblyMarker).Assembly;
        string[] internals = ["SearchService", "SearchDsl", "SearchBodySpec", "SortKey", "OpenSearchConnection", "OpenSearchResponse", "JsonBodies"];

        search.GetExportedTypes().Select(t => t.Name).Should().NotContain(internals);
        search.GetTypes().Where(t => t.Name == "SearchService").Should().ContainSingle()
            .Which.GetInterfaces().Should().Contain(typeof(Application.Search.ISearchService));
    }

    [Fact]
    public void Index_naming_and_the_rest_client_are_not_public()
    {
        var search = typeof(Search.AssemblyMarker).Assembly;
        string[] internals = ["IndexNames", "OpenSearchConnection", "IndexTemplates", "IndexManager"];

        search.GetExportedTypes().Select(t => t.Name).Should().NotContain(internals);
    }

    [Fact]
    public void Application_placement_records_carry_no_names()
    {
        var properties = typeof(Application.Search.Indexing.WorkspaceIndexPlacement).GetProperties()
            .Concat(typeof(Application.Search.Indexing.WorkspaceSearchPlacementInfo).GetProperties())
            .Select(p => p.Name);

        properties.Should().NotContain(n => n.Contains("Index", StringComparison.Ordinal) || n.Contains("Alias", StringComparison.Ordinal)
            || n.Contains("Routing", StringComparison.Ordinal));
    }
}
