using System.Globalization;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

using Opportunity.Testing.Images;

namespace Opportunity.Testing.Toxiproxy;

/// <summary>
/// A Toxiproxy container on a fixture's Docker network with a pool of pre-published listener ports,
/// so proxies can be created and removed while the container runs.
/// </summary>
public sealed class ToxiproxyHost : IAsyncDisposable
{
    public const int ApiPort = 8474;
    public const int FirstProxyPort = 8666;
    public const int DefaultPoolSize = 16;

    private readonly IContainer _container;
    private readonly int _poolSize;
    private readonly Lock _gate = new();
    private readonly bool[] _inUse;
    private ToxiproxyClient? _client;

    public ToxiproxyHost(INetwork network, string networkAlias = "toxiproxy", int poolSize = DefaultPoolSize)
    {
        _poolSize = poolSize;
        _inUse = new bool[poolSize];

        var builder = new ContainerBuilder(ContainerImages.Toxiproxy)
            .WithNetwork(network)
            .WithNetworkAliases(networkAlias)
            .WithPortBinding(ApiPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ApiPort).ForPath("/version")));
        for (var i = 0; i < poolSize; i++)
        {
            builder = builder.WithPortBinding(FirstProxyPort + i, assignRandomHostPort: true);
        }

        _container = builder.Build();
    }

    public ToxiproxyClient Client => _client ?? throw new InvalidOperationException("Toxiproxy has not been started.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);
        _client = new ToxiproxyClient(new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}/"));
    }

    /// <summary>Creates a proxy to <paramref name="upstream"/> (<c>alias:port</c> on the fixture network).</summary>
    public async Task<FaultProxy> CreateProxyAsync(string upstream, CancellationToken cancellationToken = default)
    {
        var slot = Reserve();
        var listenPort = FirstProxyPort + slot;
        var name = string.Create(CultureInfo.InvariantCulture, $"p{listenPort}_{Guid.NewGuid():N}");
        try
        {
            await Client.CreateProxyAsync(name, $"0.0.0.0:{listenPort}", upstream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(slot);
            throw;
        }

        return new FaultProxy(Client, name, _container.Hostname, _container.GetMappedPublicPort(listenPort), () => Release(slot));
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    private int Reserve()
    {
        lock (_gate)
        {
            for (var i = 0; i < _poolSize; i++)
            {
                if (!_inUse[i])
                {
                    _inUse[i] = true;
                    return i;
                }
            }
        }

        throw new InvalidOperationException(
            $"All {_poolSize} Toxiproxy listener ports are in use; dispose FaultProxy instances when a test finishes.");
    }

    private void Release(int slot)
    {
        lock (_gate)
        {
            _inUse[slot] = false;
        }
    }
}
