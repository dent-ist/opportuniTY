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
