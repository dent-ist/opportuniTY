using Microsoft.Net.Http.Headers;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>
/// A minimal browser over an in-memory API client: keeps its own cookie jar (so tests can inspect and tamper with
/// cookies), never follows redirects, and can send the anti-forgery header and <c>Origin</c> like the Angular app.
/// </summary>
public sealed class TestBrowser(HttpClient client) : IDisposable
{
    public const string Origin = "https://localhost";

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    public HttpResponseMessage? LastResponse { get; private set; }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    /// <summary>An unsafe request; by default with the same <c>Origin</c> and the XSRF header, as Angular sends it.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, HttpContent? content = null, bool withXsrf = true, string? origin = Origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (withXsrf && Cookies.TryGetValue("__Host-opp-xsrf", out var token))
        {
            request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
        }

        return SendAsync(request);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        foreach (var cookie in SetCookies(response))
        {
            if (string.IsNullOrEmpty(cookie.Value.Value) || cookie.Expires < DateTimeOffset.UtcNow)
            {
                Cookies.Remove(cookie.Name.Value!);
            }
            else
            {
                Cookies[cookie.Name.Value!] = cookie.Value.Value!;
            }
        }

        LastResponse = response;
        request.Dispose();
        return response;
    }

    public static IList<SetCookieHeaderValue> SetCookies(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues(HeaderNames.SetCookie, out var values)
            && SetCookieHeaderValue.TryParseList(values.ToList(), out var parsed)
                ? parsed
                : [];
    }

    public static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    public void Dispose() => client.Dispose();
}
