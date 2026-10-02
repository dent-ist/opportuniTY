using System.Reflection;

using AwesomeAssertions;

using NetArchTest.Rules;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// Enforces the project layering in docs/adr/0019-layering-and-api-conventions.md (rules R1–R3, plus the host-composition layer).
/// </summary>
public class LayeringTests
{
    private static readonly string[] InfrastructureSdks =
    [
        "Npgsql",
        "OpenSearch",
        "RabbitMQ.Client",
        "Amazon",
        "Azure",
        "Minio",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
    ];

    private static readonly string[] Infrastructure =
    [
        "Opportunity.Data",
        "Opportunity.Search",
        "Opportunity.Storage",
        "Opportunity.Messaging",
        "Opportunity.Security",
        "Opportunity.Jobs",
    ];

    private static readonly string[] FeatureModules =
    [
        "Opportunity.Import",
        "Opportunity.Rendering",
        "Opportunity.Production",
    ];

    private static readonly string[] HostComposition = ["Opportunity.Hosting"];

    private static readonly string[] Hosts = ["Opportunity.Api", "Opportunity.Worker", "Opportunity.Migrator"];

    private static readonly string[] OpportunityInfrastructure = [.. Infrastructure, .. FeatureModules, .. HostComposition, .. Hosts];

    public static TheoryData<string> PureAssemblies => new() { "Opportunity.Core", "Opportunity.Contracts" };

    [Theory]
    [MemberData(nameof(PureAssemblies))]
    public void Pure_assemblies_do_not_reference_infrastructure_sdks(string assemblyName)
    {
        AssertNoDependencyOn(Load(assemblyName), InfrastructureSdks);
    }

    [Fact]
    public void Core_does_not_reference_other_opportunity_projects()
    {
        AssertNoDependencyOn(
            typeof(Core.AssemblyMarker).Assembly,
            [.. OpportunityInfrastructure, "Opportunity.Application", "Opportunity.Contracts"]);
    }

    [Fact]
    public void Contracts_does_not_reference_other_opportunity_projects()
    {
        AssertNoDependencyOn(
            typeof(Contracts.AssemblyMarker).Assembly,
            [.. OpportunityInfrastructure, "Opportunity.Application", "Opportunity.Core"]);
    }

    [Fact]
    public void Application_does_not_reference_transport_store_or_infrastructure_projects()
    {
        AssertNoDependencyOn(
            typeof(Application.AssemblyMarker).Assembly,
            [.. InfrastructureSdks, .. OpportunityInfrastructure]);
    }

    public static TheoryData<string> FeatureModuleNames => new(FeatureModules);

    [Theory]
    [MemberData(nameof(FeatureModuleNames))]
    public void Feature_modules_do_not_reference_infrastructure_vendor_sdks_or_hosts(string assemblyName)
    {
        AssertNoDependencyOn(Load(assemblyName), [.. InfrastructureSdks, .. Infrastructure, .. HostComposition, .. Hosts]);
    }

    public static TheoryData<string> InfrastructureNames => new(Infrastructure);

    [Theory]
    [MemberData(nameof(InfrastructureNames))]
    public void Infrastructure_projects_do_not_reference_each_other_feature_modules_or_hosts(string assemblyName)
    {
        AssertNoDependencyOn(
            Load(assemblyName),
            [.. Infrastructure.Where(n => n != assemblyName), .. FeatureModules, .. HostComposition, .. Hosts]);
    }

    [Fact]
    public void Host_composition_does_not_reference_hosts()
    {
        AssertNoDependencyOn(Load("Opportunity.Hosting"), Hosts);
    }

    private static Assembly Load(string name) => Assembly.Load(name);

    private static void AssertNoDependencyOn(Assembly assembly, string[] forbidden)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0} must not depend on [{1}]; offending types: {2}",
            assembly.GetName().Name,
            string.Join(", ", forbidden),
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}