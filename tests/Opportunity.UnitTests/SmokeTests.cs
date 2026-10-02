using AwesomeAssertions;

namespace Opportunity.UnitTests;

public class SmokeTests
{
    [Fact]
    public void Core_assembly_loads()
    {
        typeof(Core.AssemblyMarker).Assembly.GetName().Name.Should().Be("Opportunity.Core");
    }
}