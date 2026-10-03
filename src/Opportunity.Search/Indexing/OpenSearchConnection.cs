using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Opportunity.Search.Indexing;

/// <summary>
/// Thin REST client for the handful of index-management calls (no vendor SDK: the surface is small and stable, and
/// the JSON bodies mirror the OpenSearch documentation one to one). Owned by <c>Opportunity.Search</c> only.
/// </summary>
internal sealed class OpenSearchConnection : IDisposable
{
    private readonly HttpClient _http;

    public OpenSearchConnection(OpenSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = options.Endpoint,
            Timeout = options.RequestTimeout,
        };
        if (!string.IsNullOrEmpty(options.Username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    /// <summary>
    /// Sends a request; returns the parsed body (null when empty) for 2xx and for any status in
    /// <paramref name="accept"/>, otherwise throws <see cref="OpenSearchRequestException"/>.
    /// </summary>
    public async Task<OpenSearchResponse> SendAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, params HttpStatusCode[] accept)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var json = string.IsNullOrWhiteSpace(text) || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) != true
            ? null
            : JsonNode.Parse(text);
        if (response.IsSuccessStatusCode || accept.Contains(response.StatusCode))
        {
            return new OpenSearchResponse(response.StatusCode, json);
        }

        throw new OpenSearchRequestException(method, path, response.StatusCode, ErrorType(json), Reason(json) ?? Truncate(text));
    }

    public void Dispose() => _http.Dispose();

    internal static string? ErrorType(JsonNode? body) =>
        body?["error"] is JsonObject error ? error["type"]?.GetValue<string>() : null;

    private static string? Reason(JsonNode? body) =>
        body?["error"] is JsonObject error ? error["reason"]?.GetValue<string>() : null;

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}

internal sealed record OpenSearchResponse(HttpStatusCode Status, JsonNode? Body);

/// <summary>An OpenSearch call that failed. The message carries the error type and reason, never request bodies.</summary>
public sealed class OpenSearchRequestException : Exception
{
    public OpenSearchRequestException()
    {
    }

    public OpenSearchRequestException(string message)
        : base(message)
    {
    }

    public OpenSearchRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal OpenSearchRequestException(HttpMethod method, string path, HttpStatusCode status, string? errorType, string? reason)
        : base($"OpenSearch {method} {path} failed with {(int)status} {errorType}: {reason}")
    {
        Status = status;
        ErrorType = errorType;
    }

    public HttpStatusCode Status { get; }

    public string? ErrorType { get; }
}

internal static class JsonBodies
{
    public static JsonObject Obj(params (string Key, JsonNode? Value)[] properties)
    {
        var o = new JsonObject();
        foreach (var (key, value) in properties)
        {
            o[key] = value;
        }

        return o;
    }

    public static string Escape(string segment) => Uri.EscapeDataString(segment);
}
