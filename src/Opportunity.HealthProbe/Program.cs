// Usage: dotnet Opportunity.HealthProbe.dll [url]   (default http://127.0.0.1:8080/health/live)
// Exit codes: 0 healthy (2xx), 1 unhealthy or unreachable, 2 invalid URL.
const string DefaultUrl = "http://127.0.0.1:8080/health/live";

var target = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("HEALTHPROBE_URL") ?? DefaultUrl;
if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
{
    await Console.Error.WriteLineAsync($"healthprobe: invalid URL '{target}'").ConfigureAwait(false);
    return 2;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
try
{
    using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
    return response.IsSuccessStatusCode ? 0 : 1;
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
{
    await Console.Error.WriteLineAsync($"healthprobe: {ex.Message}").ConfigureAwait(false);
    return 1;
}
