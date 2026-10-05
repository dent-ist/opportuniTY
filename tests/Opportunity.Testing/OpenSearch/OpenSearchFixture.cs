using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Opportunity.Testing.Images;
using Opportunity.Testing.Toxiproxy;

namespace Opportunity.Testing.OpenSearch;

/// <summary>
/// Single-node OpenSearch (version from <c>versions.env</c>) with the security plugin disabled and a 512 MB heap,
/// behind Toxiproxy. Each test gets a unique index-name prefix; disposing the scope deletes its indices.
/// </summary>
public class OpenSearchFixture : DependencyFixture
{
    private const string HostAlias = "opensearch";
    private const int Port = 9200;

    private readonly IContainer _container;
    private readonly HttpClient _admin = new() { Timeout = TimeSpan.FromSeconds(30) };

    public OpenSearchFixture()
    {
        _container = new ContainerBuilder(ContainerImages.OpenSearch)
            .WithNetwork(Network)
            .WithNetworkAliases(HostAlias)
            .WithPortBinding(Port, assignRandomHostPort: true)
            .WithEnvironment("discovery.type", "single-node")
            .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
            .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
            .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
            // Allow wildcard deletes for per-test cleanup, and keep shared CI disks from tripping read-only blocks.
            .WithEnvironment("action.destructive_requires_name", "false")
            .WithEnvironment("cluster.routing.allocation.disk.threshold_enabled", "false")
            // Reap expired point-in-time readers every second (default 1 min), so keep-alive expiry is testable (E10-T03).
            .WithEnvironment("search.keep_alive_interval", "1s")
            // ForPath escapes '?', so poll the plain health endpoint; it answers 200 once the node is up.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(Port)
                .ForPath("/_cluster/health")))
            .Build();
    }

    /// <summary>Direct REST endpoint.</summary>
    public Uri BaseAddress => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/");

    protected override string NetworkAlias => HostAlias;

    protected override int ServicePort => Port;

    /// <summary>REST endpoint routed through <paramref name="proxy"/> for fault injection.</summary>
    public static Uri BaseAddressVia(FaultProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return new Uri($"http://{proxy.Host}:{proxy.Port}/");
    }

    /// <summary>Reserves a unique index prefix for one test; dispose to delete every index under it.</summary>
    public OpenSearchIndexScope CreateIndexScope() => new(this, TestIsolation.NewName("t", '-'));

    internal async Task DeleteIndicesAsync(string prefix)
    {
        using var response = await _admin.DeleteAsync(
            new Uri(BaseAddress, $"{prefix}*?expand_wildcards=all&ignore_unavailable=true")).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    protected override IContainer DependencyContainer => _container;

    protected override Task StartDependencyAsync() => _container.StartAsync();

    protected override async ValueTask StopDependencyAsync()
    {
        _admin.Dispose();
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class OpenSearchIndexScope : IAsyncDisposable
{
    private readonly OpenSearchFixture _fixture;

    internal OpenSearchIndexScope(OpenSearchFixture fixture, string prefix)
    {
        _fixture = fixture;
        Prefix = prefix;
    }

    /// <summary>Lowercase prefix every index (and alias) of this test must start with.</summary>
    public string Prefix { get; }

    public string IndexName(string name) => $"{Prefix}-{name}";

    public async ValueTask DisposeAsync() => await _fixture.DeleteIndicesAsync(Prefix).ConfigureAwait(false);
}
