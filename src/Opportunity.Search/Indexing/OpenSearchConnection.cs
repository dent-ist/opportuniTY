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
        : this(options, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>Over <paramref name="handler"/> (tests record or fault requests); the connection owns it.</summary>
    public OpenSearchConnection(OpenSearchOptions options, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        options.Validate();
        _http = new HttpClient(handler)
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

    /// <summary>
    /// Sends an NDJSON body made of <paramref name="lines"/> (each already newline-terminated) without concatenating
    /// them, and returns status and parsed body whatever the status: the bulk writer classifies every outcome itself.
    /// </summary>
    public async Task<OpenSearchResponse> SendNdjsonAsync(string path, IReadOnlyList<byte[]> lines, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new NdjsonContent(lines) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(text) && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.Ordinal) == true)
        {
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                // A truncated or non-JSON error page: the status alone classifies it.
            }
        }

        return new OpenSearchResponse(response.StatusCode, json);
    }

    public void Dispose() => _http.Dispose();

    private sealed class NdjsonContent : HttpContent
    {
        private readonly IReadOnlyList<byte[]> _lines;
        private readonly long _length;

        public NdjsonContent(IReadOnlyList<byte[]> lines)
        {
            _lines = lines;
            _length = lines.Sum(l => (long)l.Length);
            Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            foreach (var line in _lines)
            {
                await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

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
