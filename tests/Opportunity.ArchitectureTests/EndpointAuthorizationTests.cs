using AwesomeAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Opportunity.Security.Authorization;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// E05-T02 / ADR-015 D5.4 PEP-1: every endpoint of the real API host declares its authorization. Workspace routes
/// (a <c>{workspaceId}</c> parameter) need a permission (<c>RequirePermission</c>) or an explicit membership-only
/// marker; other endpoints need an explicit <c>RequireAuthorization</c>. The authenticated fallback policy alone does
/// not count. <c>[AllowAnonymous]</c> is allowed only for the routes listed here.
/// </summary>
public sealed class EndpointAuthorizationTests
{
    private static readonly HashSet<string> AnonymousAllowList =
    [
        "/bff/login",                   // starts the OIDC flow
        "/bff/backchannel-logout",      // IdP-to-API, authenticated by the signed logout token
        "/openapi/{documentName}.json", // public API description
        "/health/live",
        "/health/ready",
    ];

    /// <summary>
    /// E20-T03: the only workspace routes a member reaches before accepting the workspace's acknowledgment text. Every other
    /// workspace route (content, search, coding, administration) is behind the gate.
    /// </summary>
    private static readonly HashSet<string> BeforeAcknowledgmentAllowList =
    [
        "GET /api/v1/workspaces/{workspaceId}/",
        "GET /api/v1/workspaces/{workspaceId}/acknowledgment",
        "POST /api/v1/workspaces/{workspaceId}/acknowledgment/acceptances",
    ];

    [Fact]
    public void Only_the_acknowledgment_routes_and_the_workspace_descriptor_skip_the_acknowledgment_gate()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:PublicOrigin", "https://localhost");
            builder.UseSetting("Authentication:Oidc:Authority", "https://idp.invalid");
            builder.UseSetting("Authentication:Oidc:ClientId", "architecture-test");
        });
        var exempt = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<AcknowledgmentExemptMetadata>() is not null)
            .Select(e => $"{string.Join(',', e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} /{e.RoutePattern.RawText?.TrimStart('/')}")
            .ToList();
        exempt.Should().BeEquivalentTo(BeforeAcknowledgmentAllowList);
    }

    [Fact]
    public void Every_endpoint_declares_its_authorization()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:PublicOrigin", "https://localhost");
            builder.UseSetting("Authentication:Oidc:Authority", "https://idp.invalid");
            builder.UseSetting("Authentication:Oidc:ClientId", "architecture-test");
        });
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.Should().NotBeEmpty();

        var offenders = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var route = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            var metadata = endpoint.Metadata;
            if (metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                if (!AnonymousAllowList.Contains(route))
                {
                    offenders.Add($"{route}: AllowAnonymous but not on the allow-list");
                }

                continue;
            }

            if (WorkspaceAuthorizationConventions.IsWorkspaceScoped(endpoint))
            {
                if (metadata.GetMetadata<RequiredPermissionMetadata>() is null && metadata.GetMetadata<WorkspaceMembershipMetadata>() is null
                    && metadata.GetMetadata<BreakGlassHolderMetadata>() is null)
                {
                    offenders.Add($"{route}: workspace endpoint without RequirePermission, RequireWorkspaceMember or RequireBreakGlassHolder");
                }

                continue;
            }

            if (metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0 && metadata.GetMetadata<RequiredPermissionMetadata>() is null)
            {
                offenders.Add($"{route}: no explicit authorization policy (the fallback policy is not a declaration)");
            }
        }

        offenders.Should().BeEmpty();
        endpoints.Select(e => "/" + e.RoutePattern.RawText?.TrimStart('/'))
            .Should().Contain(r => r.StartsWith("/api/v1/workspaces/{workspaceId}", StringComparison.Ordinal), "the scan sees the workspace routes");
    }
}
