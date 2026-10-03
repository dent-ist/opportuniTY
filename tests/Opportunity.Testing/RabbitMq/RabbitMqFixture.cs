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

    /// <summary>
    /// Stops the broker application (connections drop, the node keeps its data) for broker-restart tests. Every test of
    /// the collection shares the broker, so call <see cref="StartBrokerAsync"/> in a <c>finally</c>.
    /// </summary>
    public async Task StopBrokerAsync(CancellationToken cancellationToken = default) =>
        await RabbitMqCtlAsync(["stop_app"], cancellationToken).ConfigureAwait(false);

    /// <summary>Starts the broker application again and waits until it accepts connections.</summary>
    public async Task StartBrokerAsync(CancellationToken cancellationToken = default)
    {
        await RabbitMqCtlAsync(["start_app"], cancellationToken).ConfigureAwait(false);
        await RabbitMqCtlAsync(["await_startup"], cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> CreateUserAsync(
        string virtualHost, string configure, string write, string read, CancellationToken cancellationToken)
    {
        var name = TestIsolation.NewName("u");
        using (var created = await Management.PutAsJsonAsync(
            $"api/users/{name}", new { password = Password, tags = string.Empty }, cancellationToken).ConfigureAwait(false))
        {
            created.EnsureSuccessStatusCode();
        }

        using (var granted = await Management.PutAsJsonAsync(
            $"api/permissions/{Uri.EscapeDataString(virtualHost)}/{name}",
            new { configure, write, read },
            cancellationToken).ConfigureAwait(false))
        {
            granted.EnsureSuccessStatusCode();
        }

        return name;
    }

    internal async Task DeleteUserAsync(string name)
    {
        using var response = await Management.DeleteAsync(new Uri($"api/users/{name}", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal Uri AmqpUri(string virtualHost, string username, FaultProxy? via)
    {
        var host = via?.Host ?? Hostname;
        var port = via?.Port ?? AmqpPort;
        return new Uri($"amqp://{username}:{Password}@{host}:{port}/{Uri.EscapeDataString(virtualHost)}");
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

    private async Task RabbitMqCtlAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", .. arguments], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', arguments)} failed: {result.Stderr}");
        }
    }
}

/// <summary>A per-test virtual host. Disposing deletes it.</summary>
public sealed class RabbitMqVirtualHost : IAsyncDisposable
{
    private readonly RabbitMqFixture _fixture;
    private readonly List<string> _users = [];

    internal RabbitMqVirtualHost(RabbitMqFixture fixture, string name)
    {
        _fixture = fixture;
        Name = name;
    }

    public string Name { get; }

    public Uri AmqpUri => _fixture.AmqpUri(Name);

    public Uri AmqpUriVia(FaultProxy proxy) => _fixture.AmqpUri(Name, proxy);

    /// <summary>
    /// Creates a broker user limited to this virtual host with the given permission regexes (configure, write, read)
    /// and returns its AMQP URI, optionally through <paramref name="via"/>. Deleted with the virtual host.
    /// </summary>
    public async Task<Uri> CreateUserAsync(
        string configure, string write, string read, FaultProxy? via = null, CancellationToken cancellationToken = default)
    {
        var user = await _fixture.CreateUserAsync(Name, configure, write, read, cancellationToken).ConfigureAwait(false);
        _users.Add(user);
        return _fixture.AmqpUri(Name, user, via);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var user in _users)
        {
            await _fixture.DeleteUserAsync(user).ConfigureAwait(false);
        }

        await _fixture.DeleteVirtualHostAsync(Name).ConfigureAwait(false);
    }
}
