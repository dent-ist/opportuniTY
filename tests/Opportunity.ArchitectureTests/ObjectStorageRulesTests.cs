using System.Reflection;

using AwesomeAssertions;

using NetArchTest.Rules;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// ADR-011 §5 / ADR-015 D12.1: signed URLs are obtainable only through the protected-content gateway in the API.
/// The signing port is separate from <c>IObjectStore</c> so this can be enforced by dependency.
/// </summary>
public class ObjectStorageRulesTests
{
    private const string Signer = "Opportunity.Application.Storage.IObjectUrlSigner";

    public static TheoryData<string> NonGatewayAssemblies => new()
    {
        "Opportunity.Core",
        "Opportunity.Contracts",
        "Opportunity.Data",
        "Opportunity.Search",
        "Opportunity.Messaging",
        "Opportunity.Security",
        "Opportunity.Jobs",
        "Opportunity.Import",
        "Opportunity.Rendering",
        "Opportunity.Production",
    };

    [Theory]
    [MemberData(nameof(NonGatewayAssemblies))]
    public void Only_the_gateway_and_storage_depend_on_the_url_signer(string assemblyName)
    {
        AssertNoSignerDependency(Types.InAssembly(Assembly.Load(assemblyName)), assemblyName);
    }

    [Fact]
    public void Application_use_cases_do_not_depend_on_the_url_signer()
    {
        AssertNoSignerDependency(
            Types.InAssembly(typeof(Application.AssemblyMarker).Assembly).That().DoNotHaveName("IObjectUrlSigner"),
            "Opportunity.Application");
    }

    private static void AssertNoSignerDependency(PredicateList types, string assemblyName) =>
        Check(types.ShouldNot().HaveDependencyOn(Signer).GetResult(), assemblyName);

    private static void AssertNoSignerDependency(Types types, string assemblyName) =>
        Check(types.ShouldNot().HaveDependencyOn(Signer).GetResult(), assemblyName);

    private static void Check(NetArchTest.Rules.TestResult result, string assemblyName) =>
        result.IsSuccessful.Should().BeTrue(
            "{0} must obtain content through the gateway, not sign URLs itself; offending types: {1}",
            assemblyName,
            string.Join(", ", result.FailingTypeNames ?? []));
}
