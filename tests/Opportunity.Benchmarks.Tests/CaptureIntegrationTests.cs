using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Infrastructure;
using Opportunity.Testing.Images;

using Testcontainers.PostgreSql;

namespace Opportunity.Benchmarks.Tests;

/// <summary>
/// PostgreSQL and OpenSearch at the versions.env digests, configured like a relaxed nightly developer run
/// (fsync=off, an index with async translog), so capture has real settings and real deviations to find.
/// </summary>
public sealed class LiveServicesFixture : IAsyncLifetime
{
    public const string IndexName = "bench-docs";

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder(ContainerImages.Postgres)
        .WithDatabase("opportunity")
        .WithUsername("bench")
        .WithPassword("bench-secret")
        .WithCommand("-c", "fsync=off", "-c", "shared_buffers=64MB")
        .Build();

    public IContainer OpenSearch { get; } = new ContainerBuilder(ContainerImages.OpenSearch)
        .WithPortBinding(9200, assignRandomHostPort: true)
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithEnvironment("cluster.routing.allocation.disk.threshold_enabled", "false")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health")))
        .Build();

    public Uri OpenSearchUri => new($"http://{OpenSearch.Hostname}:{OpenSearch.GetMappedPublicPort(9200)}/");

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(Postgres.StartAsync(), OpenSearch.StartAsync()).ConfigureAwait(false);
        using var http = new HttpClient { BaseAddress = OpenSearchUri };
        using HttpResponseMessage created = await http.PutAsJsonAsync(IndexName, new
        {
            settings = new Dictionary<string, object>
            {
                ["index.number_of_shards"] = 2,
                ["index.number_of_replicas"] = 0,
                ["index.refresh_interval"] = "5s",
                ["index.translog.durability"] = "async",
            },
        }).ConfigureAwait(false);
        created.EnsureSuccessStatusCode();
        using HttpResponseMessage health = await http.GetAsync(new Uri($"_cluster/health/{IndexName}?wait_for_status=green&timeout=60s", UriKind.Relative)).ConfigureAwait(false);
        health.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync().ConfigureAwait(false);
        await OpenSearch.DisposeAsync().ConfigureAwait(false);
    }
}

[CollectionDefinition(Name)]
public sealed class LiveServicesGroup : ICollectionFixture<LiveServicesFixture>
{
    public const string Name = "live-services";
}

[Collection(LiveServicesGroup.Name)]
public sealed class CaptureIntegrationTests(LiveServicesFixture services)
{
    [Fact]
    public async Task Captures_postgres_opensearch_and_containers_and_flags_relaxed_durability()
    {
        EnvironmentManifest manifest = await new EnvironmentCapturer().CaptureAsync(Options(), TestContext.Current.CancellationToken);

        PostgresInstance pg = manifest.Postgres.Should().ContainSingle().Subject;
        pg.Role.Should().Be(PostgresRole.Primary);
        pg.ServerVersion.Should().StartWith(VersionsEnvFile.Load(VersionsEnvFile.Locate()!).Values["POSTGRES"]);
        pg.Endpoint.Should().EndWith("/opportunity").And.NotContain("bench-secret");
        pg.Durability.Fsync.Should().Be("off");
        pg.Durability.SynchronousCommit.Should().Be(pg.Settings["synchronous_commit"].Value);
        pg.Durability.FullPageWrites.Should().Be(pg.Settings["full_page_writes"].Value);
        pg.Settings["shared_buffers"].Should().Be(new PgSetting { Value = "8192", Unit = "8kB", Source = "command line" });
        pg.Settings.Should().ContainKeys("wal_level", "max_wal_size", "checkpoint_timeout", "work_mem", "autovacuum");

        OpenSearchCluster os = manifest.Opensearch!;
        os.Version.Should().Be(VersionsEnvFile.Load(VersionsEnvFile.Locate()!).Values["OPENSEARCH"]);
        OpenSearchNode node = os.Nodes.Should().ContainSingle().Subject;
        node.Jvm.HeapMaxBytes.Should().Be(512L * 1024 * 1024);
        node.Jvm.InputArguments.Should().Contain("-Xmx512m");
        node.Roles.Should().Contain("data");
        os.ClusterSettings.Defaults.Should().ContainKey("indices.memory.index_buffer_size");
        OpenSearchIndex index = os.Indices.Should().ContainSingle(i => i.Name == LiveServicesFixture.IndexName).Subject;
        index.PrimaryShards.Should().Be(2);
        index.Replicas.Should().Be(0);
        index.RefreshInterval.Should().Be("5s");
        index.TranslogDurability.Should().Be("async");
        index.Shards.Should().HaveCount(2).And.OnlyContain(s => s.Primary && s.State == "STARTED");

        ContainerInfo container = manifest.Containers!.Should().ContainSingle(c => c.Image.Reference.StartsWith("postgres:", StringComparison.Ordinal)).Subject;
        container.Pin.Should().NotBeNull();
        container.Pin!.Matches.Should().BeTrue("the container runs the versions.env digest");
        container.Command.Should().Contain("fsync=off");
        container.Environment?.Keys.Should().NotContain(k => k.Contains("PASSWORD", StringComparison.Ordinal));
        manifest.Containers!.Should().Contain(c => c.Image.Reference.StartsWith("opensearchproject/opensearch:", StringComparison.Ordinal)
            && c.Environment!["OPENSEARCH_JAVA_OPTS"] == "-Xms512m -Xmx512m");

        manifest.Hosts.Single().ContainerRuntime!.Name.Should().Be("docker");
        manifest.Durability.Mode.Should().Be(DurabilityMode.Relaxed);
        // Testcontainers' PostgreSQL module relaxes synchronous_commit/full_page_writes by default; whatever it set is reported.
        manifest.Durability.Deviations.Should().Contain(new DurabilityDeviation("postgres", "primary", "fsync", "off", "on"))
            .And.Contain(new DurabilityDeviation("opensearch", LiveServicesFixture.IndexName, "index.translog.durability", "async", "request"))
            .And.BeEquivalentTo(DurabilityPolicy.Evaluate(manifest));

        // Relaxed without a justification does not satisfy the schema (Q-05); with one it does.
        BundleSchemas.ValidateEnvironment(BundleSchemas.ToNode(manifest)).Should().NotBeEmpty();
        EnvironmentManifest justified = manifest with { Durability = manifest.Durability with { Justification = "nightly developer run (Q-05)" } };
        BundleSchemas.ValidateEnvironment(BundleSchemas.ToNode(justified)).Should().BeEmpty();
        BenchJson.Serialize(justified).Should().NotContain("bench-secret");
    }

    [Fact]
    public async Task Capture_env_command_refuses_unrecorded_relaxation_and_writes_a_valid_manifest_when_justified()
    {
        string output = Path.Combine(SampleBundle.NewDirectory(), "environment.json");
        string[] common =
        [
            "capture-env", "--postgres", "primary=env:BENCH_TEST_PG", "--opensearch", services.OpenSearchUri.ToString(),
            "--container", services.Postgres.Id, "--worker", "all=1", "--object-store", "FileSystem", "--out", output,
        ];

        (int refused, string refusedError) = await RunCliAsync(common);
        (int accepted, string acceptedError) = await RunCliAsync([.. common, "--relaxed-durability", "integration test of the relaxed nightly profile"]);

        refused.Should().Be(3, refusedError);
        refusedError.Should().Contain("fsync=off").And.Contain("index.translog.durability=async");
        accepted.Should().Be(0, acceptedError);
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken))!;
        BundleSchemas.ValidateEnvironment(manifest).Should().BeEmpty();
        manifest["workers"]![0]!["type"]!.GetValue<string>().Should().Be("all");
        manifest["durability"]!["mode"]!.GetValue<string>().Should().Be("relaxed");
    }

    private CaptureOptions Options() => new()
    {
        Profile = BenchmarkProfile.DeveloperRegression,
        Containers = [services.Postgres.Id, services.OpenSearch.Id],
        Postgres = [new PostgresTarget("primary", services.Postgres.GetConnectionString())],
        OpenSearch = services.OpenSearchUri,
    };

    private async Task<(int ExitCode, string StandardError)> RunCliAsync(string[] arguments)
    {
        string cli = Path.Combine(AppContext.BaseDirectory, "opportunity-bench.dll");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(cli);
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["BENCH_TEST_PG"] = services.Postgres.GetConnectionString();
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stderr);
    }
}
