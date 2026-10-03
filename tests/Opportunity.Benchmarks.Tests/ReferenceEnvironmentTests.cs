using AwesomeAssertions;

using Opportunity.Benchmarks.Capture;

namespace Opportunity.Benchmarks.Tests;

/// <summary>The Compose definitions of both profiles keep the settings docs/benchmarks/reference-environments.md promises.</summary>
public class ReferenceEnvironmentTests
{
    private static readonly string Benchmarks = Path.Combine(Path.GetDirectoryName(VersionsEnvFile.Locate()!)!, "deploy", "benchmarks");

    private static string Read(string file) => File.ReadAllText(Path.Combine(Benchmarks, file));

    [Theory]
    [InlineData("compose.bench.yaml")]
    [InlineData("compose.reference.yaml")]
    public void Benchmark_profiles_pin_production_durability(string file)
    {
        string compose = Read(file);

        compose.Should().Contain("- fsync=on").And.Contain("- synchronous_commit=on").And.Contain("- full_page_writes=on");
        compose.Should().NotContain("fsync=off").And.NotContain("synchronous_commit=off");
    }

    [Fact]
    public void Relaxed_durability_exists_only_as_the_nightly_developer_overlay()
    {
        Read("compose.bench-relaxed.yaml").Should().Contain("- fsync=off");
        Read("bench.sh").Should().Contain("--relaxed is not allowed on the reference profile");
    }

    [Fact]
    public void Reference_topology_matches_section_29()
    {
        string compose = Read("compose.reference.yaml");

        foreach (string service in new[] { "os1:", "os2:", "os3:", "pg-primary:", "pg-replica:", "rabbitmq:", "seaweedfs:", "api:", "worker-indexing:", "worker-bulk-coding:" })
        {
            compose.Should().Contain("\n  " + service, $"the reference topology needs {service.TrimEnd(':')}");
        }

        compose.Should().Contain("cluster.initial_cluster_manager_nodes: os1,os2,os3");
        compose.Should().Contain("${OPENSEARCH_DIGEST:?").And.Contain("${POSTGRES_DIGEST:?").And.Contain("${RABBITMQ_DIGEST:?").And.Contain("${SEAWEEDFS_DIGEST:?");
    }
}
