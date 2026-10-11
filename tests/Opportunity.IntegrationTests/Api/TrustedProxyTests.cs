using System.Net;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Opportunity.Api.Conventions;

namespace Opportunity.IntegrationTests.Api;

/// <summary>
/// E14-T02: behind a configured reverse proxy the client address (what audit events record) is the last
/// <c>X-Forwarded-For</c> hop; any other sender's header is ignored, and nothing is trusted unless configured.
/// </summary>
public sealed class TrustedProxyTests
{
    private const string Echo = "/api/v1/workspaces/0199a8a0-0000-7000-8000-0000000000a1/test-client-address";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("10.4.0.2", "10.4.0.0/24", "", "198.51.100.23")]
    [InlineData("10.4.0.2", "", "10.4.0.2", "198.51.100.23")]
    [InlineData("10.9.0.2", "10.4.0.0/24", "", "10.9.0.2")]
    [InlineData("10.4.0.2", "", "", "10.4.0.2")]
    public async Task The_client_address_comes_from_x_forwarded_for_only_through_a_configured_proxy(
        string connection, string network, string proxy, string expected)
    {
        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            if (network.Length > 0)
            {
                builder.UseSetting("ForwardedHeaders:KnownNetworks:0", network);
            }

            if (proxy.Length > 0)
            {
                builder.UseSetting("ForwardedHeaders:KnownProxies:0", proxy);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new ConnectionFrom(IPAddress.Parse(connection)));
                services.AddSingleton<IApiEndpointModule, EchoClientAddress>();
            });
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Echo);
        request.Headers.Add("X-Forwarded-For", "203.0.113.9, 198.51.100.23");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be(expected);
    }

    private sealed class ConnectionFrom(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class EchoClientAddress : IApiEndpointModule
    {
        public void MapEndpoints(ApiRouteGroups routes) =>
            routes.MemberOnly().MapGet("/test-client-address", (HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "none");
    }
}
