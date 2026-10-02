using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using DotNet.Testcontainers.Builders;

using Opportunity.Testing.Images;
using Opportunity.Testing.Toxiproxy;

using Testcontainers.RabbitMq;

namespace Opportunity.Testing.RabbitMq;

/// <summary>
/// RabbitMQ (version from <c>versions.env</c>) behind Toxiproxy. Each test gets its own virtual host, created and
/// deleted through the management API, so queues and exchanges never collide.
/// </summary>
public class RabbitMqFixture : DependencyFixture
{
    public const string Username = "opportunity";
    public const string Password = "opportunity";

    private const string HostAlias = "rabbitmq";
    private const int Port = 5672;
    private const int ManagementPort = 15672;

    private readonly RabbitMqContainer _container;
    private HttpClient? _management;

    public RabbitMqFixture()
    {
        _container = new RabbitMqBuilder(ContainerImages.RabbitMq)
            .WithNetwork(Network)
            .WithNetworkAliases(HostAlias)
            .WithUsername(Username)
            .WithPassword(Password)
            // The plain image ships the management plugin disabled; it gives fast vhost create/delete over HTTP.
            .WithResourceMapping(Encoding.ASCII.GetBytes("[rabbitmq_management]."), "/etc/rabbitmq/enabled_plugins")
            .WithPortBinding(ManagementPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(ManagementPort)
                .ForPath("/api/overview")
                .WithBasicAuthentication(Username, Password)))
            .Build();
    }

    public string Hostname => _container.Hostname;

    public int AmqpPort => _container.GetMappedPublicPort(Port);

    protected override string NetworkAlias => HostAlias;

    protected override int ServicePort => Port;

    /// <summary>AMQP URI for <paramref name="virtualHost"/>, optionally routed through a fault proxy.</summary>
    public Uri AmqpUri(string virtualHost, FaultProxy? via = null)
    {
        var host = via?.Host ?? Hostname;
        var port = via?.Port ?? AmqpPort;
        return new Uri($"amqp://{Username}:{Password}@{host}:{port}/{Uri.EscapeDataString(virtualHost)}");
    }

    /// <summary>Creates an isolated virtual host for one test; dispose to delete it with everything in it.</summary>
    public async Task<RabbitMqVirtualHost> CreateVirtualHostAsync(CancellationToken cancellationToken = default)
    {
        var name = TestIsolation.NewName("t");
        var path = Uri.EscapeDataString(name);
        using (var created = await Management.PutAsync(new Uri($"api/vhosts/{path}", UriKind.Relative), null, cancellationToken).ConfigureAwait(false))
        {
            created.EnsureSuccessStatusCode();
        }

        using (var granted = await Management.PutAsJsonAsync(
            $"api/permissions/{path}/{Username}",
            new { configure = ".*", write = ".*", read = ".*" },
            cancellationToken).ConfigureAwait(false))
        {
            granted.EnsureSuccessStatusCode();
        }

        return new RabbitMqVirtualHost(this, name);
    }

    internal async Task DeleteVirtualHostAsync(string name)
    {
        using var response = await Management.DeleteAsync(
            new Uri($"api/vhosts/{Uri.EscapeDataString(name)}", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task StartDependencyAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        _management = new HttpClient
        {
            BaseAddress = new Uri($"http://{Hostname}:{_container.GetMappedPublicPort(ManagementPort)}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{Username}:{Password}")));
    }

    protected override async ValueTask StopDependencyAsync()
    {
        _management?.Dispose();
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    private HttpClient Management => _management ?? throw new InvalidOperationException("RabbitMQ has not been started.");
}

/// <summary>A per-test virtual host. Disposing deletes it.</summary>
public sealed class RabbitMqVirtualHost : IAsyncDisposable
{
    private readonly RabbitMqFixture _fixture;

    internal RabbitMqVirtualHost(RabbitMqFixture fixture, string name)
    {
        _fixture = fixture;
        Name = name;
    }

    public string Name { get; }

    public Uri AmqpUri => _fixture.AmqpUri(Name);

    public Uri AmqpUriVia(FaultProxy proxy) => _fixture.AmqpUri(Name, proxy);

    public async ValueTask DisposeAsync() => await _fixture.DeleteVirtualHostAsync(Name).ConfigureAwait(false);
}
