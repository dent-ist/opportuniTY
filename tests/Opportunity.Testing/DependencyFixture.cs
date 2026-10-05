using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

using Opportunity.Testing.Toxiproxy;

namespace Opportunity.Testing;

/// <summary>
/// Base for a fixture that owns one dependency container plus a Toxiproxy container on a private Docker network.
/// Use as an xUnit collection (or assembly) fixture: containers start once and are shared by the tests of the
/// collection; each test isolates itself through the fixture's per-test scope methods.
/// </summary>
public abstract class DependencyFixture : IAsyncLifetime
{
    private readonly INetwork _network;
    private readonly ToxiproxyHost _toxiproxy;

    protected DependencyFixture()
    {
        _network = new NetworkBuilder().WithName($"opportunity-test-{TestIsolation.NewId()}").Build();
        _toxiproxy = new ToxiproxyHost(_network);
    }

    protected INetwork Network => _network;

    /// <summary>Alias of the dependency on the fixture network (the upstream Toxiproxy dials).</summary>
    protected abstract string NetworkAlias { get; }

    /// <summary>Container-side port of the dependency's main protocol.</summary>
    protected abstract int ServicePort { get; }

    /// <summary>
    /// Creates a dedicated Toxiproxy proxy in front of the dependency. Faults injected on it affect only clients
    /// connected through it. Dispose it at the end of the test.
    /// </summary>
    public Task<FaultProxy> CreateFaultProxyAsync(CancellationToken cancellationToken = default) =>
        _toxiproxy.CreateProxyAsync($"{NetworkAlias}:{ServicePort}", cancellationToken);

    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync().ConfigureAwait(false);
        await Task.WhenAll(_toxiproxy.StartAsync(), StartDependencyAsync()).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_toxiproxy.DisposeAsync().AsTask(), StopDependencyAsync().AsTask()).ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>The dependency's container (fault tests pause, restart or exec into it).</summary>
    protected abstract IContainer DependencyContainer { get; }

    /// <summary>Freezes the dependency (<c>docker pause</c>): connections stay open, nothing is answered.</summary>
    public Task PauseAsync(CancellationToken cancellationToken = default) => DependencyContainer.PauseAsync(cancellationToken);

    public Task UnpauseAsync(CancellationToken cancellationToken = default) => DependencyContainer.UnpauseAsync(cancellationToken);

    /// <summary>
    /// Stops and starts the dependency's container (its data survives; its host port may change, so clients that must
    /// survive the restart connect through <see cref="CreateFaultProxyAsync"/>, whose upstream is the network alias).
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await DependencyContainer.StopAsync(cancellationToken).ConfigureAwait(false);
        await DependencyContainer.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a command inside the dependency's container.</summary>
    public Task<ExecResult> ExecAsync(IList<string> command, CancellationToken cancellationToken = default) =>
        DependencyContainer.ExecAsync(command, cancellationToken);

    protected abstract Task StartDependencyAsync();

    protected abstract ValueTask StopDependencyAsync();
}
