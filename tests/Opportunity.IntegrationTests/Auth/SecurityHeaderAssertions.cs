using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Opportunity.IntegrationTests.Auth;

/// <summary>ADR-015 D4.4–D4.6 header checks shared by the API and web-image tests.</summary>
internal static partial class SecurityHeaderAssertions
{
    public static void AssertApiHeaders(HttpResponseMessage response)
    {
        var headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        AssertCommon(headers);
        Directives(headers["Content-Security-Policy"])["default-src"].Should().Equal("'none'");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("API responses are never cached (D4.5)");
    }

    /// <summary>HSTS, nosniff, referrer, COOP/CORP, permissions policy and a CSP that forbids framing and inline script.</summary>
    public static void AssertCommon(IReadOnlyDictionary<string, string> headers)
    {
        headers["Strict-Transport-Security"].Should().Be("max-age=31536000; includeSubDomains");
        headers["X-Content-Type-Options"].Should().Be("nosniff");
        headers["Referrer-Policy"].Should().Be("no-referrer");
        headers["Cross-Origin-Opener-Policy"].Should().Be("same-origin");
        headers["Cross-Origin-Resource-Policy"].Should().Be("same-origin");
        headers["Permissions-Policy"].Should().Contain("camera=()").And.Contain("microphone=()").And.Contain("geolocation=()").And.Contain("payment=()");

        var csp = Directives(headers["Content-Security-Policy"]);
        csp["frame-ancestors"].Should().Equal("'none'");
        var scripts = csp.TryGetValue("script-src", out var scriptSrc) ? scriptSrc : csp["default-src"];
        scripts.Should().NotContain("'unsafe-inline'").And.NotContain("'unsafe-eval'").And.NotContain("*");
        csp["base-uri"].Should().Equal("'none'");
    }

    public static Dictionary<string, string[]> Directives(string policy) =>
        policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(d => d[0], d => d[1..], StringComparer.Ordinal);

    /// <summary>Parses <c>add_header Name "value" always;</c> lines of an nginx include.</summary>
    public static Dictionary<string, string> NginxHeaders(string config) =>
        AddHeader().Matches(config).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("""^\s*add_header\s+(?<name>[A-Za-z-]+)\s+"(?<value>[^"]*)"\s+always;""", RegexOptions.Multiline)]
    private static partial Regex AddHeader();
}
