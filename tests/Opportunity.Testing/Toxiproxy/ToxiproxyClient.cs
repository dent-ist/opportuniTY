using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Opportunity.Testing.Toxiproxy;

/// <summary>Minimal client for the Toxiproxy HTTP API (https://github.com/Shopify/toxiproxy#http-api).</summary>
public sealed class ToxiproxyClient : IDisposable
{
    private readonly HttpClient _http;

    public ToxiproxyClient(Uri apiBaseAddress)
    {
        _http = new HttpClient { BaseAddress = apiBaseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task CreateProxyAsync(string name, string listen, string upstream, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "proxies",
            new { name, listen, upstream, enabled = true },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProxyAsync(string name, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(ProxyPath(name), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Disabling a proxy closes its open connections and refuses new ones.</summary>
    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(ProxyPath(name), new { enabled }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddToxicAsync(
        string proxy,
        string toxicName,
        string type,
        ToxicDirection direction,
        IReadOnlyDictionary<string, long> attributes,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            name = toxicName,
            type,
            stream = direction == ToxicDirection.Upstream ? "upstream" : "downstream",
            toxicity = 1.0,
            attributes,
        };
        using var response = await _http.PostAsJsonAsync($"{ProxyPath(proxy)}/toxics", body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveToxicAsync(string proxy, string toxicName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(
            $"{ProxyPath(proxy)}/toxics/{Uri.EscapeDataString(toxicName)}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();

    private static string ProxyPath(string name) => "proxies/" + Uri.EscapeDataString(name);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                string.Create(CultureInfo.InvariantCulture, $"Toxiproxy {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} failed with {(int)response.StatusCode}: {body}"),
                inner: null,
                response.StatusCode);
        }
    }
}

public enum ToxicDirection
{
    /// <summary>Server to client (responses).</summary>
    Downstream,

    /// <summary>Client to server (requests).</summary>
    Upstream,
}
