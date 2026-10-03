using System.Globalization;

namespace Opportunity.Testing.Toxiproxy;

/// <summary>
/// A dedicated Toxiproxy proxy in front of one dependency. Point a client at <see cref="Host"/>:<see cref="Port"/>
/// and inject faults; faults affect only clients of this proxy, so tests can run them in parallel.
/// Disposing deletes the proxy (and with it every toxic).
/// </summary>
public sealed class FaultProxy : IAsyncDisposable
{
    private readonly ToxiproxyClient _client;
    private readonly Action _release;
    private int _toxicCounter;
    private int _disposed;

    internal FaultProxy(ToxiproxyClient client, string name, string host, int port, Action release)
    {
        _client = client;
        Name = name;
        Host = host;
        Port = port;
        _release = release;
    }

    public string Name { get; }

    /// <summary>Host the test process uses to reach the proxy.</summary>
    public string Host { get; }

    /// <summary>Host-mapped port of the proxy listener.</summary>
    public int Port { get; }

    /// <summary>Delays every chunk of data by <paramref name="latency"/> (± <paramref name="jitter"/>).</summary>
    public Task<Toxic> AddLatencyAsync(
        TimeSpan latency,
        TimeSpan jitter = default,
        ToxicDirection direction = ToxicDirection.Downstream,
        CancellationToken cancellationToken = default) =>
        AddToxicAsync(
            "latency",
            direction,
            new Dictionary<string, long>
            {
                ["latency"] = (long)latency.TotalMilliseconds,
                ["jitter"] = (long)jitter.TotalMilliseconds,
            },
            cancellationToken);

    /// <summary>
    /// Stops all data and closes the connection after <paramref name="timeout"/>;
    /// <see cref="TimeSpan.Zero"/> holds the connection open without delivering data until the toxic is removed.
    /// </summary>
    public Task<Toxic> AddTimeoutAsync(
        TimeSpan timeout,
        ToxicDirection direction = ToxicDirection.Upstream,
        CancellationToken cancellationToken = default) =>
        AddToxicAsync(
            "timeout",
            direction,
            new Dictionary<string, long> { ["timeout"] = (long)timeout.TotalMilliseconds },
            cancellationToken);

    /// <summary>Resets connections with a TCP RST once <paramref name="after"/> has elapsed since data arrived.</summary>
    public Task<Toxic> AddResetPeerAsync(
        TimeSpan after = default,
        ToxicDirection direction = ToxicDirection.Upstream,
        CancellationToken cancellationToken = default) =>
        AddToxicAsync(
            "reset_peer",
            direction,
            new Dictionary<string, long> { ["timeout"] = (long)after.TotalMilliseconds },
            cancellationToken);

    /// <summary>Cuts the link: closes open connections and refuses new ones until <see cref="RestoreAsync"/>.</summary>
    public Task CutAsync(CancellationToken cancellationToken = default) =>
        _client.SetEnabledAsync(Name, enabled: false, cancellationToken);

    public Task RestoreAsync(CancellationToken cancellationToken = default) =>
        _client.SetEnabledAsync(Name, enabled: true, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _client.DeleteProxyAsync(Name).ConfigureAwait(false);
        }
        finally
        {
            _release();
        }
    }

    private async Task<Toxic> AddToxicAsync(
        string type,
        ToxicDirection direction,
        IReadOnlyDictionary<string, long> attributes,
        CancellationToken cancellationToken)
    {
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{type}_{direction.ToString().ToLowerInvariant()}_{Interlocked.Increment(ref _toxicCounter)}");
        await _client.AddToxicAsync(Name, name, type, direction, attributes, cancellationToken).ConfigureAwait(false);
        return new Toxic(this, name);
    }

    internal Task RemoveToxicAsync(string toxicName) => _client.RemoveToxicAsync(Name, toxicName);
}

/// <summary>An active toxic; dispose (or <see cref="RemoveAsync"/>) to lift the fault.</summary>
public sealed class Toxic : IAsyncDisposable
{
    private readonly FaultProxy _proxy;

    internal Toxic(FaultProxy proxy, string name)
    {
        _proxy = proxy;
        Name = name;
    }

    public string Name { get; }

    public Task RemoveAsync() => _proxy.RemoveToxicAsync(Name);

    public async ValueTask DisposeAsync() => await RemoveAsync().ConfigureAwait(false);
}
