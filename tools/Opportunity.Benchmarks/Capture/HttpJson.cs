using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Opportunity.Benchmarks.Capture;

/// <summary>GET-JSON helper for REST endpoints given as URLs that may carry <c>user:password@</c> (never recorded).</summary>
internal sealed class HttpJson : IDisposable
{
    private readonly HttpClient _client;

    public HttpJson(Uri endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var builder = new UriBuilder(endpoint) { UserName = string.Empty, Password = string.Empty };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        BaseAddress = builder.Uri;
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            string credentials = Uri.UnescapeDataString(endpoint.UserInfo);
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
    }

    /// <summary>A plain client for <paramref name="endpoint"/> (credentials in the URL become a Basic header).</summary>
    public static HttpClient CreateClient(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var builder = new UriBuilder(endpoint) { UserName = string.Empty, Password = string.Empty };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        var client = new HttpClient { BaseAddress = builder.Uri, Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            string credentials = Uri.UnescapeDataString(endpoint.UserInfo);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        return client;
    }

    /// <summary>The endpoint without credentials; safe to record.</summary>
    public Uri BaseAddress { get; }

    public string DisplayEndpoint => $"{BaseAddress.Scheme}://{BaseAddress.Authority}";

    public async Task<JsonElement> GetAsync(string relative, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(BaseAddress, relative), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {DisplayEndpoint}/{relative} returned {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
    }

    public void Dispose() => _client.Dispose();
}
