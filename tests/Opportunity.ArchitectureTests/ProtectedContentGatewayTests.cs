using System.Reflection;

using AwesomeAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using NetArchTest.Rules;

using Opportunity.Api.Content;
using Opportunity.Security.Authorization;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// E05-T04 / ADR-015 D12.1: no API route returns object-storage content or URLs except through the protected-content
/// gateway. Statically, only the gateway namespace of the API (and no other host-facing assembly) can reach object
/// storage or sign URLs; at runtime, every endpoint that answers with anything but JSON is a gateway endpoint.
/// </summary>
public sealed class ProtectedContentGatewayTests
{
    private const string GatewayNamespace = "Opportunity.Api.Content";

    private static readonly string[] StorageAccess =
    [
        "Opportunity.Application.Storage.IObjectStore",
        "Opportunity.Application.Storage.IObjectUrlSigner",
        "Opportunity.Application.Storage.PresignedObjectUrl",
        "Opportunity.Storage",
    ];

    [Fact]
    public void Only_the_gateway_namespace_of_the_api_reads_object_storage_or_signs_urls()
    {
        var result = Types.InAssembly(typeof(ProtectedContentGateway).Assembly)
            .That().DoNotResideInNamespace(GatewayNamespace)
            .ShouldNot().HaveDependencyOnAny(StorageAccess)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "API code outside {0} must not touch object storage; offending types: {1}",
            GatewayNamespace,
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData("Opportunity.Security")]
    [InlineData("Opportunity.Hosting")]
    public void Other_assemblies_that_map_endpoints_do_not_touch_object_storage(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(assemblyName)).ShouldNot().HaveDependencyOnAny(StorageAccess).GetResult();
        result.IsSuccessful.Should().BeTrue("offending types: {0}", string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Every_endpoint_that_returns_non_json_content_is_a_gateway_endpoint()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:PublicOrigin", "https://localhost");
            builder.UseSetting("Authentication:Oidc:Authority", "https://idp.invalid");
            builder.UseSetting("Authentication:Oidc:ClientId", "architecture-test");
        });
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var offenders = new List<string>();
        var gateway = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var route = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            var isGateway = endpoint.Metadata.GetMetadata<ProtectedContentEndpointMetadata>() is not null;
            var contentTypes = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>()
                .Where(m => m.StatusCode is >= 200 and < 300)
                .SelectMany(m => m.ContentTypes)
                .ToList();
            if (!isGateway && contentTypes.Any(t => !IsJson(t)))
            {
                offenders.Add($"{route}: returns {string.Join(", ", contentTypes)} outside the protected-content gateway");
            }

            if (isGateway)
            {
                gateway.Add(route);
                if (!WorkspaceAuthorizationConventions.IsWorkspaceScoped(endpoint))
                {
                    offenders.Add($"{route}: gateway endpoint outside a workspace");
                }

                // Document content declares the per-document permission the gateway decides; export packages declare the
                // workspace permission PEP-1 enforces (Export.Download).
                if (endpoint.Metadata.GetMetadata<DocumentPermissionMetadata>() is not { Permissions.Count: > 0 }
                    && endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>() is null)
                {
                    offenders.Add($"{route}: gateway endpoint without a declared document permission");
                }
            }
        }

        offenders.Should().BeEmpty();
        gateway.Should().Contain(
        [
            "/api/v1/workspaces/{workspaceId}/documents/{documentId}",
            "/api/v1/workspaces/{workspaceId}/documents/{documentId}/pages",
            "/api/v1/workspaces/{workspaceId}/documents/{documentId}/text/chunks/{chunkIndex}",
            "/api/v1/workspaces/{workspaceId}/documents/{documentId}/native",
            "/api/v1/workspaces/{workspaceId}/documents/{documentId}/text",
            "/api/v1/workspaces/{workspaceId}/documents/{documentId}/pages/{pageNumber}/image",
            "/api/v1/workspaces/{workspaceId}/documents/{documentId}/pages/{pageNumber}/thumbnail",
            "/api/v1/workspaces/{workspaceId}/exports/{exportId}/files/{fileId}/content",
            "/api/v1/workspaces/{workspaceId}/exports/{exportId}/package",
            "/api/v1/workspaces/{workspaceId}/imports/{importId}/report",
            "/api/v1/workspaces/{workspaceId}/imports/{importId}/report.csv",
            "/api/v1/workspaces/{workspaceId}/imports/{importId}/error-file",
            "/api/v1/workspaces/{workspaceId}/imports/preflight/{preflightId}/issues",
        ]);
    }

    private static bool IsJson(string contentType) =>
        contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("application/problem+json", StringComparison.OrdinalIgnoreCase)
        || contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
}
