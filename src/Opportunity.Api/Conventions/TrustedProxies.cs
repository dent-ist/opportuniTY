using System.Net;

using Microsoft.AspNetCore.HttpOverrides;

namespace Opportunity.Api.Conventions;

/// <summary>
/// Configuration section <c>ForwardedHeaders</c>: the reverse proxies whose <c>X-Forwarded-For</c> names the client.
/// Empty (the default) trusts no proxy, and audit records the connection's address (E14-T02, ADR-013 §4).
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Proxy addresses, e.g. <c>10.0.4.2</c>.</summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Proxy networks in CIDR notation, e.g. <c>10.0.4.0/24</c>.</summary>
    public IList<string> KnownNetworks { get; } = [];
}

internal static class TrustedProxies
{
    /// <summary>
    /// Replaces the connection's remote address with the client's from the last <c>X-Forwarded-For</c> hop when the
    /// connection comes from a configured proxy (one hop only; scheme and host are not taken from headers). A malformed
    /// entry fails startup.
    /// </summary>
    public static WebApplication UseTrustedProxies(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var configured = new TrustedProxyOptions();
        app.Configuration.GetSection(TrustedProxyOptions.SectionName).Bind(configured);
        if (configured.KnownProxies.Count == 0 && configured.KnownNetworks.Count == 0)
        {
            return app;
        }

        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in configured.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in configured.KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }

        app.UseForwardedHeaders(options);
        return app;
    }
}
